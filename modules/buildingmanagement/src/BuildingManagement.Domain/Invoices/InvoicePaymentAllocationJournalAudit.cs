using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Payments;

namespace BuildingManagement.Invoices;

/// <summary>
/// Pure batch audit of STAGED invoice allocation journal entries. No
/// recognized receivable, debt reduction or deposit credit exists. Only
/// a future approved issuer/allocator can decide whether entries post.
/// </summary>
public sealed class InvoicePaymentAllocationJournalAudit
{
    public int VerifiedStagedEntryCount { get; internal set; }
    public decimal? VerifiedStagedAmount { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool CanPost => false;
    public bool CanReduceDebt => false;
    public bool CanSettleDeposit => false;
}

public static class InvoicePaymentAllocationJournalAuditor
{
    public static InvoicePaymentAllocationJournalAudit Assess(
        IReadOnlyCollection<InvoicePaymentAllocationJournalEntry> entries,
        IReadOnlyCollection<Payment> receipts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(receipts);

        var review = new InvoicePaymentAllocationJournalAudit();
        if (entries.Any(x => x is null || x.Id == Guid.Empty) ||
            receipts.Any(x => x is null || x.Id == Guid.Empty) ||
            entries.Select(x => x.Id).Distinct().Count() != entries.Count ||
            receipts.Select(x => x.Id).Distinct().Count() != receipts.Count)
        {
            review.Blockers.Add("DuplicateOrInvalidEvidenceIdentifiers");
            return Stop(review);
        }

        if (entries.Select(x => x.PaymentId).Distinct().Count() != entries.Count)
            review.Blockers.Add("PaymentAlreadyUsedInAnotherAllocation");

        var paymentMap = receipts.ToDictionary(x => x.Id);
        foreach (var entry in entries)
        {
            if (!paymentMap.TryGetValue(entry.PaymentId, out var payment))
            {
                review.Blockers.Add("AllocationSourcePaymentMissing");
                continue;
            }

            try
            {
                entry.VerifySourceReceipt(payment);
            }
            catch (InvalidOperationException)
            {
                review.Blockers.Add("AllocationSourceReceiptMismatch");
            }
        }

        var references = entries.Select(x => x.PaymentTransactionReferenceSnapshot)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()).ToArray();
        if (references.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            references.Length)
            review.Blockers.Add("DuplicateAllocationTransactionReference");

        if (review.Blockers.Count == 0)
        {
            review.VerifiedStagedEntryCount = entries.Count;
            review.VerifiedStagedAmount = entries.Sum(x => x.Amount);
        }

        return Stop(review);
    }

    private static InvoicePaymentAllocationJournalAudit Stop(
        InvoicePaymentAllocationJournalAudit review)
    {
        // Even a mathematically consistent audit is not proof of a legally
        // posted invoice or approved allocation. Never infer debt reduction.
        review.Blockers.Add("IssuedInvoiceLedgerAndAllocationAuthorizationMissing");
        return review;
    }
}
