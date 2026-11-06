using System;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Payments;

/// <summary>
/// Staged payOS webhook inbox record. It is evidence of an accepted, signed
/// notification, never evidence that an invoice is paid or a cash journal posted.
/// The ingress writer MUST enforce uniqueness transactionally in PostgreSQL.
/// </summary>
public sealed class PayOsWebhookInboxRecord : AggregateRoot<Guid>
{
    public Guid BuildingId { get; private set; }
    public Guid PaymentIntentId { get; private set; }
    public long OrderCode { get; private set; }
    public string ProviderReference { get; private set; } = default!;
    public string PayloadSha256 { get; private set; } = default!;
    public DateTime ReceivedAtUtc { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public PayOsWebhookInboxStatus Status { get; private set; }

    private PayOsWebhookInboxRecord() { }

    public PayOsWebhookInboxRecord(Guid id, Guid buildingId, Guid paymentIntentId,
        long orderCode, string providerReference, string payloadSha256, DateTime receivedAtUtc)
        : base(id)
    {
        if (id == Guid.Empty || buildingId == Guid.Empty || paymentIntentId == Guid.Empty)
            throw new ArgumentException("Inbox, building and intent IDs must be nonempty.");
        if (orderCode <= 0) throw new ArgumentOutOfRangeException(nameof(orderCode));
        if (string.IsNullOrWhiteSpace(providerReference) ||
            providerReference.Trim().Length > 128)
            throw new ArgumentException("A bounded provider reference is required.", nameof(providerReference));
        if (string.IsNullOrWhiteSpace(payloadSha256) ||
            payloadSha256.Length != 64 || !IsLowerHex(payloadSha256))
            throw new ArgumentException("SHA256 digest must contain 64 lowercase hexadecimal characters.", nameof(payloadSha256));
        if (receivedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Receipt time must be UTC.", nameof(receivedAtUtc));

        BuildingId = buildingId;
        PaymentIntentId = paymentIntentId;
        OrderCode = orderCode;
        ProviderReference = providerReference.Trim();
        PayloadSha256 = payloadSha256;
        ReceivedAtUtc = receivedAtUtc;
        Status = PayOsWebhookInboxStatus.PendingReconciliation;
    }

    public void RequireManualReview(DateTime reviewedAtUtc)
    {
        EnsurePending();
        EnsureUtc(reviewedAtUtc);
        ReviewedAtUtc = reviewedAtUtc;
        Status = PayOsWebhookInboxStatus.RequiresManualReview;
    }

    public void MarkReconciled(DateTime reviewedAtUtc)
    {
        EnsurePending();
        EnsureUtc(reviewedAtUtc);
        ReviewedAtUtc = reviewedAtUtc;
        Status = PayOsWebhookInboxStatus.ReconciledNoPosting;
    }

    private void EnsurePending()
    {
        if (Status != PayOsWebhookInboxStatus.PendingReconciliation)
            throw new InvalidOperationException("Webhook inbox events cannot be processed twice.");
    }

    private static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Review timestamp must be UTC.");
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }
}

public enum PayOsWebhookInboxStatus
{
    PendingReconciliation = 0,
    ReconciledNoPosting = 1,
    RequiresManualReview = 2
}
