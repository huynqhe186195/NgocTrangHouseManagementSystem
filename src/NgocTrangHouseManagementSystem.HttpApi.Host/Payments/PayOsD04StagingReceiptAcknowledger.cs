using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Internal source-only D04 ACK coordinator. No controller, hosted worker,
/// DI registration or public invocation is enabled. It does not post money.
/// The irreversible legacy Payment + settlement + receipt + outbox must
/// ALREADY be committed in a different database before this service runs.
///
/// Order is deliberate:
/// 1. read exact committed BuildingManagement proof (not provider PAID);
/// 2. lock payOS staging intent and validate signed/reconciled inbox;
/// 3. commit payOS Posted marker only when legacy evidence matches;
/// 4. mark BuildingManagement outbox acknowledged separately.
///
/// A crash between 3 and 4 is replay-safe: step 2 reads already Posted and
/// step 4 repeats idempotently. A crash before 3 leaves outbox unacknowledged.
/// </summary>
public sealed class PayOsD04StagingReceiptAcknowledger
{
    private readonly PayOsPaymentMigrationDbContext _staging;
    private readonly IPayOsD04CommittedReceiptStore _legacy;

    public PayOsD04StagingReceiptAcknowledger(
        PayOsPaymentMigrationDbContext staging,
        IPayOsD04CommittedReceiptStore legacy)
    {
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
    }

    public async Task<PayOsD04AckOutcome> AcknowledgeAsync(
        Guid intentId, CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty)
            throw new ArgumentException("A persisted payOS intent ID is required.", nameof(intentId));

        var committed = await _legacy.ReadCommittedAsync(intentId, cancellationToken);
        if (committed is null)
            return PayOsD04AckOutcome.NoCommittedFinancialJournal;

        await using var tx = await _staging.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        PayOsPaymentIntent? locked = null;
        await foreach (var intent in _staging.PayOsPaymentIntents
            .FromSqlInterpolated($@"SELECT * FROM ""BuildingManagementPayOsPaymentIntents""
                                  WHERE ""Id"" = {intentId} FOR UPDATE")
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            locked = intent;
        }
        if (locked is null ||
            locked.PaymentId != committed.Plan.PaymentId ||
            locked.BuildingId != committed.Plan.BuildingId ||
            locked.OrderCode != committed.Plan.OrderCode ||
            locked.AmountVnd != committed.Plan.AmountVnd ||
            locked.Status is not (
                PayOsPaymentIntentStatus.ConfirmedPendingPosting or
                PayOsPaymentIntentStatus.Posted))
            throw new InvalidOperationException(
                "D04 source staging intent is absent or conflicts with the committed legacy journal.");

        // Reader MUST fetch signed inbox from persistent staging DB. Never
        // accept a webhook payload or a caller-supplied confirmation Boolean.
        var inbox = await _staging.PayOsWebhookInbox.AsNoTracking()
            .Where(x => x.PaymentIntentId == locked.Id &&
                x.BuildingId == locked.BuildingId && x.OrderCode == locked.OrderCode)
            .Take(2).ToListAsync(cancellationToken);
        if (inbox.Count != 1 || inbox[0].Status !=
            PayOsWebhookInboxStatus.ReconciledNoPosting)
            throw new InvalidOperationException(
                "D04 requires exactly one signed, previously reconciled inbox row.");

        var decision = PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
            locked, inbox[0], committed.Payment, committed.Plan, committed.Proof);
        if (decision == PayOsD04RecoveryDecision.StopAndInvestigate)
            throw new InvalidOperationException(
                "D04 legacy Payment/receipt/settlement/outbox does not match the staged source.");

        if (committed.OutboxAcknowledged && decision !=
            PayOsD04RecoveryDecision.AlreadyAcknowledged)
            throw new InvalidOperationException(
                "Legacy outbox was acknowledged before payOS Posted; investigate cross-database inconsistency.");

        if (decision == PayOsD04RecoveryDecision.MarkPayOsPostedAfterLegacyProof)
        {
            locked.MarkPosted();
            await _staging.SaveChangesAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);

        // The legacy outbox ACK must be an independently committed update.
        // It is intentionally AFTER the durable payOS marker. If it fails,
        // the next attempt must read the same journal and retry only ACK.
        await _legacy.MarkOutboxAcknowledgedAsync(
            committed.Proof.OutboxId, committed.Proof.SourceKey,
            cancellationToken);

        return decision == PayOsD04RecoveryDecision.AlreadyAcknowledged
            ? PayOsD04AckOutcome.RecoveredAlreadyPosted
            : PayOsD04AckOutcome.PostedAndOutboxAcknowledged;
    }
}

public enum PayOsD04AckOutcome
{
    NoCommittedFinancialJournal = 0,
    PostedAndOutboxAcknowledged = 1,
    RecoveredAlreadyPosted = 2
}
