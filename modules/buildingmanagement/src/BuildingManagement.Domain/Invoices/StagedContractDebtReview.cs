using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Payments;

namespace BuildingManagement.Invoices;

/// <summary>
/// Read-only facts obtained from verified, immutable issued-invoice rows.
/// Neither these facts nor the resulting balance grant posting authority.
/// </summary>
public sealed record StagedContractInvoiceDebtFacts(
    Guid InvoiceId, Guid ContractId, decimal TotalAmount,
    DateTime IssuedAtUtc, DateOnly DueDate);

public sealed class StagedContractDebtReview
{
    public Guid ContractId { get; init; }
    public DateOnly AsOfDate { get; init; }
    public List<IssuedInvoicePaymentSummary> Invoices { get; } = new();
    public decimal? TotalInvoiced { get; internal set; }
    public decimal? TotalReceivedAsOf { get; internal set; }
    public decimal? TotalOutstandingAsOf { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool HasCompleteStagedEvidence =>
        Blockers.Count == 0 && TotalOutstandingAsOf.HasValue;
    public bool IsOfficialReceivable => false;
    public bool CanPost => false;
    public bool CanOffsetDeposit => false;
    public bool CanCompleteMoveOut => false;
}

/// <summary>
/// Contract-wide staging reconciliation. Audit the WHOLE receipt/journal
/// graph first, then compute every invoice's Vietnam-calendar as-of balance.
/// The whole result fails closed if any invoice or receipt is inconsistent.
/// </summary>
public static class StagedContractDebtEvaluator
{
    public static StagedContractDebtReview Assess(
        Guid contractId, DateOnly asOfDate,
        IReadOnlyCollection<StagedContractInvoiceDebtFacts> issued,
        IReadOnlyCollection<InvoicePaymentAllocationJournalEntry> journal,
        IReadOnlyCollection<Payment> invoiceReceipts)
    {
        if (contractId == Guid.Empty || asOfDate == default)
            throw new ArgumentException("Contract and evaluation date are required.");
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(invoiceReceipts);

        var result = new StagedContractDebtReview
        {
            ContractId = contractId, AsOfDate = asOfDate
        };
        if (issued.Count == 0)
            result.Blockers.Add("NoIssuedInvoiceEvidence");
        if (issued.Any(x => x is null || x.InvoiceId == Guid.Empty ||
                x.ContractId != contractId || x.TotalAmount <= 0m ||
                decimal.Truncate(x.TotalAmount) != x.TotalAmount ||
                x.IssuedAtUtc == default ||
                x.IssuedAtUtc.Kind != DateTimeKind.Utc ||
                x.DueDate == default) ||
            issued.Where(x => x is not null)
                .Select(x => x.InvoiceId).Distinct().Count() != issued.Count)
            result.Blockers.Add("InvalidOrDuplicateIssuedInvoices");
        if (result.Blockers.Count > 0)
            return result;

        var validInvoiceIds = issued.Select(x => x.InvoiceId).ToHashSet();
        if (journal.Any(x => x is null ||
            !validInvoiceIds.Contains(x.IssuedInvoiceId)))
            result.Blockers.Add("AllocationReferencesMissingContractInvoice");

        // An as-of projection must exclude invoices that were not yet
        // issued in the Vietnam business calendar. Summing today's whole
        // ledger would invent debt when viewing a past date.
        var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
            "Asia/Ho_Chi_Minh");
        var issuedAsOf = issued.Where(x =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                x.IssuedAtUtc, vietnamTimeZone)) <= asOfDate).ToArray();

        // A contract-level audit is not complete if the caller supplies only
        // already-journaled receipts. The application reader loads ALL
        // invoice payments including soft-deleted ones.
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            journal, invoiceReceipts, contractId);
        result.Blockers.AddRange(audit.Blockers.Where(x =>
            x != "IssuedInvoiceLedgerAndAllocationAuthorizationMissing"));
        if (result.Blockers.Count > 0)
            return result;

        if (issuedAsOf.Length == 0)
        {
            result.Blockers.Add("NoInvoiceIssuedAsOfDate");
            return result;
        }

        var receipts = invoiceReceipts.ToDictionary(x => x.Id);
        try
        {
            foreach (var invoice in issuedAsOf.OrderBy(x => x.DueDate)
                         .ThenBy(x => x.InvoiceId))
            {
                // Payment date alone is insufficient for historical
                // allocation: a receipt journaled AFTER the as-of date
                // cannot be treated as already reconciled on that date.
                var applied = journal
                    .Where(x => x.IssuedInvoiceId == invoice.InvoiceId &&
                        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                            x.RecordedAtUtc, vietnamTimeZone)) <= asOfDate)
                    .Select(x => receipts[x.PaymentId]).ToArray();
                if (applied.Any(x => x.PaidAt!.Value < invoice.IssuedAtUtc))
                {
                    result.Blockers.Add("ReceiptPredatesInvoiceIssuance");
                    break;
                }

                var summary = IssuedInvoicePaymentReconciler.Assess(
                    invoice.InvoiceId, contractId, invoice.TotalAmount,
                    invoice.DueDate, asOfDate, applied);
                result.Invoices.Add(summary);
                if (summary.HasUnresolvedOverpayment)
                    result.Blockers.Add("UnresolvedInvoiceOverpayment");
            }

            if (result.Blockers.Count > 0)
            {
                result.Invoices.Clear();
                return result;
            }

            // Totals are a read-only review, never a recognized debt balance.
            result.TotalInvoiced = checked(issuedAsOf.Sum(x => x.TotalAmount));
            result.TotalReceivedAsOf = checked(result.Invoices.Sum(
                x => x.CompletedPaymentsAmount));
            result.TotalOutstandingAsOf = checked(result.Invoices.Sum(
                x => x.OutstandingBeforeAdjustments));
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            ArgumentException or OverflowException or KeyNotFoundException)
        {
            result.Blockers.Add("StagedContractDebtReconciliationInvalid");
            result.Invoices.Clear();
            result.TotalInvoiced = null;
            result.TotalReceivedAsOf = null;
            result.TotalOutstandingAsOf = null;
        }
        return result;
    }
}
