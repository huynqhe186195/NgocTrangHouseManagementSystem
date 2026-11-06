using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.RoomChanges;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Staging-only, authenticated H4 D04 SETTLEMENT JOURNAL.
/// Nothing here initiates a bank transfer or marks a Payment completed.
/// External receipt/refund evidence must already exist and be independently
/// attested before the Owner can append a posting. No original H4 obligation
/// or issued invoice is rewritten.
/// </summary>
public sealed partial class SliceBStagingRoomChangeRequests
{
    private sealed record D04Obligation(
        Guid Id, Guid RequestId, Guid SourceContractId, Guid TargetContractId,
        Guid BuildingId, int Kind, decimal Amount, DateOnly? Due,
        DateOnly TransferDate, Guid OwnerId, DateTime OriginalAtUtc);

    private static void D04Param(DbCommand cmd, string name, object? value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(param);
    }

    private static bool D04Ref(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 12 and <= 500;

    private async Task<D04Obligation> LoadD04Async(
        BuildingManagementDbContext db, Guid contractId, Guid requestId,
        Guid sourceRoom, Guid building)
    {
        _ = await LockRequestAsync(db, requestId, contractId, sourceRoom, building);
        var completion = await db.RoomChangeMidMonthCompletions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                x.SourceContractId == contractId && x.BuildingId == building &&
                x.H4DispositionIntentId != null) ?? throw Denied();
        var original = await db.RoomChangeDepositDispositionIntents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == completion.H4DispositionIntentId &&
                x.RequestId == requestId && x.SourceContractId == contractId &&
                x.BuildingId == building) ?? throw Denied();
        try
        {
            completion.VerifyFingerprint();
            original.VerifyFingerprint();
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or
            InvalidOperationException) { throw Denied(); }
        if (completion.H4IntentSha256 != original.EvidenceSha256 ||
            completion.TargetContractId != original.TargetContractId)
            throw Denied();

        await using var cmd = Command(db, """
            SELECT o."Id", o."TargetContractId", o."Kind", o."AmountVnd",
                   o."DueDateVietnam", o."RecordedByOwnerId", o."RecordedAtUtc",
                   r."RequestedDate"
              FROM "BuildingManagementRoomChangeH4Obligations" o
              JOIN "BuildingManagementRoomChangeRequests" r
                ON r."Id"=o."RequestId"
             WHERE o."RequestId"=@request
               AND o."SourceContractId"=@source
               AND o."CompletionId"=@completion
               AND o."IntentId"=@intent
               AND r."BuildingId"=@building
             FOR UPDATE OF o
            """);
        Add(cmd, "request", requestId);
        Add(cmd, "source", contractId);
        Add(cmd, "completion", completion.Id);
        Add(cmd, "intent", original.Id);
        Add(cmd, "building", building);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw Denied();
        var claim = new D04Obligation(
            reader.GetGuid(0), requestId, contractId, reader.GetGuid(1),
            building, reader.GetInt32(2), reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
            reader.GetFieldValue<DateOnly>(7), reader.GetGuid(5),
            reader.GetDateTime(6));
        if (await reader.ReadAsync()) throw Denied();
        var expected = claim.Kind == 1 ? completion.H4UnpaidTopUpVnd :
            claim.Kind == 2 ? completion.H4NextMonthRentCreditVnd :
            completion.H4ManualRefundLiabilityVnd;
        if (claim.Kind is < 1 or > 3 || claim.Amount <= 0m ||
            claim.Amount != expected || claim.OwnerId != completion.CompletedByUserId ||
            claim.TargetContractId != completion.TargetContractId ||
            claim.OriginalAtUtc != completion.CompletedAtUtc ||
            claim.Due != completion.H4TopUpDueDateVietnam)
            throw Denied();
        return claim;
    }

    /// <summary>
    /// A different authenticated building operator reviews an ALREADY
    /// completed bank transfer. Review only; never initiates bank movement.
    /// For top-up, amount/bank reference come from the immutable Payment,
    /// not from a user-supplied sum. Refund is a signed manual attestation.
    /// </summary>
    public Task<SliceBRoomChangeD04ReviewRead> ReviewH4BankEvidenceAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeD04ReviewInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || input.Kind is not (1 or 3) ||
                !D04Ref(input.BankStatementEvidenceReference) ||
                !D04Ref(input.SignedReviewReference) ||
                db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel !=
                    IsolationLevel.Serializable)
                throw Denied();
            var claim = await LoadD04Async(db, contractId, requestId,
                sourceRoom, building);
            if (claim.Kind != input.Kind || actor == claim.OwnerId)
                throw Denied();
            var now = _clock.GetUtcNow().UtcDateTime;
            decimal amount;
            DateTime paidAt;
            string bankRef;
            if (input.Kind == 1)
            {
                if (input.PaymentId is null || input.PaymentId == Guid.Empty ||
                    input.PaidAtUtc.HasValue || input.BankTransactionReference is not null)
                    throw Denied();
                var payment = await db.Payments.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == input.PaymentId &&
                        !x.IsDeleted && x.ContractId == claim.TargetContractId)
                    ?? throw Denied();
                if (payment.Purpose != PaymentPurpose.ContractDeposit ||
                    payment.Status != PaymentStatus.Completed ||
                    (int)payment.Method != 2 || (int)payment.Channel != 1 ||
                    payment.PaidAt is null || payment.PaidAt > now ||
                    payment.Amount <= 0m ||
                    decimal.Truncate(payment.Amount) != payment.Amount ||
                    !D04Ref(payment.TransactionReference) ||
                    payment.Notes is null ||
                    !payment.Notes.StartsWith("BANK-PROOF: ", StringComparison.Ordinal))
                    throw Denied();
                amount = payment.Amount;
                paidAt = payment.PaidAt.Value;
                bankRef = payment.TransactionReference!;
            }
            else
            {
                if (input.PaymentId.HasValue ||
                    input.PaidAtUtc is null ||
                    input.PaidAtUtc.Value.Kind != DateTimeKind.Utc ||
                    input.PaidAtUtc > now ||
                    !D04Ref(input.BankTransactionReference))
                    throw Denied();
                amount = claim.Amount;
                paidAt = input.PaidAtUtc.Value;
                bankRef = input.BankTransactionReference!.Trim();
            }
            if (paidAt < claim.OriginalAtUtc ||
                input.BankStatementEvidenceReference!.Trim() == bankRef ||
                input.SignedReviewReference!.Trim() == bankRef ||
                input.SignedReviewReference.Trim() ==
                    input.BankStatementEvidenceReference.Trim())
                throw Denied();
            var id = Guid.NewGuid();
            await using (var cmd = Command(db, """
                INSERT INTO "BuildingManagementRoomChangeH4SettlementReviews"
                 ("Id","ObligationId","Kind","PaymentId","AmountVnd",
                  "BankTransactionReference","BankStatementEvidenceReference",
                  "PaidAtUtc","ReviewedByUserId","ReviewedAtUtc",
                  "SignedReviewReference")
                VALUES (@id,@obligation,@kind,@payment,@amount,
                        @bank,@statement,@paid,@reviewer,@now,@signed)
                """))
            {
                Add(cmd, "id", id);
                Add(cmd, "obligation", claim.Id);
                D04Param(cmd, "kind", claim.Kind);
                D04Param(cmd, "payment", input.PaymentId);
                D04Param(cmd, "amount", amount);
                D04Param(cmd, "bank", bankRef);
                D04Param(cmd, "statement",
                    input.BankStatementEvidenceReference.Trim());
                D04Param(cmd, "paid", paidAt);
                Add(cmd, "reviewer", actor);
                D04Param(cmd, "now", now);
                D04Param(cmd, "signed", input.SignedReviewReference.Trim());
                await cmd.ExecuteNonQueryAsync();
            }
            return new SliceBRoomChangeD04ReviewRead(
                id, claim.Id, claim.Kind, amount, actor, now,
                IsIndependentReview: true,
                IsBankApiVerified: false, IsFinancialPosting: false);
        }, permitEndedContract: true);

    /// <summary>
    /// The founding Owner alone posts the reviewed financial event.
    /// The database SERIALIZABLE trigger independently rechecks source
    /// details and immutable H4 amount, forbids replay and overpayment.
    /// </summary>
    public Task<SliceBRoomChangeD04PostingRead> PostH4SettlementAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeD04PostingInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            if (input is null ||
                db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel !=
                    IsolationLevel.Serializable ||
                (input.ReviewId.HasValue == input.IssuedInvoiceId.HasValue))
                throw Denied();
            var claim = await LoadD04Async(db, contractId, requestId,
                sourceRoom, building);
            if (claim.OwnerId != actor) throw Denied();

            var prior = new List<RoomChangeH4VerifiedPayment>();
            await using (var cmd = Command(db, """
                SELECT p."AmountVnd", p."Id", p."SourceReference"
                  FROM "BuildingManagementRoomChangeH4SettlementPostings" p
                 WHERE p."ObligationId"=@obligation ORDER BY p."Id"
                """))
            {
                Add(cmd, "obligation", claim.Id);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    prior.Add(new RoomChangeH4VerifiedPayment(
                        reader.GetGuid(1), reader.GetDecimal(0),
                        reader.GetString(2)));
            }
            Guid? reviewId = null;
            Guid? invoiceId = null;
            string sourceReference;
            decimal amount;
            Guid? reviewer = null;
            Guid? approval = null;
            string? bankReference = null;
            Guid? issueTarget = null;
            int? issueYear = null, issueMonth = null;
            decimal? invoiceOutstanding = null;
            if (claim.Kind is 1 or 3)
            {
                if (!input.ReviewId.HasValue || input.ReviewId == Guid.Empty)
                    throw Denied();
                reviewId = input.ReviewId;
                await using var cmd = Command(db, """
                    SELECT "ObligationId","Kind","AmountVnd",
                           "BankTransactionReference","ReviewedByUserId"
                      FROM "BuildingManagementRoomChangeH4SettlementReviews"
                     WHERE "Id"=@review FOR SHARE
                    """);
                Add(cmd, "review", reviewId.Value);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync() ||
                    reader.GetGuid(0) != claim.Id ||
                    reader.GetInt32(1) != claim.Kind)
                    throw Denied();
                amount = reader.GetDecimal(2);
                sourceReference = reader.GetString(3);
                reviewer = reader.GetGuid(4);
                approval = actor;
                bankReference = claim.Kind == 3 ? sourceReference : null;
                if (await reader.ReadAsync()) throw Denied();
            }
            else
            {
                if (!input.IssuedInvoiceId.HasValue ||
                    input.IssuedInvoiceId == Guid.Empty)
                    throw Denied();
                invoiceId = input.IssuedInvoiceId;
                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == invoiceId &&
                        x.ContractId == claim.TargetContractId &&
                        x.BuildingId == building) ?? throw Denied();
                var receivable = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.IssuedInvoiceId == invoiceId &&
                        x.Kind == OfficialInvoiceKind.Monthly &&
                        x.ContractId == claim.TargetContractId)
                    ?? throw Denied();
                var receipts = await db.OfficialInvoiceReceiptReviews.AsNoTracking()
                    .Where(x => x.ReceivablePostingId == receivable.Id &&
                        x.Accepted).SumAsync(x => x.Amount);
                var adjustments = await db.MonthlyInvoiceAdjustmentDecisions
                    .AsNoTracking().Where(x => x.IssuedInvoiceId == invoiceId &&
                        x.Accepted).SumAsync(x => x.ApprovedSignedDeltaAmount);
                // D04 policy is RENT-only: reserve all bank receipts
                // against the rent component conservatively, so no credit
                // can spill into electricity, water or auxiliary fees.
                var originalCandidate = await db.MonthlyInvoiceCandidates
                    .AsNoTracking().SingleOrDefaultAsync(x =>
                        x.Id == issued.CandidateId &&
                        x.ContractId == claim.TargetContractId)
                    ?? throw Denied();
                invoiceOutstanding = Math.Min(
                    receivable.OriginalAmount + adjustments - receipts,
                    originalCandidate.RentAmount - receipts);
                amount = claim.Amount;
                sourceReference = issued.InvoiceNumber;
                issueTarget = issued.ContractId;
                issueYear = issued.BillingYear;
                issueMonth = issued.BillingMonth;
            }

            var evidence = new RoomChangeH4SettlementEvidence(
                claim.Id, claim.SourceContractId, claim.TargetContractId,
                (RoomChangeH4SettlementKind)claim.Kind, claim.Amount,
                claim.TransferDate, claim.Due,
                prior.ToArray(),
                new[] { new RoomChangeH4VerifiedPayment(
                    reviewId ?? invoiceId ?? Guid.Empty, amount, sourceReference) },
                invoiceId, issueTarget, issueYear, issueMonth,
                invoiceOutstanding, claim.Kind == 3 ? reviewId : null,
                reviewer, approval, bankReference);
            try { RoomChangeH4SettlementEvidenceGate.RequireEligible(evidence); }
            catch (InvalidOperationException) { throw Denied(); }

            var id = Guid.NewGuid();
            var at = _clock.GetUtcNow().UtcDateTime;
            await using (var cmd = Command(db, """
                INSERT INTO "BuildingManagementRoomChangeH4SettlementPostings"
                 ("Id","ObligationId","Kind","ReviewId","IssuedInvoiceId",
                  "AmountVnd","PostedByOwnerId","PostedAtUtc","SourceReference")
                VALUES(@id,@obligation,@kind,@review,@invoice,
                       @amount,@actor,@at,@source)
                """))
            {
                Add(cmd, "id", id);
                Add(cmd, "obligation", claim.Id);
                D04Param(cmd, "kind", claim.Kind);
                D04Param(cmd, "review", reviewId);
                D04Param(cmd, "invoice", invoiceId);
                D04Param(cmd, "amount", amount);
                Add(cmd, "actor", actor);
                D04Param(cmd, "at", at);
                D04Param(cmd, "source", sourceReference);
                await cmd.ExecuteNonQueryAsync();
            }
            var remaining = claim.Amount - prior.Sum(x => x.AmountVnd) - amount;
            return new SliceBRoomChangeD04PostingRead(
                id, claim.Id, claim.Kind, amount, remaining,
                IsLedgerPosted: true, IsBankApiVerified: false,
                IsBankTransferInitiatedBySystem: false);
        }, permitEndedContract: true);

    public Task<SliceBRoomChangeD04BalanceRead> GetH4SettlementBalanceAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            await _identity.AcquireBootstrapAsync(actor);
            var claim = await LoadD04Async(db, contractId, requestId,
                sourceRoom, building);
            await using var cmd = Command(db, """
                SELECT COALESCE(sum("AmountVnd"),0),count(*)
                  FROM "BuildingManagementRoomChangeH4SettlementPostings"
                 WHERE "ObligationId"=@id
                """);
            Add(cmd, "id", claim.Id);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw Denied();
            var paid = reader.GetDecimal(0);
            var count = reader.GetInt64(1);
            if (paid < 0m || paid > claim.Amount) throw Denied();
            await reader.DisposeAsync();
            Guid? matchedIssued = null;
            decimal? remainingOfficialDebt = null;
            // D04 credit is a non-cash offset against the exact originally
            // issued target contract's next-month rent. Recalculate the
            // issued debt after accepted bank receipts AND the D04 offset.
            if (claim.Kind == 2 && paid > 0m)
            {
                Guid invoiceId;
                await using (var link = Command(db, """
                    SELECT "IssuedInvoiceId"
                      FROM "BuildingManagementRoomChangeH4SettlementPostings"
                     WHERE "ObligationId"=@obligation AND "Kind"=2
                    """))
                {
                    Add(link, "obligation", claim.Id);
                    var found = await link.ExecuteScalarAsync();
                    if (found is not Guid guid || guid == Guid.Empty)
                        throw Denied();
                    invoiceId = guid;
                }
                var issued = await db.MonthlyIssuedInvoices.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == invoiceId &&
                        x.ContractId == claim.TargetContractId &&
                        x.BuildingId == building) ?? throw Denied();
                var official = await db.OfficialInvoiceReceivables.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.Kind == OfficialInvoiceKind.Monthly &&
                        x.IssuedInvoiceId == invoiceId &&
                        x.ContractId == claim.TargetContractId) ?? throw Denied();
                var bankReceipts = await db.OfficialInvoiceReceiptReviews
                    .AsNoTracking().Where(x => x.ReceivablePostingId == official.Id &&
                        x.Accepted).SumAsync(x => x.Amount);
                var signedChanges = await db.MonthlyInvoiceAdjustmentDecisions
                    .AsNoTracking().Where(x => x.IssuedInvoiceId == invoiceId &&
                        x.Accepted).SumAsync(x => x.ApprovedSignedDeltaAmount);
                var debt = official.OriginalAmount + signedChanges -
                    bankReceipts - paid;
                if (debt < 0m || issued.BillingYear !=
                    claim.TransferDate.AddMonths(1).Year ||
                    issued.BillingMonth != claim.TransferDate.AddMonths(1).Month)
                    throw Denied();
                matchedIssued = invoiceId;
                remainingOfficialDebt = debt;
            }
            return new SliceBRoomChangeD04BalanceRead(
                claim.Id, claim.Kind, claim.Amount, paid, claim.Amount-paid,
                count, IsBankApiVerified: false,
                CreditedIssuedInvoiceId: matchedIssued,
                IssuedRentDebtAfterCreditVnd: remainingOfficialDebt);
        }, permitEndedContract: true);
}

public sealed record SliceBRoomChangeD04ReviewInput(
    int Kind, Guid? PaymentId, string? BankTransactionReference,
    DateTime? PaidAtUtc, string? BankStatementEvidenceReference,
    string? SignedReviewReference);

public sealed record SliceBRoomChangeD04PostingInput(
    Guid? ReviewId, Guid? IssuedInvoiceId);

public sealed record SliceBRoomChangeD04ReviewRead(
    Guid Id, Guid ObligationId, int Kind, decimal AmountVnd,
    Guid ReviewedByUserId, DateTime ReviewedAtUtc,
    bool IsIndependentReview, bool IsBankApiVerified, bool IsFinancialPosting);

public sealed record SliceBRoomChangeD04PostingRead(
    Guid Id, Guid ObligationId, int Kind, decimal AmountVnd,
    decimal RemainingOutstandingVnd,
    bool IsLedgerPosted, bool IsBankApiVerified,
    bool IsBankTransferInitiatedBySystem);

public sealed record SliceBRoomChangeD04BalanceRead(
    Guid ObligationId, int Kind, decimal OriginalAmountVnd,
    decimal LedgerPostedAmountVnd, decimal RemainingOutstandingVnd,
    long PostingCount, bool IsBankApiVerified,
    Guid? CreditedIssuedInvoiceId = null,
    decimal? IssuedRentDebtAfterCreditVnd = null);
