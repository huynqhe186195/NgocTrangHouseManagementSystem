using System;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Payments;

/// <summary>
/// Internal record for a payOS checkout request. An intent is NOT a receipt.
/// All methods are state-local; external workers must use a transaction, DB
/// row lock and unique indexes before writing a receipt or financial journal.
/// </summary>
public sealed class PayOsPaymentIntent : AggregateRoot<Guid>
{
    public Guid BuildingId { get; private set; }
    public Guid PayerId { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public long OrderCode { get; private set; }
    public long AmountVnd { get; private set; }
    public string? PaymentLinkId { get; private set; }
    public string? ProviderReference { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }
    public PayOsPaymentIntentStatus Status { get; private set; }

    private PayOsPaymentIntent() { }

    public PayOsPaymentIntent(Guid id, Guid buildingId, Guid payerId,
        long orderCode, long amountVnd, DateTime expiresAtUtc,
        Guid? invoiceId = null, Guid? paymentId = null) : base(id)
    {
        if (id == Guid.Empty || buildingId == Guid.Empty || payerId == Guid.Empty)
            throw new ArgumentException("Intent, building and payer IDs are required.");
        if (invoiceId.HasValue == paymentId.HasValue)
            throw new ArgumentException("Exactly one payment target is required.");
        if (invoiceId == Guid.Empty || paymentId == Guid.Empty)
            throw new ArgumentException("Payment target must not be empty.");
        if (orderCode <= 0 || amountVnd <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountVnd));
        if (expiresAtUtc.Kind != DateTimeKind.Utc || expiresAtUtc <= DateTime.UtcNow)
            throw new ArgumentException("Expiry must be in the future, UTC.", nameof(expiresAtUtc));
        BuildingId = buildingId;
        PayerId = payerId;
        OrderCode = orderCode;
        AmountVnd = amountVnd;
        ExpiresAtUtc = expiresAtUtc;
        InvoiceId = invoiceId;
        PaymentId = paymentId;
        Status = PayOsPaymentIntentStatus.Created;
    }

    public void AttachProviderLink(string paymentLinkId)
    {
        if (Status != PayOsPaymentIntentStatus.Created)
            throw new InvalidOperationException("Provider link can only be attached to a newly created intent.");
        if (string.IsNullOrWhiteSpace(paymentLinkId) || paymentLinkId.Trim().Length > 128)
            throw new ArgumentException("Invalid provider payment-link ID.", nameof(paymentLinkId));
        PaymentLinkId = paymentLinkId.Trim();
        Status = PayOsPaymentIntentStatus.AwaitingPayment;
    }

    public void MarkConfirmed(string reference, DateTime confirmedAtUtc)
    {
        if (Status != PayOsPaymentIntentStatus.AwaitingPayment || PaymentLinkId is null)
            throw new InvalidOperationException("Only an awaiting linked intent may be confirmed.");
        if (string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 128)
            throw new ArgumentException("Bank transaction reference is required.", nameof(reference));
        if (confirmedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Confirmation must use UTC.", nameof(confirmedAtUtc));
        // An intent may have expired before delayed provider confirmation.
        // Reconcile the authoritative payment time separately; never infer it
        // from the webhook receipt arrival time.
        ProviderReference = reference.Trim();
        ConfirmedAtUtc = confirmedAtUtc;
        Status = PayOsPaymentIntentStatus.ConfirmedPendingPosting;
    }

    public void MarkPosted()
    {
        if (Status != PayOsPaymentIntentStatus.ConfirmedPendingPosting)
            throw new InvalidOperationException("Posting requires durable confirmation and independent journal checks.");
        Status = PayOsPaymentIntentStatus.Posted;
    }

    public void CancelUnpaid()
    {
        if (Status is not (PayOsPaymentIntentStatus.Created or PayOsPaymentIntentStatus.AwaitingPayment))
            throw new InvalidOperationException("Never cancel confirmed or posted payment intents.");
        Status = PayOsPaymentIntentStatus.Cancelled;
    }
}

public enum PayOsPaymentIntentStatus
{
    Created = 0,
    AwaitingPayment = 1,
    ConfirmedPendingPosting = 2,
    Posted = 3,
    Cancelled = 4
}
