using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Payments;

namespace BuildingManagement.Invoices;

/// <summary>
/// Pure payment-to-issued-invoice reconciliation kernel for Phase 2G.3.
/// Invoke ONLY after a real immutable invoice has been issued and verified
/// server-side. Current persisted Invoice entities are DRAFT ONLY, so NO
/// production app service can invoke this to settle debt or a deposit.
/// Adjustments and fee-policy approval are separate audited transactions.
/// </summary>
public static class IssuedInvoicePaymentReconciler
{
    public static IssuedInvoicePaymentSummary Assess(
        Guid issuedInvoiceId,
        Guid contractId,
        decimal issuedInvoiceAmount,
        DateOnly dueDate,
        DateOnly asOfDate,
        IReadOnlyCollection<Payment> attributedPayments)
    {
        if (issuedInvoiceId == Guid.Empty || contractId == Guid.Empty)
            throw new ArgumentException("Issued invoice and contract IDs are required.");
        if (issuedInvoiceAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(issuedInvoiceAmount));
        if (dueDate == default || asOfDate == default)
            throw new ArgumentException("Due date and evaluation date are required.");
        ArgumentNullException.ThrowIfNull(attributedPayments);

        var payments = attributedPayments.ToArray();
        if (payments.Any(x => x is null ||
            x.Id == Guid.Empty ||
            x.Purpose != PaymentPurpose.InvoicePayment ||
            x.InvoiceId != issuedInvoiceId ||
            x.ContractId != contractId ||
            x.Amount <= 0 ||
            x.IsDeleted ||
            !Enum.IsDefined(x.Status) ||
            (x.Status == PaymentStatus.Completed && !x.PaidAt.HasValue) ||
            (x.Status != PaymentStatus.Completed && x.PaidAt.HasValue)))
            throw new InvalidOperationException(
                "Invoice allocation contains invalid, deleted, deposit, or misattributed receipts.");

        if (payments.Select(x => x.Id).Distinct().Count() != payments.Length)
            throw new InvalidOperationException(
                "The same PaymentId cannot be applied twice.");

        var references = payments
            .Where(x => !string.IsNullOrWhiteSpace(x.TransactionReference))
            .Select(x => x.TransactionReference!.Trim()).ToArray();
        if (references.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != references.Length)
            throw new InvalidOperationException(
                "Duplicate transaction references require manual reconciliation.");

        // Pending / Cancelled / Failed do not reduce what is owed.
        // A single completed payment is counted ONCE, by Payment.Id.
        var completed = payments
            .Where(x => x.Status == PaymentStatus.Completed)
            .ToArray();
        var received = completed.Sum(x => x.Amount);
        var stillOwed = Math.Max(0m, issuedInvoiceAmount - received);
        var excess = Math.Max(0m, received - issuedInvoiceAmount);

        var status = stillOwed == 0m
            ? IssuedInvoicePaymentStatus.Paid
            : asOfDate > dueDate
                ? IssuedInvoicePaymentStatus.Overdue
                : received > 0m
                    ? IssuedInvoicePaymentStatus.PartiallyPaid
                    : IssuedInvoicePaymentStatus.Unpaid;

        return new IssuedInvoicePaymentSummary(
            issuedInvoiceId, contractId, issuedInvoiceAmount,
            received, stillOwed, excess, completed.Length, status);
    }
}

public enum IssuedInvoicePaymentStatus
{
    Unpaid = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Overdue = 4
}

/// <summary>
/// Outstanding BEFORE approved invoice adjustments. This result never
/// authorizes issuing, payment allocation, deposit settlement or write-off.
/// An excess collection requires a separate explicit resolution.
/// </summary>
public sealed record IssuedInvoicePaymentSummary(
    Guid InvoiceId,
    Guid ContractId,
    decimal IssuedAmount,
    decimal CompletedPaymentsAmount,
    decimal OutstandingBeforeAdjustments,
    decimal UnresolvedOverpayment,
    int CompletedPaymentCount,
    IssuedInvoicePaymentStatus Status)
{
    public bool CanSettleDeposit => false;
    public bool HasUnresolvedOverpayment => UnresolvedOverpayment > 0m;
}
