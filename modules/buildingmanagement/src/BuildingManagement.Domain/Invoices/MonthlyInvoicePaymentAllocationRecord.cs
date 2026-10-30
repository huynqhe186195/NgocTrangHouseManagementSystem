using System;
using BuildingManagement.Payments;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// One immutable completed bank-receipt claim allocated exclusively to one
/// MONTHLY staging-issued invoice. No gateway verification or deposit offset
/// is implied. Its original receipt remains frozen after allocation.
/// </summary>
public sealed class MonthlyInvoicePaymentAllocationRecord : Entity<Guid>
{
    public Guid PaymentId { get; private set; }
    public Guid IssuedInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public decimal Amount { get; private set; }
    public string PaymentNumberSnapshot { get; private set; } = string.Empty;
    public DateTime PaidAtUtc { get; private set; }
    public string? BankTransactionReferenceSnapshot { get; private set; }
    public Guid RecordedByUserId { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }

    public bool IsIndependentlyVerifiedBankPayment => false;
    public bool IsPostedDebtSettlement => false;
    public bool CanOffsetDeposit => false;

    private MonthlyInvoicePaymentAllocationRecord() { }
    private MonthlyInvoicePaymentAllocationRecord(Guid id) : base(id) { }

    public static MonthlyInvoicePaymentAllocationRecord Capture(
        Guid id, MonthlyIssuedInvoiceRecord issued, Payment receipt,
        Guid recordedByUserId, DateTime recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(receipt);
        if (id == Guid.Empty || recordedByUserId == Guid.Empty ||
            recordedAtUtc.Kind != DateTimeKind.Utc ||
            recordedAtUtc == default)
            throw new ArgumentException("Valid monthly allocation identity and UTC recording required.");

        if (receipt.Id == Guid.Empty ||
            receipt.Purpose != PaymentPurpose.InvoicePayment ||
            receipt.Status != PaymentStatus.Completed ||
            receipt.IsDeleted ||
            receipt.ContractId != issued.ContractId ||
            receipt.InvoiceId != issued.Id ||
            receipt.Amount <= 0m ||
            decimal.Truncate(receipt.Amount) != receipt.Amount ||
            !receipt.PaidAt.HasValue ||
            receipt.PaidAt.Value.Kind != DateTimeKind.Utc ||
            receipt.PaidAt.Value < issued.IssuedAtUtc ||
            string.IsNullOrWhiteSpace(receipt.PaymentNumber) ||
            string.IsNullOrWhiteSpace(receipt.TransactionReference) ||
            recordedAtUtc < receipt.PaidAt.Value)
            throw new InvalidOperationException(
                "Monthly allocation must reference an exact completed original invoice receipt.");

        return new MonthlyInvoicePaymentAllocationRecord(id)
        {
            PaymentId = receipt.Id,
            IssuedInvoiceId = issued.Id,
            ContractId = issued.ContractId,
            Amount = receipt.Amount,
            PaymentNumberSnapshot = receipt.PaymentNumber,
            PaidAtUtc = receipt.PaidAt.Value,
            BankTransactionReferenceSnapshot = receipt.TransactionReference,
            RecordedByUserId = recordedByUserId,
            RecordedAtUtc = recordedAtUtc.AddTicks(
                -(recordedAtUtc.Ticks % 10L))
        };
    }

    public void VerifySource(Payment receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (PaymentId == Guid.Empty || IssuedInvoiceId == Guid.Empty ||
            ContractId == Guid.Empty || RecordedByUserId == Guid.Empty ||
            RecordedAtUtc.Kind != DateTimeKind.Utc ||
            PaidAtUtc.Kind != DateTimeKind.Utc ||
            Amount <= 0m || decimal.Truncate(Amount) != Amount ||
            receipt.Id != PaymentId ||
            receipt.Purpose != PaymentPurpose.InvoicePayment ||
            receipt.Status != PaymentStatus.Completed ||
            receipt.IsDeleted ||
            receipt.ContractId != ContractId ||
            receipt.InvoiceId != IssuedInvoiceId ||
            receipt.Amount != Amount ||
            receipt.PaidAt != PaidAtUtc ||
            receipt.PaymentNumber != PaymentNumberSnapshot ||
            !StringComparer.Ordinal.Equals(
                receipt.TransactionReference, BankTransactionReferenceSnapshot))
            throw new InvalidOperationException(
                "Monthly staged receipt has drifted from immutable source evidence.");
    }
}
