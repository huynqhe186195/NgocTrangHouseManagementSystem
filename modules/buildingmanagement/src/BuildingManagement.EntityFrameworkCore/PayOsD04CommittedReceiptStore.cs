using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Internal-only source of committed BuildingManagement-side D04 financial
/// evidence. The returned plan/proof are assembled from the REAL legacy
/// tables; a payOS webhook, caller or DTO must never supply them.
/// </summary>
public interface IPayOsD04CommittedReceiptStore
{
    Task<PayOsD04CommittedReadBack?> ReadCommittedAsync(
        Guid intentId, CancellationToken cancellationToken);

    /// <summary>
    /// Acknowledge the legacy outbox ONLY AFTER the separate payOS staging
    /// transaction has durably marked Posted. Retry is idempotent.
    /// </summary>
    Task MarkOutboxAcknowledgedAsync(
        Guid outboxId, string sourceKey, CancellationToken cancellationToken);
}

public sealed record PayOsD04CommittedReadBack(
    PayOsD04LegacyCommitPlan Plan,
    PayOsD04ReceiptOutboxProof Proof,
    Payment Payment,
    bool OutboxAcknowledged);

/// <summary>
/// This implementation is deliberately NOT registered in runtime DI.
/// The actual D04 posting/ACK feature remains disabled until authenticated
/// independent bank review and revocation-safe Owner approval are delivered.
/// </summary>
public sealed class PayOsD04CommittedReceiptStore : IPayOsD04CommittedReceiptStore
{
    private readonly BuildingManagementDbContext _db;

    public PayOsD04CommittedReceiptStore(BuildingManagementDbContext db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<PayOsD04CommittedReadBack?> ReadCommittedAsync(
        Guid intentId, CancellationToken cancellationToken)
    {
        if (intentId == Guid.Empty)
            throw new ArgumentException("Persisted intent required.", nameof(intentId));

        // Same MVCC snapshot for the actual Payment row and the signed
        // journal/settlement/outbox, never a Boolean claim from payOS.
        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var connection = Connection();
        var transaction = PostgreSqlTransaction(tx);
        const string sql = """
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
                      AND "TargetContractId"=j."ContractId"
                      AND "Type"=3 AND "Status"=2
                      AND "Amount"=j."AmountVnd"),
                   (SELECT count(*) FROM "BuildingManagementPayOsD04Outbox"
                    WHERE "ReceiptId"=j."Id" AND "SourceKey"=j."SourceKey"),
                   (SELECT count(*) FROM "BuildingManagementPayOsD04ReceiptJournal"
                    WHERE "BankReference"=j."BankReference"),
                   j."ReviewerId",j."OwnerApproverId",o."AcknowledgedAtUtc",
                   j."OrderCode"
            FROM "BuildingManagementPayOsD04ReceiptJournal" j
            JOIN "BuildingManagementPayments" p
              ON p."Id"=j."PaymentId"
             AND p."ContractId"=j."ContractId"
             AND p."IsDeleted"=FALSE
             AND p."Status"=2 AND p."Purpose"=2
             AND p."Method"=2 AND p."Channel"=2
             AND p."DepositDisposition"=1
             AND p."TransactionReference"=j."BankReference"
             AND p."Amount"=j."AmountVnd"
            JOIN "BuildingManagementDepositSettlements" s
              ON s."Id"=j."DepositSettlementId"
             AND s."PaymentId"=p."Id"
             AND s."TargetContractId"=j."ContractId"
             AND s."Status"=2 AND s."Type"=3
             AND s."Amount"=j."AmountVnd"
            JOIN "BuildingManagementPayOsD04BankReviews" b
              ON b."Id"=j."BankReviewId"
             AND b."PayOsIntentId"=j."PayOsIntentId"
             AND b."OrderCode"=j."OrderCode"
             AND b."BuildingId"=j."BuildingId"
             AND b."PaymentId"=j."PaymentId"
             AND b."BankReference"=j."BankReference"
             AND b."AmountVnd"=j."AmountVnd"
             AND b."DocumentSha256"=j."DocumentSha256"
             AND b."ReviewerId"=j."ReviewerId"
            JOIN "BuildingManagementPayOsD04OwnerApprovals" a
              ON a."Id"=j."OwnerApprovalId"
             AND a."BankReviewId"=b."Id"
             AND a."ReviewerId"=b."ReviewerId"
             AND a."DocumentSha256"=b."DocumentSha256"
             AND a."OwnerApproverId"=j."OwnerApproverId"
             AND a."OwnerApproverId"<>a."ReviewerId"
            JOIN "BuildingManagementPayOsD04Outbox" o
              ON o."ReceiptId"=j."Id" AND o."SourceKey"=j."SourceKey"
            WHERE j."PayOsIntentId"=@intentId
            """;
        PayOsD04ReceiptOutboxProof? proof = null;
        PayOsD04LegacyCommitPlan? plan = null;
        var acknowledged = false;
        await using (var cmd = new NpgsqlCommand(sql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("intentId", intentId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                proof = new PayOsD04ReceiptOutboxProof(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5),
                    reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
                    checked((long)reader.GetDecimal(9)), reader.GetString(10),
                    reader.GetGuid(11), checked((int)reader.GetInt64(14)),
                    checked((int)reader.GetInt64(15)),
                    checked((int)reader.GetInt64(16)),
                    checked((int)reader.GetInt64(17)),
                    reader.GetDateTime(12), reader.GetDateTime(13));
                plan = new PayOsD04LegacyCommitPlan(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.GetInt64(21),
                    reader.GetInt64(8), reader.GetString(7),
                    reader.GetString(6), reader.GetString(10),
                    reader.GetGuid(18), reader.GetGuid(19));
                acknowledged = !reader.IsDBNull(20);
                if (await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Multiple receipts for one D04 intent.");
            }
        }

        if (proof is null || plan is null)
        {
            // If the source journal exists but any JOIN is broken, refuse to
            // classify it as "not posted". The coordinator must investigate.
            await using var exists = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM "BuildingManagementPayOsD04ReceiptJournal"
                              WHERE "PayOsIntentId"=@intentId)
                """, connection, transaction);
            exists.Parameters.AddWithValue("intentId", intentId);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false))
                throw new InvalidOperationException(
                    "Committed D04 receipt lacks matching Payment, settlement, bank review, approval or outbox.");
            return null;
        }

        var payment = await _db.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == proof.PaymentId, cancellationToken);
        if (payment is null || payment.Status != PaymentStatus.Completed ||
            payment.Amount != proof.AmountVnd ||
            payment.TransactionReference != proof.BankReference)
            throw new InvalidOperationException("D04 legacy Payment read-back conflicts with receipt.");

        // OrderCode comes from the immutable BuildingManagement journal and
        // matched bank review, not from the caller or payOS webhook.
        await tx.CommitAsync(cancellationToken);
        return new PayOsD04CommittedReadBack(plan, proof, payment, acknowledged);
    }

    public async Task MarkOutboxAcknowledgedAsync(
        Guid outboxId, string sourceKey, CancellationToken cancellationToken)
    {
        if (outboxId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey))
            throw new ArgumentException("D04 immutable outbox ID and source key required.");

        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var changed = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "BuildingManagementPayOsD04Outbox"
               SET "AcknowledgedAtUtc"=COALESCE("AcknowledgedAtUtc", now()),
                   "AttemptCount"=CASE WHEN "AcknowledgedAtUtc" IS NULL
                                      THEN "AttemptCount"+1 ELSE "AttemptCount" END
             WHERE "Id"={outboxId} AND "SourceKey"={sourceKey}
            """, cancellationToken);
        if (changed != 1)
            throw new InvalidOperationException(
                "No exact legacy D04 outbox row matches this immutable source.");
        await tx.CommitAsync(cancellationToken);
    }

    private NpgsqlConnection Connection() =>
        _db.Database.GetDbConnection() as NpgsqlConnection ??
        throw new InvalidOperationException("D04 requires a PostgreSQL Building DB.");

    private static NpgsqlTransaction PostgreSqlTransaction(IDbContextTransaction tx) =>
        tx.GetDbTransaction() as NpgsqlTransaction ??
        throw new InvalidOperationException("D04 must use a PostgreSQL transaction.");
}
