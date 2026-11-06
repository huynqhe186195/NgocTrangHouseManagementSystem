using System;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Internal staging-only payOS notification inbox writer. This class is NOT
/// exposed as an endpoint, registered for production or permitted to post D04.
/// A caller must supply a server-held checksum key, never a tenant-provided key.
///
/// Order row lock serializes webhook attempts; globally unique transaction
/// references provide a second DB barrier to cross-order double attribution.
/// No bank success is inferred and NO Payment.CompleteAsync is invoked.
/// </summary>
public sealed class PayOsStagingWebhookInboxWriter
{
    private readonly PayOsPaymentMigrationDbContext _db;
    private readonly string _checksumKey;

    public PayOsStagingWebhookInboxWriter(
        PayOsPaymentMigrationDbContext db, string checksumKey)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(checksumKey))
            throw new ArgumentException("A server-held payOS checksum key is required.", nameof(checksumKey));
        _checksumKey = checksumKey;
    }

    public async Task<PayOsWebhookIngestResult> RecordAsync(
        ReadOnlyMemory<byte> rawBody,
        CancellationToken cancellationToken = default)
    {
        if (rawBody.IsEmpty || rawBody.Length > PayOsWebhookIntakeGuard.MaximumBodyBytes)
            return PayOsWebhookIngestResult.Rejected;

        long orderCode;
        try
        {
            using var json = JsonDocument.Parse(rawBody);
            if (!PayOsPaymentWebhookSignature.IsValid(json.RootElement, _checksumKey) ||
                !json.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("orderCode", out var order) ||
                order.ValueKind != JsonValueKind.Number ||
                !order.TryGetInt64(out orderCode) || orderCode <= 0)
                return PayOsWebhookIngestResult.Rejected;
        }
        catch (JsonException) { return PayOsWebhookIngestResult.Rejected; }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // Lock the authoritative intent before reading state or writing inbox.
        // FromSqlRaw is NOT composed by EF; AsAsyncEnumerable executes it as-is.
        PayOsPaymentIntent? intent = null;
        await foreach (var candidate in _db.PayOsPaymentIntents
            .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsPaymentIntents""
                WHERE ""OrderCode"" = {orderCode} FOR UPDATE")
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (intent is not null) return PayOsWebhookIngestResult.Conflict;
            intent = candidate;
        }

        if (intent is null)
            return PayOsWebhookIngestResult.UnknownIntent;

        var validated = PayOsWebhookIntakeGuard.Prepare(rawBody, intent, _checksumKey);
        if (validated is null ||
            validated.BuildingId != intent.BuildingId ||
            validated.PaymentIntentId != intent.Id ||
            validated.OrderCode != intent.OrderCode)
            return PayOsWebhookIngestResult.Rejected;

        // Different order or payload with the same bank reference is never
        // silently treated as an idempotent repeat.
        var prior = await _db.PayOsWebhookInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderReference == validated.ProviderReference,
                cancellationToken);
        if (prior is not null)
            return prior.PaymentIntentId == validated.PaymentIntentId &&
                   prior.BuildingId == validated.BuildingId &&
                   prior.OrderCode == validated.OrderCode &&
                   prior.PayloadSha256 == validated.PayloadSha256
                ? PayOsWebhookIngestResult.Duplicate
                : PayOsWebhookIngestResult.Conflict;

        var record = new PayOsWebhookInboxRecord(
            Guid.NewGuid(), intent.BuildingId, intent.Id,
            intent.OrderCode, validated.ProviderReference, validated.PayloadSha256,
            DateTime.UtcNow);

        // Funds received after cancellation, or after any already-confirmed
        // status, MUST enter independent manual review, not automatic posting.
        var manual = intent.Status != PayOsPaymentIntentStatus.AwaitingPayment;
        if (manual)
            record.RequireManualReview(DateTime.UtcNow);

        _db.PayOsWebhookInbox.Add(record);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return manual
                ? PayOsWebhookIngestResult.StoredManualReview
                : PayOsWebhookIngestResult.StoredPendingReconciliation;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException pg &&
            pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            // Do not leave a failed Added entity pending on this DbContext.
            _db.Entry(record).State = EntityState.Detached;
            // Let a retry with a fresh context distinguish an exact duplicate
            // from a conflicting reference after a concurrent unique violation.
            return PayOsWebhookIngestResult.ConcurrentConflictRetry;
        }
    }
}

public enum PayOsWebhookIngestResult
{
    Rejected = 0,
    UnknownIntent = 1,
    StoredPendingReconciliation = 2,
    StoredManualReview = 3,
    Duplicate = 4,
    Conflict = 5,
    ConcurrentConflictRetry = 6
}
