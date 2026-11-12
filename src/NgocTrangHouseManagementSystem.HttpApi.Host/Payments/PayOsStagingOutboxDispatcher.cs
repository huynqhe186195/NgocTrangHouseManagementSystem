using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Outbox dispatcher for the isolated payOS staging lane (P1.5 "timeout
/// handling with outbox pattern"). Registered ONLY together with the
/// internal simulated provider — never with the live payOS HTTP client —
/// so it can never generate autonomous real-provider traffic.
///
/// Processing rules (fail-closed):
/// - AttachProviderLink: performs the SAME recovery the checkout endpoint
///   would do — a read-only provider FindAsync for the reserved orderCode,
///   then the durable AttachProviderLink transaction. Provider ambiguity
///   (timeout/5xx) or InvalidOperationException routes to bounded
///   exponential backoff and finally RequiresReview; it NEVER creates a
///   different order code and NEVER ACKs payOS.
/// - ReconcileIntent: a read-only provider probe that records the observed
///   provider status on the row. Completion happens ONLY through the
///   operator reconciliation endpoint (permission-checked, signed-inbox
///   based); the dispatcher may never confirm an intent autonomously.
/// - No Payment is completed, no D04 journal is posted, and no webhook is
///   ACKed by this class.
/// </summary>
public sealed class PayOsStagingOutboxDispatcher : BackgroundService
{
    public const int DefaultMaxAttempts = 5;
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClaimBatchSize = TimeSpan.FromSeconds(0);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PayOsStagingOutboxDispatcher> _logger;
    private readonly IPayOsPaymentLinkProvider _provider;
    private readonly int _maxAttempts;

    public PayOsStagingOutboxDispatcher(
        IServiceScopeFactory scopes,
        ILogger<PayOsStagingOutboxDispatcher> logger,
        IPayOsPaymentLinkProvider provider,
        int maxAttempts = DefaultMaxAttempts)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _maxAttempts = maxAttempts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessDueAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(IdlePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never crash the Host on a dispatcher iteration; the next
                // loop re-reads the durable queue state from PostgreSQL.
                _logger.LogWarning(ex,
                    "payOS staging outbox dispatch iteration failed; will retry.");
                try { await Task.Delay(IdlePollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// One dispatch pass over due pending rows. Returns the number of rows
    /// processed (success or failure recorded). Public for narrow tests.
    /// </summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<PayOsPaymentMigrationDbContext>();

        var now = DateTime.UtcNow;
        PayOsStagingOutboxRecord? due = null;
        await using (var tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken))
        {
            // SKIP LOCKED: multiple dispatcher instances (or a dispatcher
            // racing the checkout endpoint's own recovery) never process
            // the same row; the loser sees zero rows.
            await foreach (var row in db.PayOsStagingOutbox
                .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsStagingOutbox""
                    WHERE ""Status"" = 0 AND ""NextAttemptAtUtc"" <= {now}
                    ORDER BY ""NextAttemptAtUtc""
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED")
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                due = row;
            }
            if (due is null)
            {
                await tx.CommitAsync(cancellationToken);
                return 0;
            }
            await tx.CommitAsync(cancellationToken);
        }

        var outcome = due.Kind == PayOsStagingOutboxKind.AttachProviderLink
            ? await ProcessAttachAsync(db, due, cancellationToken)
            : await ProcessReconcileAsync(db, due, cancellationToken);
        return outcome ? 1 : 0;
    }

    private async Task<bool> ProcessAttachAsync(
        PayOsPaymentMigrationDbContext db, PayOsStagingOutboxRecord due,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read-only provider recovery for the SAME reserved orderCode —
            // exactly the FindAsync the checkout endpoint performs. Only a
            // definitive provider-side record authorizes the attach.
            var snapshot = await _provider.FindAsync(due.OrderCode, cancellationToken);
            if (snapshot is null)
            {
                // The provider never recorded this order: safe to leave the
                // row pending with backoff (the checkout endpoint remains
                // the only place a create is issued).
                RecordFailure(db, due, "provider has no record yet");
                return false;
            }

            if (snapshot.OrderCode != due.OrderCode ||
                snapshot.AmountPaidVnd < 0 || snapshot.AmountRemainingVnd < 0 ||
                snapshot.AmountPaidVnd > snapshot.AmountVnd ||
                snapshot.AmountPaidVnd != snapshot.AmountVnd - snapshot.AmountRemainingVnd)
                throw new InvalidOperationException(
                    "Ambiguous simulated provider recovery result; requires review.");

            await using var tx = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            PayOsPaymentIntent? locked = null;
            await foreach (var row in db.PayOsPaymentIntents
                .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsPaymentIntents""
                    WHERE ""Id"" = {due.PaymentIntentId} FOR UPDATE")
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                locked = row;
            }
            if (locked is null || locked.OrderCode != due.OrderCode ||
                locked.BuildingId != due.BuildingId ||
                locked.Status is PayOsPaymentIntentStatus.Cancelled or
                    PayOsPaymentIntentStatus.Posted)
                throw new InvalidOperationException(
                    "Outbox attach target intent is inconsistent; requires review.");

            if (locked.PaymentLinkId is null)
            {
                locked.AttachProviderLink(snapshot.PaymentLinkId);
                due.MarkAttachCompleted(DateTime.UtcNow);
            }
            else if (string.Equals(locked.PaymentLinkId, snapshot.PaymentLinkId,
                StringComparison.Ordinal))
            {
                due.MarkAttachCompleted(DateTime.UtcNow);
            }
            else
            {
                throw new InvalidOperationException(
                    "Provider link conflict on outbox attach; requires review.");
            }
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or
            InvalidOperationException or TaskCanceledException or TimeoutException)
        {
            RecordFailure(db, due, ex.Message);
            return false;
        }
    }

    private async Task<bool> ProcessReconcileAsync(
        PayOsPaymentMigrationDbContext db, PayOsStagingOutboxRecord due,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read-only provider probe: record what the provider currently
            // reports so the operator sees live evidence on the queue row.
            // The dispatcher NEVER completes the intent or the inbox row;
            // confirmation is exclusively the operator reconciliation
            // endpoint's permission-checked, signed-inbox-based transition.
            var snapshot = await _provider.FindAsync(due.OrderCode, cancellationToken);
            if (snapshot is not null)
            {
                await using var tx = await db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                PayOsStagingOutboxRecord? locked = null;
                await foreach (var row in db.PayOsStagingOutbox
                    .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsStagingOutbox""
                        WHERE ""Id"" = {due.Id} FOR UPDATE")
                    .AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    locked = row;
                }
                if (locked is not null && locked.Status == PayOsStagingOutboxStatus.Pending)
                {
                    locked.ObserveProviderStatus(snapshot.Status, DateTime.UtcNow);
                    await db.SaveChangesAsync(cancellationToken);
                }
                await tx.CommitAsync(cancellationToken);
            }
            // Reconcile rows stay pending until the operator reconciliation
            // completes them; report processed=1 for a successful probe.
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or
            TaskCanceledException or TimeoutException)
        {
            RecordFailure(db, due, ex.Message);
            return false;
        }
    }

    private void RecordFailure(
        PayOsPaymentMigrationDbContext db, PayOsStagingOutboxRecord row, string reason)
    {
        try
        {
            // Re-attach on a fresh tracked instance: the caller's context
            // may have rolled back or the row was loaded AsNoTracking in a
            // disposed scope. The bounded retry/backoff state must persist.
            var fresh = db.PayOsStagingOutbox.SingleOrDefault(
                x => x.Id == row.Id);
            if (fresh is not null && fresh.Status == PayOsStagingOutboxStatus.Pending)
            {
                fresh.RecordAttemptFailure(_maxAttempts, DateTime.UtcNow);
                db.SaveChanges();
            }
            _logger.LogWarning(
                "payOS staging outbox row {OutboxId} ({Kind}) attempt failed: {Reason}",
                row.Id, row.Kind, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist outbox attempt failure for row {OutboxId}.",
                row.Id);
        }
    }
}
