using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Floors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PaymentMethod = BuildingManagement.Payments.BuildingManagement.Payments.PaymentMethod;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Trusted source reader only: read a CURRENT persisted payOS intent and
/// SIGNED, reconciled inbox directly from the isolated staging database.
/// Must never construct either object from a webhook/controller DTO.
/// No implementation or DI registration is provided by this draft.
/// </summary>
public interface IPayOsD04TrustedStagingEvidenceReader
{
    Task<(PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox)?>
        ReadCurrentAsync(Guid intentId, CancellationToken cancellationToken);
}

/// <summary>
/// Cross-database authorization boundary: lease MUST hold active ABP Identity
/// and Building membership/permission read-locks preventing revocation races
/// until AFTER the legacy BuildingManagement transaction commits.
/// No permissive implementation or DI registration exists in this draft.
/// </summary>
public interface IPayOsD04CurrentDualAuthority
{
    Task<IAsyncDisposable> AcquireCommitLeaseAsync(
        Guid buildingId, Guid paymentId, Guid bankReviewerId,
        Guid ownerApproverId, CancellationToken cancellationToken);
}

/// <summary>
/// Building-DB transaction writer for the existing Payment/DepositSettlement
/// aggregates and the new UNIQUE payOS D04 receipt + outbox tables.
///
/// IMPORTANT: This is NOT registered by any runtime module and has no HTTP
/// route. Its required independent staging/Identity readers are deliberately
/// missing (deny-by-absence) until secure readers, durable bank verification
/// and signed Owner approval have been accepted. Never call from a webhook.
/// All columns/table names are the actual BuildingManagement legacy model.
/// </summary>
public sealed class PayOsD04LegacyPostingWriter
{
    private readonly BuildingManagementDbContext _db;
    private readonly IPayOsD04TrustedStagingEvidenceReader _staging;
    private readonly IPayOsD04CurrentDualAuthority _authority;

    public PayOsD04LegacyPostingWriter(
        BuildingManagementDbContext db,
        IPayOsD04TrustedStagingEvidenceReader staging,
        IPayOsD04CurrentDualAuthority authority)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    /// <summary>
    /// Commit ONE ContractDeposit payment and its allocation in the same
    /// SERIALIZABLE Building DB transaction as immutable source journal/outbox.
    /// Cross-DB ACK is NOT done here. On serialization failure retry the SAME
    /// intent ID via a fresh DbContext and re-authorize; never mint a new order.
    /// </summary>
    public async Task<PayOsD04LegacyWriteResult> PostAsync(
        Guid intentId, CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty)
            throw new ArgumentException("A persisted intent ID is required.", nameof(intentId));

        var initial = await ReadSourceAsync(intentId, cancellationToken);
        var targetPaymentId = initial.Intent.PaymentId ??
            throw Denied("The payOS intent does not target a legacy Payment.");
        if (initial.Intent.InvoiceId.HasValue)
            throw Denied("D04 permits ContractDeposit Payment only.");

        // Read ID only before taking locks. Source authorization is checked
        // AGAIN after Contract -> Payment locks (in stable legacy order).
        var contractId = await _db.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == targetPaymentId && x.ContractId.HasValue)
            .Select(x => x.ContractId!.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (contractId == Guid.Empty)
            throw Denied("The legacy contract source is missing.");

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        Contract? contract = null;
        await foreach (var row in _db.Contracts.FromSqlInterpolated(
            $@"SELECT * FROM ""BuildingManagementContracts""
               WHERE ""Id"" = {contractId} AND ""IsDeleted"" = FALSE FOR UPDATE")
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            contract = row;
        }
        if (contract is null)
            throw Denied("The legacy contract was deleted.");
        // A previously committed receipt still needs ACK after this contract
        // subsequently ends. Its status is checked only for NEW posting, not
        // for journal replay and reconciliation.

        Payment? payment = null;
        await foreach (var row in _db.Payments.FromSqlInterpolated(
            $@"SELECT * FROM ""BuildingManagementPayments""
               WHERE ""Id"" = {targetPaymentId} AND ""IsDeleted"" = FALSE FOR UPDATE")
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            payment = row;
        }
        if (payment is null || payment.ContractId != contract.Id)
            throw Denied("The payment source changed while acquiring locks.");

        var room = await _db.Rooms.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == contract.RoomId, cancellationToken);
        var floor = room is null ? null :
            await _db.Floors.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == room.FloorId, cancellationToken);
        if (floor is null || floor.BuildingId != initial.Intent.BuildingId)
            throw Denied("The contract no longer belongs to this building.");

        var source = await ReadSourceAsync(intentId, cancellationToken);
        if (source.Intent.PaymentId != payment.Id ||
            source.Intent.BuildingId != initial.Intent.BuildingId ||
            source.Intent.PayerId != payment.TenantId ||
            source.Intent.OrderCode != initial.Intent.OrderCode ||
            source.Intent.AmountVnd != initial.Intent.AmountVnd ||
            source.Intent.ProviderReference != initial.Intent.ProviderReference)
            throw Denied("Cross-database payOS source changed before commit.");

        var reviewed = await ReadCommittedReviewAsync(
            transaction, intentId, cancellationToken);
        if (reviewed is null || reviewed.PaymentId != payment.Id ||
            reviewed.OrderCode != source.Intent.OrderCode ||
            reviewed.BuildingId != floor.BuildingId ||
            reviewed.AmountVnd != source.Intent.AmountVnd ||
            !string.Equals(reviewed.BankReference, source.Intent.ProviderReference,
                StringComparison.Ordinal) ||
            reviewed.BankReviewerId == Guid.Empty ||
            reviewed.OwnerApproverId == Guid.Empty ||
            reviewed.BankReviewerId == reviewed.OwnerApproverId ||
            reviewed.VerifiedAtUtc.Kind != DateTimeKind.Utc ||
            reviewed.ApprovedAtUtc.Kind != DateTimeKind.Utc ||
            reviewed.ApprovedAtUtc < reviewed.VerifiedAtUtc)
            throw Denied("Independent bank review/Owner approval is missing or expired.");

        // A committed receipt can be recovered even if permissions are now
        // revoked, but MUST NOT create any new money. Read exact source journal.
        var existing = await ReadJournalProofAsync(
            transaction, intentId, cancellationToken);
        // A partially missing outbox or mismatched Payment can cause the
        // joined proof query to return null. Never confuse that anomaly with
        // an unposted intent and try to credit it a second time.
        if (existing is null &&
            await JournalForIntentExistsAsync(transaction, intentId, cancellationToken))
            throw Denied("D04 receipt exists but its required Payment/settlement/outbox is incomplete or conflicting.");
        if (existing is not null)
        {
            var recoveryPlan = new PayOsD04LegacyCommitPlan(
                intentId, reviewed.BuildingId, payment.Id, source.Intent.OrderCode,
                reviewed.AmountVnd, reviewed.BankReference,
                PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(source.Intent),
                reviewed.DocumentSha256, reviewed.BankReviewerId,
                reviewed.OwnerApproverId);
            var result = PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                source.Intent, source.Inbox, payment, recoveryPlan, existing);
            if (result == PayOsD04RecoveryDecision.StopAndInvestigate)
                throw Denied("Existing legacy D04 journal does not match the source.");
            return new PayOsD04LegacyWriteResult(
                existing.ReceiptId, existing.DepositSettlementId,
                existing.OutboxId,
                result == PayOsD04RecoveryDecision.AlreadyAcknowledged
                    ? PayOsD04LegacyWriteDisposition.ExistingAlreadyAcknowledged
                    : PayOsD04LegacyWriteDisposition.ExistingCommittedNeedsAck);
        }

        // Existing journals may be acknowledged after the contract closes
        // or approval expires, but neither may authorize NEW cash posting.
        if (contract.Status is not (ContractStatus.Signed or ContractStatus.Active))
            throw Denied("The contract is no longer payable.");
        if (reviewed.ExpiresAtUtc <= DateTime.UtcNow)
            throw Denied("Owner approval expired before financial commit.");

        // No receipt => current Identity/Building grants MUST be revalidated.
        await using var activeAuthorizationLease =
            await _authority.AcquireCommitLeaseAsync(
                reviewed.BuildingId, payment.Id, reviewed.BankReviewerId,
                reviewed.OwnerApproverId, cancellationToken)
            ?? throw Denied("An active Identity/Building revocation-safe lease is required.");

        if (source.Intent.Status != PayOsPaymentIntentStatus.ConfirmedPendingPosting ||
            payment.Status != PaymentStatus.Pending ||
            payment.Purpose != PaymentPurpose.ContractDeposit ||
            payment.Method != PaymentMethod.BankTransfer ||
            payment.Channel != PaymentChannel.VietQr ||
            payment.Amount <= 0 || payment.Amount > long.MaxValue ||
            decimal.Truncate(payment.Amount) != payment.Amount ||
            payment.Amount != reviewed.AmountVnd)
            throw Denied("Pending VietQR ContractDeposit source is not valid.");

        if (!await _db.ContractTenants.AnyAsync(x =>
            x.ContractId == contract.Id && x.TenantId == payment.TenantId &&
            x.Role == ContractTenantRole.PrimaryTenant, cancellationToken))
            throw Denied("The primary tenant does not match the contract.");

        // Follow PaymentAppService.CompleteContractDepositAsync: use COMPLETED
        // settlement coverage, not total Payment rows and not manual balances.
        var covered = await _db.DepositSettlements.IgnoreQueryFilters()
            .Where(x => x.TargetContractId == contract.Id &&
                x.Status == DepositSettlementStatus.Completed &&
                (x.Type == DepositSettlementType.AllocateToContractDeposit ||
                 x.Type == DepositSettlementType.ContractTransfer ||
                 x.Type == DepositSettlementType.RoomChangeCarryForward))
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;
        var remaining = contract.DepositAmount - covered;
        if (remaining <= 0 || payment.Amount > remaining)
            throw Denied("Deposit coverage changed or exceeds contract balance.");

        // Check GLOBAL bank-reference history, including soft-deleted rows.
        var referenceUsed = await _db.Payments.IgnoreQueryFilters()
            .AsNoTracking().AnyAsync(x =>
                x.Id != payment.Id &&
                x.TransactionReference == reviewed.BankReference,
                cancellationToken);
        var journalRefUsed = await JournalReferenceExistsAsync(
            transaction, reviewed.BankReference, cancellationToken);
        if (referenceUsed || journalRefUsed ||
            (!string.IsNullOrWhiteSpace(payment.TransactionReference) &&
             payment.TransactionReference != reviewed.BankReference))
            throw Denied("Bank reference is already used or conflicts.");

        var review = new PayOsD04IndependentReview(
            BuildingId: reviewed.BuildingId, PaymentId: payment.Id,
            BankReviewerId: reviewed.BankReviewerId,
            ApprovingOwnerId: reviewed.OwnerApproverId,
            ReviewerHasCurrentBankEvidenceGrant: true,
            OwnerHasCurrentPostingGrant: true,
            IndependentBankEvidenceVerified: true,
            IndependentlyVerifiedAmountVnd: reviewed.AmountVnd,
            IndependentlyVerifiedReference: reviewed.BankReference,
            EvidenceSha256: reviewed.DocumentSha256,
            VerifiedAtUtc: reviewed.VerifiedAtUtc,
            CurrentContractScopeMatches: true,
            CurrentDepositCoverageMatches: true,
            LegacyPaymentLocked: true,
            BankReferenceUnusedInLegacyLedger: true);
        var plan = PayOsD04LegacyCommitProtocol.CreateCandidate(
            source.Intent, source.Inbox, payment, review);

        // All database changes below are atomic with this SAME legacy
        // transaction. A failed journal or outbox unique constraint rolls back
        // both Payment.Complete and DepositSettlement. No auto-retry inside.
        var paidAt = DateTime.UtcNow;
        payment.Complete(paidAt, plan.BankReference);
        var settlement = new DepositSettlement(
            Guid.NewGuid(), DepositSettlementType.AllocateToContractDeposit,
            payment.Amount, paymentId: payment.Id,
            targetContractId: contract.Id,
            notes: "payOS D04 independently approved deposit " + payment.PaymentNumber);
        settlement.Complete(paidAt);
        _db.DepositSettlements.Add(settlement);
        await _db.SaveChangesAsync(cancellationToken);

        var receiptId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""BuildingManagementPayOsD04ReceiptJournal""
            (""Id"", ""PayOsIntentId"", ""OrderCode"", ""BuildingId"", ""ContractId"",
             ""PaymentId"", ""DepositSettlementId"", ""BankReviewId"",
             ""OwnerApprovalId"", ""SourceKey"", ""BankReference"",
             ""AmountVnd"", ""DocumentSha256"", ""ReviewerId"",
             ""OwnerApproverId"", ""CreatedAtUtc"")
            VALUES ({receiptId}, {plan.IntentId}, {plan.OrderCode}, {plan.BuildingId}, {contract.Id},
                    {payment.Id}, {settlement.Id}, {reviewed.BankReviewId},
                    {reviewed.OwnerApprovalId}, {plan.SourceKey},
                    {plan.BankReference}, {plan.AmountVnd},
                    {plan.EvidenceSha256}, {plan.BankReviewerId},
                    {plan.OwnerApproverId}, {paidAt})", cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""BuildingManagementPayOsD04Outbox""
                (""Id"", ""ReceiptId"", ""SourceKey"", ""EventType"", ""CreatedAtUtc"")
            VALUES ({outboxId}, {receiptId}, {plan.SourceKey},
                    {"D04PayOsReceiptCommitted"}, {paidAt})", cancellationToken);

        // Commit legacy cash ONCE. Mark payOS Posted only in a separate
        // later read-back/ack transaction with the authoritative journal.
        await transaction.CommitAsync(cancellationToken);
        // IMPORTANT: even after this commit, the coordinator must re-read
        // the committed journal and only THEN consider payOS ACK. Returning
        // MarkPayOsPostedAfterLegacyProof here would be a false assertion.
        return new PayOsD04LegacyWriteResult(
            receiptId, settlement.Id, outboxId,
            PayOsD04LegacyWriteDisposition.NewlyCommittedPendingReadBack);
    }

    private async Task<(PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox)>
        ReadSourceAsync(Guid intentId, CancellationToken ct)
    {
        var source = await _staging.ReadCurrentAsync(intentId, ct);
        if (source is null || source.Value.Intent.Id != intentId ||
            source.Value.Intent.Status is not (
                PayOsPaymentIntentStatus.ConfirmedPendingPosting or
                PayOsPaymentIntentStatus.Posted) ||
            source.Value.Inbox.Status != PayOsWebhookInboxStatus.ReconciledNoPosting ||
            source.Value.Inbox.PaymentIntentId != intentId ||
            source.Value.Inbox.BuildingId != source.Value.Intent.BuildingId ||
            source.Value.Inbox.OrderCode != source.Value.Intent.OrderCode ||
            source.Value.Inbox.ProviderReference != source.Value.Intent.ProviderReference)
            throw Denied("Both persisted payOS confirmation and signed inbox are required.");
        return source.Value;
    }

    private async Task<DurableReview?> ReadCommittedReviewAsync(
        IDbContextTransaction tx, Guid intentId, CancellationToken ct)
    {
        const string sql = """
            SELECT b."Id",a."Id",b."BuildingId",b."PaymentId",b."OrderCode",b."AmountVnd",
                   b."BankReference",b."DocumentSha256",b."ReviewerId",
                   a."OwnerApproverId",b."VerifiedAtUtc",a."ApprovedAtUtc",
                   a."ExpiresAtUtc"
              FROM "BuildingManagementPayOsD04BankReviews" b
              JOIN "BuildingManagementPayOsD04OwnerApprovals" a
                ON a."BankReviewId"=b."Id"
               AND a."ReviewerId"=b."ReviewerId"
               AND a."DocumentSha256"=b."DocumentSha256"
             WHERE b."PayOsIntentId"=@intentId
             FOR SHARE OF b,a
            """;
        await using var cmd = Command(tx, sql);
        cmd.Parameters.AddWithValue("intentId", intentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var result = new DurableReview(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
            reader.GetGuid(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetString(6), reader.GetString(7), reader.GetGuid(8), reader.GetGuid(9),
            reader.GetDateTime(10), reader.GetDateTime(11), reader.GetDateTime(12));
        if (await reader.ReadAsync(ct))
            throw Denied("Multiple independent review/approval records for one intent.");
        return result;
    }

    private async Task<bool> JournalForIntentExistsAsync(
        IDbContextTransaction tx, Guid intentId, CancellationToken ct)
    {
        await using var cmd = Command(tx, """
            SELECT EXISTS(SELECT 1 FROM "BuildingManagementPayOsD04ReceiptJournal"
                          WHERE "PayOsIntentId"=@intentId)
            """);
        cmd.Parameters.AddWithValue("intentId", intentId);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ??
            throw Denied("D04 receipt presence could not be checked."));
    }

    private async Task<bool> JournalReferenceExistsAsync(
        IDbContextTransaction tx, string bankReference, CancellationToken ct)
    {
        await using var cmd = Command(tx, """
            SELECT EXISTS(SELECT 1 FROM "BuildingManagementPayOsD04ReceiptJournal"
                          WHERE "BankReference"=@bankReference)
            """);
        cmd.Parameters.AddWithValue("bankReference", bankReference);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ??
            throw Denied("Bank reference uniqueness could not be checked."));
    }

    private async Task<PayOsD04ReceiptOutboxProof?> ReadJournalProofAsync(
        IDbContextTransaction tx, Guid intentId, CancellationToken ct)
    {
        await using var cmd = Command(tx, """
            SELECT j."PayOsIntentId",j."BuildingId",j."PaymentId",j."Id",
                   j."DepositSettlementId",o."Id",j."SourceKey",
                   j."BankReference",j."AmountVnd",s."Amount",
                   j."DocumentSha256",j."OwnerApproverId",
                   j."CreatedAtUtc",o."CreatedAtUtc",
                   (SELECT count(*) FROM "BuildingManagementPayOsD04ReceiptJournal"
                       WHERE "PayOsIntentId"=@intentId),
                   (SELECT count(*) FROM "BuildingManagementDepositSettlements"
                       WHERE "Id"=j."DepositSettlementId"
                         AND "PaymentId"=j."PaymentId"
                         AND "Type"=3 AND "Status"=2
                         AND "TargetContractId"=j."ContractId"),
                   (SELECT count(*) FROM "BuildingManagementPayOsD04Outbox"
                       WHERE "ReceiptId"=j."Id" AND "SourceKey"=j."SourceKey"),
                   (SELECT count(*) FROM "BuildingManagementPayOsD04ReceiptJournal"
                       WHERE "BankReference"=j."BankReference")
              FROM "BuildingManagementPayOsD04ReceiptJournal" j
              JOIN "BuildingManagementPayments" p
                ON p."Id"=j."PaymentId"
               AND p."ContractId"=j."ContractId"
               AND p."Status"=2 AND p."IsDeleted"=FALSE
               AND p."TransactionReference"=j."BankReference"
               AND p."Amount"=j."AmountVnd"
              JOIN "BuildingManagementDepositSettlements" s
                ON s."Id"=j."DepositSettlementId"
               AND s."PaymentId"=p."Id"
               AND s."TargetContractId"=p."ContractId"
               AND s."Status"=2 AND s."Type"=3
               AND s."Amount"=j."AmountVnd"
              JOIN "BuildingManagementPayOsD04Outbox" o
                ON o."ReceiptId"=j."Id" AND o."SourceKey"=j."SourceKey"
             WHERE j."PayOsIntentId"=@intentId
            """);
        cmd.Parameters.AddWithValue("intentId", intentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var proof = new PayOsD04ReceiptOutboxProof(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
            reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5),
            reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
            (long)reader.GetDecimal(9), reader.GetString(10),
            reader.GetGuid(11), checked((int)reader.GetInt64(14)),
            checked((int)reader.GetInt64(15)),
            checked((int)reader.GetInt64(16)),
            checked((int)reader.GetInt64(17)),
            reader.GetDateTime(12), reader.GetDateTime(13));
        if (await reader.ReadAsync(ct))
            throw Denied("Multiple legacy D04 receipts for one intent.");
        return proof;
    }

    private NpgsqlCommand Command(IDbContextTransaction tx, string sql)
    {
        var connection = _db.Database.GetDbConnection() as NpgsqlConnection ??
            throw Denied("D04 requires a PostgreSQL BuildingManagement database.");
        var transaction = tx.GetDbTransaction() as NpgsqlTransaction ??
            throw Denied("D04 requires a PostgreSQL transaction.");
        return new NpgsqlCommand(sql, connection, transaction);
    }

    private static InvalidOperationException Denied(string reason) =>
        new("D04 posting HOLD: " + reason);

    private sealed record DurableReview(
        Guid BankReviewId, Guid OwnerApprovalId, Guid BuildingId,
        Guid PaymentId, long OrderCode, long AmountVnd, string BankReference,
        string DocumentSha256, Guid BankReviewerId, Guid OwnerApproverId,
        DateTime VerifiedAtUtc, DateTime ApprovedAtUtc, DateTime ExpiresAtUtc);
}

public enum PayOsD04LegacyWriteDisposition
{
    NewlyCommittedPendingReadBack = 1,
    ExistingCommittedNeedsAck = 2,
    ExistingAlreadyAcknowledged = 3
}

/// <summary>
/// Durable financial commit result, NOT a payOS marker-update permission.
/// The coordinator must read the authoritative journal after commit.
/// </summary>
public sealed record PayOsD04LegacyWriteResult(
    Guid ReceiptId, Guid DepositSettlementId, Guid OutboxId,
    PayOsD04LegacyWriteDisposition Disposition);
