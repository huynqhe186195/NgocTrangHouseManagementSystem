using System;
using System.IO;
using System.Threading.Tasks;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Owner-only read of the ORIGINAL H4 deferred obligation produced by
/// an atomic mid-month transfer. No clearing/payment/refund state is inferred.
/// The current H4 writer never collects a top-up or pays a refund.
/// </summary>
public sealed partial class SliceBStagingRoomChangeRequests
{
    private static readonly TimeZoneInfo VietnamZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public Task<SliceBRoomChangeH4ObligationRead> GetH4OutstandingObligationAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            _ = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);

            var completion = await db.RoomChangeMidMonthCompletions
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.RequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building &&
                    x.H4DispositionIntentId != null) ?? throw Denied();
            var intent = await db.RoomChangeDepositDispositionIntents
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == completion.H4DispositionIntentId &&
                    x.RequestId == requestId &&
                    x.SourceContractId == contractId &&
                    x.BuildingId == building) ?? throw Denied();
            try
            {
                completion.VerifyFingerprint();
                intent.VerifyFingerprint();
            }
            catch (InvalidDataException) { throw Denied(); }

            if (intent.TargetContractId != completion.TargetContractId ||
                intent.EvidenceSha256 != completion.H4IntentSha256)
                throw Denied();

            await using var command = Command(db, """
                SELECT "Id", "CompletionId", "IntentId", "TargetContractId",
                       "Kind", "AmountVnd", "DueDateVietnam",
                       "RecordedByOwnerId", "RecordedAtUtc", "SignedReference"
                  FROM "BuildingManagementRoomChangeH4Obligations"
                 WHERE "RequestId"=@request AND "SourceContractId"=@source
                """);
            Add(command, "request", requestId);
            Add(command, "source", contractId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw Denied();

            var id = reader.GetGuid(0);
            var originalCompletionId = reader.GetGuid(1);
            var originalIntentId = reader.GetGuid(2);
            var targetContractId = reader.GetGuid(3);
            var kind = reader.GetInt32(4);
            var amount = reader.GetDecimal(5);
            DateOnly? due = reader.IsDBNull(6)
                ? null : reader.GetFieldValue<DateOnly>(6);
            var owner = reader.GetGuid(7);
            var recordedAt = reader.GetDateTime(8);
            var signedReference = reader.GetString(9);
            if (await reader.ReadAsync()) throw Denied();
            // Release the Npgsql reader before querying the journal on the
            // same transaction/connection (NpgsqlOperationInProgress otherwise).
            await reader.DisposeAsync();

            var expectedKind = completion.H4UnpaidTopUpVnd > 0m ? 1 :
                completion.H4NextMonthRentCreditVnd > 0m ? 2 : 3;
            var expectedAmount = expectedKind == 1
                ? completion.H4UnpaidTopUpVnd
                : expectedKind == 2
                    ? completion.H4NextMonthRentCreditVnd
                    : completion.H4ManualRefundLiabilityVnd;

            if (id == Guid.Empty ||
                originalCompletionId != completion.Id ||
                originalIntentId != intent.Id ||
                targetContractId != completion.TargetContractId ||
                owner != completion.CompletedByUserId ||
                recordedAt != completion.CompletedAtUtc ||
                kind != expectedKind || amount <= 0m ||
                amount != expectedAmount ||
                due != completion.H4TopUpDueDateVietnam ||
                !StringComparer.Ordinal.Equals(
                    signedReference, intent.OwnerSignedDispositionReference))
                throw Denied();

            // D04 is append-only. Reconcile settled amount from the
            // independently reviewed posting journal; never overwrite the
            // signed original obligation or infer a bank API confirmation.
            decimal posted;
            await using (var accounting = Command(db, """
                SELECT COALESCE(sum("AmountVnd"),0)
                  FROM "BuildingManagementRoomChangeH4SettlementPostings"
                 WHERE "ObligationId"=@obligation
                """))
            {
                Add(accounting, "obligation", id);
                posted = Convert.ToDecimal(await accounting.ExecuteScalarAsync());
            }
            if (posted < 0m || posted > amount) throw Denied();
            var remaining = amount - posted;

            var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                _clock.GetUtcNow().UtcDateTime, VietnamZone));
            var aging = RoomChangeH4ObligationAgingEvaluator.Evaluate(
                id, (RoomChangeH4SettlementKind)kind, due, asOf,
                amount, posted);

            return new SliceBRoomChangeH4ObligationRead(
                id, requestId, completion.Id, contractId, targetContractId,
                intent.Id, kind, amount, due,
                completion.H4OriginalHeldDepositVnd,
                completion.CarriedDepositVnd,
                completion.H4TargetRequiredDepositVnd,
                completion.H4UnpaidTopUpVnd,
                completion.H4NextMonthRentCreditVnd,
                completion.H4ManualRefundLiabilityVnd,
                owner, recordedAt, signedReference,
                IsOutstanding: remaining > 0m,
                IsExternallyCollected: kind == 1 && remaining == 0m,
                IsRefundPaid: kind == 3 && remaining == 0m,
                // The D04 rent-only ledger posting is included in the
                // official receivable debt projection, never treated as cash.
                IsRentCreditApplied: kind == 2 && remaining == 0m,
                CanSettleDeposit: false,
                RemainingAmountVnd: remaining,
                IsOverdue: aging.IsOverdue,
                DaysPastDue: aging.DaysPastDue,
                AgedAsOfVietnam: aging.AsOfVietnam);
        }, permitEndedContract: true);
}

/// <summary>
/// Original pending H4 receivable (Kind=1), future rent credit (Kind=2)
/// or independent manual refund liability (Kind=3).
/// No fields constitute proof of external financial settlement.
/// </summary>
public sealed record SliceBRoomChangeH4ObligationRead(
    Guid Id, Guid RequestId, Guid CompletionId,
    Guid SourceContractId, Guid TargetContractId,
    Guid OriginalDispositionIntentId, int Kind,
    decimal AmountVnd, DateOnly? DueDateVietnam,
    decimal OriginalSourceHeldVnd, decimal CarriedDepositVnd,
    decimal TargetRequiredDepositVnd, decimal UnpaidTopUpVnd,
    decimal NextRentCreditVnd, decimal ManualRefundLiabilityVnd,
    Guid RecordedByOwnerId, DateTime RecordedAtUtc,
    string OwnerSignedReference, bool IsOutstanding,
    bool IsExternallyCollected, bool IsRefundPaid,
    bool IsRentCreditApplied, bool CanSettleDeposit,
    decimal RemainingAmountVnd,
    bool IsOverdue = false, int DaysPastDue = 0,
    DateOnly? AgedAsOfVietnam = null)
{
    // Presentation-only aging: a missed deadline is surfaced, never
    // converted into a fee, penalty or new receivable.
    public bool CanPostMoney => false;
}
