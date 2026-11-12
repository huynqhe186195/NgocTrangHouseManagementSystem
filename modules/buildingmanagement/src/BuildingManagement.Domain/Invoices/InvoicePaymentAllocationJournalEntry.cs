using System;
using BuildingManagement.Payments;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// STAGED journal schema for an eventual issued-invoice payment allocation.
/// No public writer, production migration, verified issued invoice ledger,
/// or permissioned posting service currently exists. This row is NOT
/// recognized payment, does not reduce debt, and does not offset a deposit.
/// A single completed Payment must be used in at most one journal entry.
/// Split allocations remain unsupported pending approved business policy.
/// </summary>
public sealed class InvoicePaymentAllocationJournalEntry : Entity<Guid>
{
    public Guid PaymentId { get; private set; }
    public Guid IssuedInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public decimal Amount { get; private set; }
    public string PaymentNumberSnapshot { get; private set; } = string.Empty;
    public DateTime PaymentPaidAtUtc { get; private set; }
    public string? PaymentTransactionReferenceSnapshot { get; private set; }
    public Guid RecordedByUserId { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }

    // No posting or permission follows from constructing/storing this row.
    public bool IsRecognizedAllocation => false;
    public bool CanReduceOutstandingDebt => false;
    public bool CanOffsetDeposit => false;

    private InvoicePaymentAllocationJournalEntry() { }

    private InvoicePaymentAllocationJournalEntry(
        Guid id, Payment payment, Guid issuedInvoiceId,
        Guid recordedByUserId, DateTime recordedAtUtc) : base(id)
    {
        PaymentId = payment.Id;
        IssuedInvoiceId = issuedInvoiceId;
        ContractId = payment.ContractId!.Value;
        Amount = payment.Amount;
        PaymentNumberSnapshot = payment.PaymentNumber;
        PaymentPaidAtUtc = payment.PaidAt!.Value;
        PaymentTransactionReferenceSnapshot = payment.TransactionReference;
        RecordedByUserId = recordedByUserId;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>
    /// OFFLINE schema preparation ONLY. A future authorized ledger writer
    /// must additionally resolve an ACTUAL issued invoice and ownership,
    /// then lock Payment and invoice in one DB transaction before insertion.
    /// Neither caller-supplied IDs nor this factory prove those conditions.
    /// </summary>
    public static InvoicePaymentAllocationJournalEntry StageForValidation(
        Guid id, Payment payment, Guid issuedInvoiceId,
        Guid recordedByUserId, DateTime recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (id == Guid.Empty || issuedInvoiceId == Guid.Empty ||
            recordedByUserId == Guid.Empty)
            throw new ArgumentException("Valid journal, invoice and actor IDs are required.");
        if (recordedAtUtc == default ||
            recordedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Journal timestamp must be UTC.",
                nameof(recordedAtUtc));
        if (payment.Id == Guid.Empty ||
            payment.Purpose != PaymentPurpose.InvoicePayment ||
            payment.Status != PaymentStatus.Completed ||
            !payment.PaidAt.HasValue ||
            payment.PaidAt.Value == default ||
            payment.IsDeleted ||
            payment.ContractId is null ||
            payment.ContractId == Guid.Empty ||
            payment.InvoiceId != issuedInvoiceId ||
            string.IsNullOrWhiteSpace(payment.PaymentNumber) ||
            payment.Amount <= 0m ||
            decimal.Truncate(payment.Amount) != payment.Amount)
            throw new InvalidOperationException(
                "Journal staging requires one valid completed whole-VND invoice payment with an exact invoice attribution.");

        // The associated Invoice table is DRAFT-only at present.
        // This factory is test-only staging, never an approval to post.
        return new InvoicePaymentAllocationJournalEntry(
            id, payment, issuedInvoiceId, recordedByUserId, recordedAtUtc);
    }

    /// <summary>
    /// Audit materialized journal metadata against the immutable receipt.
    /// Verifies a candidate row, but NEVER declares recognized settlement.
    /// </summary>
    public void VerifySourceReceipt(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (PaymentId == Guid.Empty || IssuedInvoiceId == Guid.Empty ||
            ContractId == Guid.Empty || RecordedByUserId == Guid.Empty ||
            (RecordedAtUtc == default ||
             RecordedAtUtc.Kind != DateTimeKind.Utc) ||
            Amount <= 0m || decimal.Truncate(Amount) != Amount ||
            string.IsNullOrWhiteSpace(PaymentNumberSnapshot) ||
            payment.Id != PaymentId ||
            payment.Purpose != PaymentPurpose.InvoicePayment ||
            payment.Status != PaymentStatus.Completed ||
            !payment.PaidAt.HasValue ||
            payment.IsDeleted ||
            payment.ContractId != ContractId ||
            payment.InvoiceId != IssuedInvoiceId ||
            payment.Amount != Amount ||
            payment.PaymentNumber != PaymentNumberSnapshot ||
            payment.PaidAt != PaymentPaidAtUtc ||
            !StringComparer.Ordinal.Equals(
                payment.TransactionReference,
                PaymentTransactionReferenceSnapshot))
            throw new InvalidOperationException(
                "Staged invoice allocation is inconsistent with its completed source receipt.");
    }
}
