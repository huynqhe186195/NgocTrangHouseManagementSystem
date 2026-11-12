using System;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Payments;

/// <summary>
/// Durable work queue for the isolated payOS staging lane ("timeout handling
/// with outbox pattern"). Rows are inserted in the SAME transaction as the
/// durable state they describe, so an ambiguous provider timeout can never
/// lose or duplicate the follow-up work:
///
/// - AttachProviderLink: an order was reserved in the checkout intent
///   transaction, but the provider payment-link attach commit is still
///   outstanding (provider create succeeded, HTTP response was lost, or the
///   attach transaction failed after the provider call).
/// - ReconcileIntent: a signed webhook was durably stored in the inbox and
///   still needs operator-driven reconciliation (an inbox row exists).
///
/// An outbox row is NEVER evidence of payment or a bank transaction, and no
/// dispatcher may complete a Payment or post a D04 journal from it.
/// </summary>
public sealed class PayOsStagingOutboxRecord : AggregateRoot<Guid>
{
    public Guid PaymentIntentId { get; private set; }
    public Guid BuildingId { get; private set; }
    public long OrderCode { get; private set; }
    public PayOsStagingOutboxKind Kind { get; private set; }
    public PayOsStagingOutboxStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public string? LastObservedProviderStatus { get; private set; }
    public DateTime? LastAttemptedAtUtc { get; private set; }

    private PayOsStagingOutboxRecord() { }

    public PayOsStagingOutboxRecord(Guid id, Guid paymentIntentId, Guid buildingId,
        long orderCode, PayOsStagingOutboxKind kind, DateTime createdAtUtc) : base(id)
    {
        if (id == Guid.Empty || paymentIntentId == Guid.Empty || buildingId == Guid.Empty)
            throw new ArgumentException("Outbox, intent and building IDs must be nonempty.");
        if (orderCode <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderCode));
        if (createdAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Outbox creation time must be UTC.", nameof(createdAtUtc));

        PaymentIntentId = paymentIntentId;
        BuildingId = buildingId;
        OrderCode = orderCode;
        Kind = kind;
        Status = PayOsStagingOutboxStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = createdAtUtc;
        AttemptCount = 0;
    }

    /// <summary>
    /// An attach attempt failed (provider ambiguity or lock contention).
    /// Bounded exponential backoff; a record past the attempt ceiling is
    /// routed to independent review instead of retrying forever.
    /// </summary>
    public void RecordAttemptFailure(int maxAttempts, DateTime attemptedAtUtc)
    {
        if (Status != PayOsStagingOutboxStatus.Pending)
            throw new InvalidOperationException("Only a pending outbox record can record attempts.");
        if (attemptedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Attempt time must be UTC.", nameof(attemptedAtUtc));
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        AttemptCount++;
        LastAttemptedAtUtc = attemptedAtUtc;
        if (AttemptCount >= maxAttempts)
        {
            Status = PayOsStagingOutboxStatus.RequiresReview;
            NextAttemptAtUtc = attemptedAtUtc;
        }
        else
        {
            // 2^attempt bounded seconds: 2, 4, 8, 16 ... capped at 1 hour.
            var delaySeconds = Math.Min(
                (long)1 << Math.Min(AttemptCount, 12), 3600);
            NextAttemptAtUtc = attemptedAtUtc.AddSeconds(delaySeconds);
        }
    }

    /// <summary>
    /// AttachProviderLink work is finished: the intent row now durably
    /// carries the provider payment link.
    /// </summary>
    public void MarkAttachCompleted(DateTime completedAtUtc)
    {
        if (Kind != PayOsStagingOutboxKind.AttachProviderLink ||
            Status != PayOsStagingOutboxStatus.Pending)
            throw new InvalidOperationException(
                "Only a pending attach outbox record can be completed.");
        EnsureUtc(completedAtUtc);
        CompletedAtUtc = completedAtUtc;
        Status = PayOsStagingOutboxStatus.Completed;
    }

    /// <summary>
    /// ReconcileIntent work is finished: the intent is confirmed pending
    /// posting and the inbox row is reconciled. Completed by the operator
    /// reconciliation path, never autonomously by a dispatcher.
    /// </summary>
    public void MarkReconcileCompleted(DateTime completedAtUtc)
    {
        if (Kind != PayOsStagingOutboxKind.ReconcileIntent ||
            Status != PayOsStagingOutboxStatus.Pending)
            throw new InvalidOperationException(
                "Only a pending reconcile outbox record can be completed.");
        EnsureUtc(completedAtUtc);
        CompletedAtUtc = completedAtUtc;
        Status = PayOsStagingOutboxStatus.Completed;
    }

    /// <summary>
    /// Read-only provider observation recorded during an attempt (for
    /// example PENDING/PAID/CANCELLED). Diagnostics only; never money state.
    /// </summary>
    public void ObserveProviderStatus(string? status, DateTime observedAtUtc)
    {
        if (Status != PayOsStagingOutboxStatus.Pending)
            throw new InvalidOperationException("Only a pending record observes provider status.");
        if (observedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Observation time must be UTC.", nameof(observedAtUtc));
        if (status is not null && (status.Length == 0 || status.Length > 32))
            throw new ArgumentException("Provider status must be a bounded nonempty string or null.", nameof(status));
        LastObservedProviderStatus = status;
        LastAttemptedAtUtc = observedAtUtc;
    }

    private static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Completion time must be UTC.");
    }
}

public enum PayOsStagingOutboxKind
{
    AttachProviderLink = 1,
    ReconcileIntent = 2
}

public enum PayOsStagingOutboxStatus
{
    Pending = 0,
    Completed = 1,
    RequiresReview = 2
}
