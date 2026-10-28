using System;
using System.Data;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// UNREGISTERED, PostgreSQL-only Slice A candidate. An actual CRUD write
/// must FIRST check real server principal, ABP operation permission, active
/// Identity account and resource->building scope in its application layer.
/// It must then perform its mutation under this same-db-context fence.
/// Neither prior read-only membership checks nor a role claim are enough.
/// </summary>
public sealed class EfBuildingScopedWriteFenceCandidate
{
    private readonly DbContext _db;
    private readonly TimeProvider _clock;

    public EfBuildingScopedWriteFenceCandidate(
        DbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<T> ExecuteAsync<T>(
        Guid trustedActorId, Guid serverResolvedBuildingId,
        Func<Task<T>> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (trustedActorId == Guid.Empty ||
            serverResolvedBuildingId == Guid.Empty ||
            !_db.Database.IsNpgsql() ||
            _db.Model.FindEntityType(typeof(BuildingOperatorAccessGrant)) is null ||
            _db.Model.FindEntityType(typeof(Building)) is null ||
            _db.Database.CurrentTransaction is not null)
            throw Denied();

        // READ COMMITTED + SELECT FOR SHARE obtains a *row* lock on the
        // issuer membership. PostgreSQL UPDATE/revoke conflicts with it.
        // If revocation committed first, the SELECT returns no live row.
        // Do not ever mutate before acquiring the membership lock.
        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted);
        try
        {
            var connection = _db.Database.GetDbConnection();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = tx.GetDbTransaction();
                command.CommandText = """
                    SELECT g."Id"
                    FROM "BuildingManagementOperatorBuildingGrants" AS g
                    INNER JOIN "Buildings" AS b ON b."Id" = g."BuildingId"
                    WHERE g."OperatorUserId" = @actor
                      AND g."BuildingId" = @building
                      AND g."RevokedAtUtc" IS NULL
                      AND g."GrantedAtUtc" <= @at
                      AND (g."ExpiresAtUtc" IS NULL OR g."ExpiresAtUtc" > @at)
                      AND b."IsDeleted" = FALSE
                    FOR SHARE OF g
                    """;
                var actor = command.CreateParameter();
                actor.ParameterName = "actor";
                actor.Value = trustedActorId;
                command.Parameters.Add(actor);
                var building = command.CreateParameter();
                building.ParameterName = "building";
                building.Value = serverResolvedBuildingId;
                command.Parameters.Add(building);
                var at = command.CreateParameter();
                at.ParameterName = "at";
                at.Value = _clock.GetUtcNow().UtcDateTime;
                command.Parameters.Add(at);

                if (await command.ExecuteScalarAsync() is not Guid)
                    throw Denied();
            }

            // Caller's actual EF mutation must use *this* DbContext/UoW.
            // No further permission/identity claim is trusted from DTO.
            var result = await mutation();
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static AbpAuthorizationException Denied()
        => new("The current account has no live locked membership for this building.");
}
