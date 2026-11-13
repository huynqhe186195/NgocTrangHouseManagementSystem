using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NgocTrangHouseManagementSystem.Security;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// H4-scoped dispute of an already committed deferred obligation.
/// A scoped operator opens it with documented evidence; a different
/// authenticated Owner resolves it. Both events are append-only and require
/// SERIALIZABLE; the DB trigger rechecks the obligation and its contract.
/// Nothing here moves money or edits the signed obligation.
/// </summary>
public sealed partial class SliceBStagingRoomChangeRequests
{
    public Task<Guid> OpenH4ObligationDisputeAsync(
        Guid contractId, Guid requestId, SliceBH4DisputeInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || !D04Ref(input.Reason) ||
                !D04Ref(input.EvidenceReference) ||
                db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel !=
                    IsolationLevel.Serializable)
                throw Denied();
            if (actor == Guid.Empty) throw Denied();

            var obligationId = await FindH4ObligationAsync(
                db, requestId, contractId);
            await GuardH4ObligationDisputedAsync(db, obligationId);
            if (await IsH4ObligationSettledAsync(db, obligationId))
                throw Denied();

            var id = Guid.NewGuid();
            await using var cmd = Command(db, """
                INSERT INTO "BuildingManagementRoomChangeH4ObligationDisputes"
                 ("Id","ObligationId","ContractId","BuildingId","OpenedByUserId",
                  "OpenedAtUtc","Reason","EvidenceReference")
                VALUES(@id,@obligation,@contract,@building,@actor,
                       @now,@reason,@evidence)
                """);
            Add(cmd, "id", id);
            Add(cmd, "obligation", obligationId);
            Add(cmd, "contract", contractId);
            Add(cmd, "building", building);
            Add(cmd, "actor", actor);
            D04Param(cmd, "now", _clock.GetUtcNow().UtcDateTime);
            D04Param(cmd, "reason", input.Reason!.Trim());
            D04Param(cmd, "evidence", input.EvidenceReference!.Trim());
            await cmd.ExecuteNonQueryAsync();
            return id;
        }, permitEndedContract: true);

    public Task<Guid> ResolveH4ObligationDisputeAsync(
        Guid contractId, Guid requestId, Guid disputeId,
        SliceBH4DisputeResolutionInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            if (input is null ||
                !D04Ref(input.SignedOwnerResolutionReference) ||
                db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel !=
                    IsolationLevel.Serializable)
                throw Denied();

            var obligationId = await FindH4ObligationAsync(
                db, requestId, contractId);
            Guid openedBy;
            await using (var open = Command(db, """
                SELECT "OpenedByUserId"
                  FROM "BuildingManagementRoomChangeH4ObligationDisputes"
                 WHERE "Id"=@dispute AND "ObligationId"=@obligation
                   AND "ContractId"=@contract AND "BuildingId"=@building
                 FOR SHARE
                """))
            {
                Add(open, "dispute", disputeId);
                Add(open, "obligation", obligationId);
                Add(open, "contract", contractId);
                Add(open, "building", building);
                var found = await open.ExecuteScalarAsync();
                if (found is not Guid value || value == Guid.Empty)
                    throw Denied();
                openedBy = value;
            }
            // The resolver must be a different authenticated person from the
            // operator who raised the dispute.
            if (openedBy == actor) throw Denied();

            await using (var exists = Command(db, """
                SELECT count(*)
                  FROM "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
                 WHERE "DisputeId"=@dispute
                """))
            {
                Add(exists, "dispute", disputeId);
                if (Convert.ToInt64(await exists.ExecuteScalarAsync()) != 0)
                    throw Denied();
            }

            var id = Guid.NewGuid();
            await using var cmd = Command(db, """
                INSERT INTO "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
                 ("Id","DisputeId","ContractId","OwnerId","ResolvedAtUtc",
                  "ObligationAccepted","SignedOwnerResolutionReference")
                VALUES(@id,@dispute,@contract,@owner,@now,@accepted,@signed)
                """);
            Add(cmd, "id", id);
            Add(cmd, "dispute", disputeId);
            Add(cmd, "contract", contractId);
            Add(cmd, "owner", actor);
            D04Param(cmd, "now", _clock.GetUtcNow().UtcDateTime);
            D04Param(cmd, "accepted", input.ObligationAccepted);
            D04Param(cmd, "signed",
                input.SignedOwnerResolutionReference!.Trim());
            await cmd.ExecuteNonQueryAsync();
            return id;
        }, permitEndedContract: true);

    public Task<SliceBH4DisputeStatusRead> GetH4ObligationDisputeStatusAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            var obligationId = await FindH4ObligationAsync(
                db, requestId, contractId);
            Guid? disputeId = null;
            DateTime? openedAt = null;
            Guid? openedBy = null;
            string? reason = null;
            Guid? resolutionId = null;
            bool? accepted = null;
            DateTime? resolvedAt = null;
            await using (var cmd = Command(db, """
                SELECT d."Id", d."OpenedAtUtc", d."OpenedByUserId", d."Reason",
                       r."Id", r."ObligationAccepted", r."ResolvedAtUtc"
                  FROM "BuildingManagementRoomChangeH4ObligationDisputes" d
                  LEFT JOIN "BuildingManagementRoomChangeH4ObligationDisputeResolutions" r
                    ON r."DisputeId"=d."Id"
                 WHERE d."ObligationId"=@obligation AND d."BuildingId"=@building
                """))
            {
                Add(cmd, "obligation", obligationId);
                Add(cmd, "building", building);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    disputeId = reader.GetGuid(0);
                    openedAt = reader.GetDateTime(1);
                    openedBy = reader.GetGuid(2);
                    reason = reader.GetString(3);
                    if (!reader.IsDBNull(4))
                    {
                        resolutionId = reader.GetGuid(4);
                        accepted = reader.GetBoolean(5);
                        resolvedAt = reader.GetDateTime(6);
                    }
                }
            }
            var state = disputeId is null ? 0 :
                resolutionId is null ? 1 : accepted == true ? 2 : 3;
            return new SliceBH4DisputeStatusRead(
                obligationId, disputeId, state, openedAt, openedBy, reason,
                resolutionId, accepted, resolvedAt,
                // Posting stays blocked until an Owner ACCEPTS the obligation.
                IsSettlementBlocked: state is 1 or 3,
                CanPostMoney: false);
        }, permitEndedContract: true);

    private static async Task<Guid> FindH4ObligationAsync(
        BuildingManagementDbContext db, Guid requestId, Guid contractId)
    {
        await using var cmd = Command(db, """
            SELECT "Id"
              FROM "BuildingManagementRoomChangeH4Obligations"
             WHERE "RequestId"=@request AND "SourceContractId"=@source
            """);
        Add(cmd, "request", requestId);
        Add(cmd, "source", contractId);
        var found = await cmd.ExecuteScalarAsync();
        if (found is not Guid id || id == Guid.Empty) throw Denied();
        return id;
    }

    private static async Task GuardH4ObligationDisputedAsync(
        BuildingManagementDbContext db, Guid obligationId)
    {
        await using var cmd = Command(db, """
            SELECT count(*)
              FROM "BuildingManagementRoomChangeH4ObligationDisputes"
             WHERE "ObligationId"=@obligation
            """);
        Add(cmd, "obligation", obligationId);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync()) != 0)
            throw Denied();
    }

    private static async Task<bool> IsH4ObligationSettledAsync(
        BuildingManagementDbContext db, Guid obligationId)
    {
        await using var cmd = Command(db, """
            SELECT count(*)
              FROM "BuildingManagementRoomChangeH4SettlementPostings"
             WHERE "ObligationId"=@obligation
            """);
        Add(cmd, "obligation", obligationId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) != 0;
    }
}

public sealed record SliceBH4DisputeInput(
    string? Reason, string? EvidenceReference);

public sealed record SliceBH4DisputeResolutionInput(
    bool ObligationAccepted, string? SignedOwnerResolutionReference);

/// <summary>State: 0 none, 1 open, 2 accepted, 3 rejected. Settlement is
/// blocked while the obligation is contested or was rejected.</summary>
public sealed record SliceBH4DisputeStatusRead(
    Guid ObligationId, Guid? DisputeId, int State,
    DateTime? OpenedAtUtc, Guid? OpenedByUserId, string? Reason,
    Guid? ResolutionId, bool? ObligationAccepted, DateTime? ResolvedAtUtc,
    bool IsSettlementBlocked, bool CanPostMoney);
