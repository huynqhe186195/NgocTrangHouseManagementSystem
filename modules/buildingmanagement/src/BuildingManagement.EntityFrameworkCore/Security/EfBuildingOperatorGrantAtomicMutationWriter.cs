using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// UNREGISTERED EF candidate. Requires a relational DbContext with the
/// proposed BuildingOperatorAccessGrant model mapped. Every mutation opens
/// a SERIALIZABLE transaction, rechecks issuer membership and active building
/// in that SAME transaction, then inserts/revokes a durable audit row.
/// Real ABP permission and account checks remain in the application preflight.
/// Never register for production until those are backed by real Identity and
/// DB-role/revocation tests. Does not authorize from a client-supplied issuer.
/// </summary>
public sealed class EfBuildingOperatorGrantAtomicMutationWriter :
    IBuildingOperatorGrantMutationWriter
{
    private readonly DbContext _db;
    private readonly TimeProvider _clock;

    public EfBuildingOperatorGrantAtomicMutationWriter(
        DbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Guid> AssignAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        DateTime? expiresAtUtc)
    {
        Validate(trustedIssuerId, recipientUserId, buildingId);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (expiresAtUtc.HasValue &&
            (expiresAtUtc.Value.Kind != DateTimeKind.Utc ||
             expiresAtUtc.Value <= now))
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));

        // Join the ABP UoW transaction when one already exists.
        // A PostgreSQL read-committed outer UoW is safe only because
        // EnsureIssuerGrantAsync rechecks and locks the issuer's row.
        var owned = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        try
        {
            await EnsureIssuerGrantAsync(trustedIssuerId, buildingId, now);

            var grant = BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), recipientUserId, buildingId,
                trustedIssuerId, now, expiresAtUtc);
            _db.Set<BuildingOperatorAccessGrant>().Add(grant);
            // Partial unique index rejects two unrevoked (even expired)
            // grants for the same recipient/building.
            await _db.SaveChangesAsync();
            if (owned is not null)
                await owned.CommitAsync();
            return grant.Id;
        }
        catch
        {
            if (owned is not null)
                await owned.RollbackAsync();
            throw;
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }

    public async Task RevokeAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        int expectedRevision)
    {
        Validate(trustedIssuerId, recipientUserId, buildingId);
        if (expectedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        var now = _clock.GetUtcNow().UtcDateTime;
        // Revoke uses the same ABP UoW as recipient/issuer checks.
        var owned = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        try
        {
            await EnsureIssuerGrantAsync(trustedIssuerId, buildingId, now);

            var grant = await _db.Set<BuildingOperatorAccessGrant>()
                .SingleOrDefaultAsync(row =>
                    row.OperatorUserId == recipientUserId &&
                    row.BuildingId == buildingId &&
                    row.RevokedAtUtc == null);
            if (grant is null || grant.RevisionNumber != expectedRevision)
                throw new DbUpdateConcurrencyException(
                    "Stale, unknown or already revoked operator membership.");

            grant.Revoke(trustedIssuerId, now);
            await _db.SaveChangesAsync();
            if (owned is not null)
                await owned.CommitAsync();
        }
        catch
        {
            if (owned is not null)
                await owned.RollbackAsync();
            throw;
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }

    private async Task EnsureIssuerGrantAsync(
        Guid trustedIssuerId, Guid buildingId, DateTime now)
    {
        if (_db.Model.FindEntityType(typeof(BuildingOperatorAccessGrant)) is null)
            throw Denied();

        // PostgreSQL must lock the issuer row for the COMPLETE transaction
        // to serialize delegation with issuer revocation. This is essential
        // when joining an ABP READ COMMITTED UnitOfWork. SQLite uses the
        // existing in-memory/isolated EF query in its test-only composition.
        if (_db.Database.IsNpgsql())
        {
            var transaction = _db.Database.CurrentTransaction
                ?? throw Denied();
            var isolation = transaction.GetDbTransaction().IsolationLevel;
            if (isolation != IsolationLevel.ReadCommitted &&
                isolation != IsolationLevel.Serializable)
                throw Denied();

            var conn = _db.Database.GetDbConnection();
            await using var command = conn.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = """
                SELECT g."Id"
                FROM "BuildingManagementOperatorBuildingGrants" AS g
                INNER JOIN "Buildings" AS b ON b."Id" = g."BuildingId"
                WHERE g."OperatorUserId" = @issuer
                  AND g."BuildingId" = @building
                  AND g."RevokedAtUtc" IS NULL
                  AND g."GrantedAtUtc" <= @now
                  AND (g."ExpiresAtUtc" IS NULL OR g."ExpiresAtUtc" > @now)
                  AND b."IsDeleted" = FALSE
                FOR SHARE OF g
                """;
            var issuer = command.CreateParameter();
            issuer.ParameterName = "issuer";
            issuer.Value = trustedIssuerId;
            command.Parameters.Add(issuer);
            var building = command.CreateParameter();
            building.ParameterName = "building";
            building.Value = buildingId;
            command.Parameters.Add(building);
            var at = command.CreateParameter();
            at.ParameterName = "now";
            at.Value = now;
            command.Parameters.Add(at);
            if (await command.ExecuteScalarAsync() is not Guid)
                throw Denied();
            return;
        }

        // Do not infer ownership from a role or client claim; the issuer
        // must currently hold a live persisted membership on this building.
        // PostgreSQL SERIALIZABLE makes conflicting issuer grant revocations
        // and dependent writes fail/retry instead of silently authorizing
        // with stale snapshots.
        var eligible = await (
            from grant in _db.Set<BuildingOperatorAccessGrant>()
            join building in _db.Set<Building>()
                on grant.BuildingId equals building.Id
            where grant.OperatorUserId == trustedIssuerId &&
                  grant.BuildingId == buildingId &&
                  grant.RevokedAtUtc == null &&
                  grant.GrantedAtUtc <= now &&
                  (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now) &&
                  !building.IsDeleted
            select grant.Id
        ).AnyAsync();

        if (!eligible)
            throw Denied();
    }

    private static void Validate(
        Guid issuer, Guid recipient, Guid building)
    {
        if (issuer == Guid.Empty || recipient == Guid.Empty ||
            building == Guid.Empty || issuer == recipient)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("No active issuer membership for this building.");
}
