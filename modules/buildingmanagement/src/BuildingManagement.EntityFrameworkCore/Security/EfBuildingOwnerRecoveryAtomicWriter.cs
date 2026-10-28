using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.Data;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// UNREGISTERED PostgreSQL staging candidate; not a remotely exposed
/// recovery command. The two server-authenticated approvals are checked in
/// BuildingOwnerRecoveryCoordinator before invoking this writer. This
/// transaction independently verifies the historic founding membership,
/// preserves it, writes a NEW grant with evidence IDs and never replaces
/// existing owner/recipient grants.
/// </summary>
public sealed class EfBuildingOwnerRecoveryAtomicWriter :
    IBuildingOwnerRecoveryWriter
{
    private readonly DbContext _db;
    private readonly TimeProvider _clock;

    public EfBuildingOwnerRecoveryAtomicWriter(
        DbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Guid> RecoverAtomicallyAsync(
        Guid buildingId, Guid originalOwnerId, Guid newOwnerId,
        Guid firstApprovedRootId, Guid secondApprovedRootId,
        Guid approvalEvidenceId)
    {
        if (buildingId == Guid.Empty || originalOwnerId == Guid.Empty ||
            newOwnerId == Guid.Empty || firstApprovedRootId == Guid.Empty ||
            secondApprovedRootId == Guid.Empty || approvalEvidenceId == Guid.Empty ||
            firstApprovedRootId == secondApprovedRootId ||
            originalOwnerId == newOwnerId ||
            newOwnerId == firstApprovedRootId ||
            newOwnerId == secondApprovedRootId ||
            !_db.Database.IsNpgsql())
            throw Denied();

        var owned = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        try
        {
            var transaction = _db.Database.CurrentTransaction
                ?? throw Denied();
            var isolation = transaction.GetDbTransaction().IsolationLevel;
            if (isolation != IsolationLevel.ReadCommitted &&
                isolation != IsolationLevel.Serializable)
                throw Denied();

            var conn = _db.Database.GetDbConnection();
            // Serialize the founding-owner check with every FK-enforced
            // grant INSERT. FOR UPDATE on the Building row conflicts with
            // the foreign-key KEY SHARE that an INSERT must acquire. This
            // prevents a second self-issued founder appearing between the
            // uniqueness check and the recovery grant commit.
            await using (var buildingLock = conn.CreateCommand())
            {
                buildingLock.Transaction = transaction.GetDbTransaction();
                buildingLock.CommandText = """
                    SELECT "Id" FROM "Buildings"
                     WHERE "Id" = @building AND "IsDeleted" = FALSE
                     FOR UPDATE
                    """;
                var id = buildingLock.CreateParameter();
                id.ParameterName = "building";
                id.Value = buildingId;
                buildingLock.Parameters.Add(id);
                if (await buildingLock.ExecuteScalarAsync() is not Guid)
                    throw Denied();
            }

            // All live self-issued grants, not just a caller-selected one,
            // must have a SINGLE matching founder. Lock each such row so
            // revocation cannot change this identity during recovery.
            var founders = new System.Collections.Generic.HashSet<Guid>();
            await using (var command = conn.CreateCommand())
            {
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = """
                    SELECT g."OperatorUserId"
                    FROM "BuildingManagementOperatorBuildingGrants" AS g
                    WHERE g."BuildingId" = @building
                      AND g."OperatorUserId" = g."GrantedByUserId"
                      AND g."RevokedAtUtc" IS NULL
                    FOR SHARE OF g
                    """;
                var building = command.CreateParameter();
                building.ParameterName = "building";
                building.Value = buildingId;
                command.Parameters.Add(building);
                await using var rows = await command.ExecuteReaderAsync();
                while (await rows.ReadAsync())
                    founders.Add(rows.GetGuid(0));
            }
            if (founders.Count != 1 || !founders.Contains(originalOwnerId))
                throw Denied();

            var now = _clock.GetUtcNow().UtcDateTime;
            // Both signatures are persisted independently by authenticated
            // server principals. Lock matching evidence rows BEFORE issuing
            // any grant; serialized consumption prevents replay and races.
            var seen = new System.Collections.Generic.HashSet<Guid>();
            await using (var signoffs = conn.CreateCommand())
            {
                signoffs.Transaction = transaction.GetDbTransaction();
                signoffs.CommandText = """
                    SELECT "ApprovedByUserId"
                      FROM "BuildingManagementOwnerRecoverySignoffs"
                     WHERE "EvidenceId" = @evidence AND "BuildingId" = @building
                       AND "FoundingOwnerId" = @founder AND "NewOwnerId" = @recipient
                       AND "ApprovedByUserId" IN (@first, @second)
                       AND "ConsumedAtUtc" IS NULL AND "ExpiresAtUtc" > @now
                     FOR UPDATE
                    """;
                void Add(string name, object value)
                {
                    var parameter = signoffs.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    signoffs.Parameters.Add(parameter);
                }
                Add("evidence", approvalEvidenceId);
                Add("building", buildingId);
                Add("founder", originalOwnerId);
                Add("recipient", newOwnerId);
                Add("first", firstApprovedRootId);
                Add("second", secondApprovedRootId);
                Add("now", now);
                await using var rows = await signoffs.ExecuteReaderAsync();
                while (await rows.ReadAsync())
                    seen.Add(rows.GetGuid(0));
            }
            if (seen.Count != 2 || !seen.Contains(firstApprovedRootId) ||
                !seen.Contains(secondApprovedRootId))
                throw Denied();

            var grant = BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), newOwnerId, buildingId,
                firstApprovedRootId, now, null);
            // Persist independent sign-off identifiers in the same grant
            // INSERT/transaction. Never use user-provided free-form audit
            // text, and never overwrite a founding membership.
            grant.SetProperty("OwnerRecoveryEvidenceId",
                approvalEvidenceId.ToString("D"));
            grant.SetProperty("OwnerRecoverySecondRootId",
                secondApprovedRootId.ToString("D"));
            grant.SetProperty("OwnerRecoveryFoundingUserId",
                originalOwnerId.ToString("D"));
            _db.Set<BuildingOperatorAccessGrant>().Add(grant);
            await _db.SaveChangesAsync();
            // Consume exactly the same two approval rows IN this Building
            // transaction. Failure rolls back both new grant and evidence.
            var consumed = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "BuildingManagementOwnerRecoverySignoffs"
                   SET "ConsumedAtUtc" = {now}, "RecoveryGrantId" = {grant.Id}
                 WHERE "EvidenceId" = {approvalEvidenceId}
                   AND "BuildingId" = {buildingId}
                   AND "FoundingOwnerId" = {originalOwnerId}
                   AND "NewOwnerId" = {newOwnerId}
                   AND "ApprovedByUserId" IN ({firstApprovedRootId}, {secondApprovedRootId})
                   AND "ConsumedAtUtc" IS NULL
                   AND "ExpiresAtUtc" > clock_timestamp()
                """);
            if (consumed != 2)
                throw Denied();
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

    private static AbpAuthorizationException Denied()
        => new("Invalid dual-root owner recovery or missing original grant.");
}
