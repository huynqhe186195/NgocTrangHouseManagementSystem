using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Payments;

namespace BuildingManagement.Invoices;

/// <summary>
/// Audit-only evidence for the final month's invoice payments. There is no
/// issued invoice, allocation ledger or recognized receivable in Phase 2G.3
/// yet. Observed receipt totals are NEVER a settled debt balance.
/// </summary>
public sealed class FinalMoveOutPaymentEvidence
{
    public Guid MoveOutRequestId { get; init; }
    public Guid ContractId { get; init; }
    public Guid? DraftInvoiceId { get; init; }
    public int ObservedReceiptCount { get; internal set; }
    public decimal? ObservedCompletedReceiptAmount { get; internal set; }
    public decimal? ObservedPendingReceiptAmount { get; internal set; }
    public List<string> Blockers { get; } = new();

    // Do not mistake payments associated with an unissued draft for
    // allocations to a legally issued invoice or paid final bill.
    public decimal? OutstandingDebt => null;
    public bool IsDebtReconciled => false;
    public bool CanSettleDeposit => false;
}

public static class FinalMoveOutPaymentEvidenceEvaluator
{
    public static FinalMoveOutPaymentEvidence Assess(
        FinalMoveOutBillReview review,
        IReadOnlyCollection<Payment> contractAndInvoicePayments)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(contractAndInvoicePayments);

        var result = new FinalMoveOutPaymentEvidence
        {
            MoveOutRequestId = review.MoveOutRequestId,
            ContractId = review.ContractId,
            DraftInvoiceId = review.UtilityDraftId
        };

        if (review.UtilityDraftId is null ||
            review.ProvisionalRentAndUtilities is null)
            result.Blockers.Add("NoVerifiedFinalBillDraft");

        // Only payments explicitly tied to THIS draft, or invoice payments
        // for THIS contract that were never assigned to an invoice, are
        // relevant. Never count deposits or another month's invoice.
        var matching = contractAndInvoicePayments.Where(payment =>
            (review.UtilityDraftId.HasValue &&
             payment.InvoiceId == review.UtilityDraftId.Value) ||
            (payment.ContractId == review.ContractId &&
             payment.Purpose == PaymentPurpose.InvoicePayment &&
             !payment.InvoiceId.HasValue)).ToArray();

        result.ObservedReceiptCount = matching.Length;

        if (matching.Any(p =>
            p.Id == Guid.Empty ||
            p.Amount <= 0 ||
            p.IsDeleted ||
            p.Purpose != PaymentPurpose.InvoicePayment ||
            p.ContractId != review.ContractId ||
            p.InvoiceId != review.UtilityDraftId ||
            (p.Status == PaymentStatus.Completed && !p.PaidAt.HasValue) ||
            !Enum.IsDefined(p.Status)))
            result.Blockers.Add("InvoicePaymentAttributionInvalid");

        if (matching.Select(p => p.Id).Distinct().Count() != matching.Length)
            result.Blockers.Add("DuplicatePaymentIdentifier");

        var references = matching
            .Where(p => !string.IsNullOrWhiteSpace(p.TransactionReference))
            .Select(p => p.TransactionReference!.Trim())
            .ToArray();
        if (references.Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Length)
            result.Blockers.Add("DuplicateTransactionReference");

        if (matching.Any(p => !p.InvoiceId.HasValue))
            result.Blockers.Add("UnallocatedInvoicePayment");

        if (matching.Any(p => p.Status == PaymentStatus.Completed) &&
            review.UtilityDraftId.HasValue)
            result.Blockers.Add("CompletedPaymentLinkedToUnissuedDraft");

        if (!result.Blockers.Any(x =>
            x == "InvoicePaymentAttributionInvalid" ||
            x == "DuplicatePaymentIdentifier" ||
            x == "DuplicateTransactionReference" ||
            x == "UnallocatedInvoicePayment") &&
            review.UtilityDraftId.HasValue)
        {
            result.ObservedCompletedReceiptAmount = matching
                .Where(p => p.Status == PaymentStatus.Completed)
                .Sum(p => p.Amount);
            result.ObservedPendingReceiptAmount = matching
                .Where(p => p.Status == PaymentStatus.Pending)
                .Sum(p => p.Amount);
        }

        // Always block financial completion: the domain has Draft only,
        // there is no invoice issue, immutable receivable or audited
        // allocation ledger. Do NOT subtract observed receipts from quote.
        result.Blockers.Add("FinalInvoiceNotIssued");
        result.Blockers.Add("PaymentAllocationNotImplemented");
        result.Blockers.Add("ActualOutstandingDebtUnknown");
        return result;
    }
}
