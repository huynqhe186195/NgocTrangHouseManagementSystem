using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Payments;

namespace BuildingManagement.Invoices;

/// <summary>
/// Read-side review of a staging issued-invoice ledger and its COMPLETE
/// contract-wide payment/journal evidence. A value is an estimated balance,
/// NOT an official receivable, posted allocation, or deposit authorization.
/// All inputs must come from persisted, scoped server-side queries.
/// </summary>
public sealed class StagedIssuedInvoiceDebtReview
{
    public Guid IssuedInvoiceId { get; init; }
    public Guid ContractId { get; init; }
    public DateOnly AsOfDate { get; init; }
    public IssuedInvoicePaymentSummary? StagedPaymentSummary { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool HasCompleteStagedEvidence => StagedPaymentSummary is not null &&
        Blockers.Count == 0;
    public bool IsOfficialReceivable => false;
    public bool IsPostedAllocation => false;
    public bool CanReduceDebt => false;
    public bool CanSettleDeposit => false;
}

public static class StagedIssuedInvoiceDebtEvaluator
{
    public static StagedIssuedInvoiceDebtReview Assess(
        Guid issuedInvoiceId,
        Guid contractId,
        decimal issuedAmount,
        DateOnly dueDate,
        DateOnly asOfDate,
        IReadOnlyCollection<InvoicePaymentAllocationJournalEntry> allContractJournal,
        IReadOnlyCollection<Payment> allContractInvoiceReceipts)
    {
        if (issuedInvoiceId == Guid.Empty || contractId == Guid.Empty ||
            issuedAmount <= 0m || decimal.Truncate(issuedAmount) != issuedAmount ||
            dueDate == default || asOfDate == default)
            throw new ArgumentException("Valid issued invoice identity, amount and dates are required.");
        ArgumentNullException.ThrowIfNull(allContractJournal);
        ArgumentNullException.ThrowIfNull(allContractInvoiceReceipts);

        var result = new StagedIssuedInvoiceDebtReview
        {
            IssuedInvoiceId = issuedInvoiceId,
            ContractId = contractId,
            AsOfDate = asOfDate
        };

        // Review the WHOLE contract, not only the journals selected for this
        // invoice. Otherwise an unjournaled receipt could be hidden.
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            allContractJournal, allContractInvoiceReceipts, contractId);
        result.Blockers.AddRange(audit.Blockers.Where(x =>
            x != "IssuedInvoiceLedgerAndAllocationAuthorizationMissing"));
        if (result.Blockers.Count != 0)
            return result;

        // Every selected payment is taken ONLY through a verified journal.
        // Never count a completed receipt merely because it carries InvoiceId.
        var receiptById = allContractInvoiceReceipts.ToDictionary(x => x.Id);
        var invoiceReceipts = allContractJournal
            .Where(x => x.IssuedInvoiceId == issuedInvoiceId)
            .Select(x => receiptById[x.PaymentId]).ToArray();
        try
        {
            result.StagedPaymentSummary = IssuedInvoicePaymentReconciler.Assess(
                issuedInvoiceId, contractId, issuedAmount, dueDate,
                asOfDate, invoiceReceipts);
            if (result.StagedPaymentSummary.HasUnresolvedOverpayment)
                result.Blockers.Add("UnresolvedInvoiceOverpayment");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or OverflowException)
        {
            result.Blockers.Add("StagedReceiptReconciliationInvalid");
        }

        // Even a clean mathematical review does not grant posting authority.
        return result;
    }
}
