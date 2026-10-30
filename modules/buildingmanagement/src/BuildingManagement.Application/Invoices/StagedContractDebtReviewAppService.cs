using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Payments;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only contract-wide staging balance. Loads all invoices and
/// original receipt/journal sources under REPEATABLE READ; no public API,
/// posting, debt adjustment, deposit offset or cross-building grant.
/// </summary>
[RemoteService(IsEnabled = false)]
public class StagedContractDebtReviewAppService : ApplicationService
{
    private readonly IRepository<IssuedInvoiceLedgerRecord, Guid> _ledger;
    private readonly IRepository<FinalInvoiceIssueEvidenceRecord, Guid> _evidence;
    private readonly IRepository<InvoiceIssuanceReviewTicket, Guid> _reviews;
    private readonly IRepository<InvoicePaymentAllocationJournalEntry, Guid> _journal;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IDataFilter<ISoftDelete> _softDelete;
    private readonly IInvoiceDraftReadIsolationGuard _isolation;

    public StagedContractDebtReviewAppService(
        IRepository<IssuedInvoiceLedgerRecord, Guid> ledger,
        IRepository<FinalInvoiceIssueEvidenceRecord, Guid> evidence,
        IRepository<InvoiceIssuanceReviewTicket, Guid> reviews,
        IRepository<InvoicePaymentAllocationJournalEntry, Guid> journal,
        IRepository<Payment, Guid> payments,
        IDataFilter<ISoftDelete> softDelete,
        IInvoiceDraftReadIsolationGuard isolation)
    {
        _ledger = ledger;
        _evidence = evidence;
        _reviews = reviews;
        _journal = journal;
        _payments = payments;
        _softDelete = softDelete;
        _isolation = isolation;
    }

    [UnitOfWork(true, IsolationLevel.RepeatableRead)]
    public virtual async Task<StagedContractDebtReview> GetAsync(
        Guid contractId, DateOnly asOfDate)
    {
        if (contractId == Guid.Empty || asOfDate == default)
            throw new ArgumentException("Contract and evaluation date are required.");

        await _isolation.RequireRepeatableReadAsync();
        var invoices = await _ledger.GetListAsync(x => x.ContractId == contractId);
        foreach (var invoice in invoices)
        {
            var evidence = await _evidence.GetAsync(invoice.StagedEvidenceId);
            var review = await _reviews.GetAsync(invoice.ReviewTicketId);
            invoice.VerifyStoredTerms(evidence, review);
        }

        List<Payment> contractPayments;
        using (_softDelete.Disable())
        {
            contractPayments = await _payments.GetListAsync(x =>
                x.ContractId == contractId &&
                x.Purpose == PaymentPurpose.InvoicePayment);
        }
        var receiptIds = contractPayments.Select(x => x.Id).ToArray();
        var journal = await _journal.GetListAsync(x =>
            x.ContractId == contractId || receiptIds.Contains(x.PaymentId));
        var journalReceiptIds = journal.Select(x => x.PaymentId)
            .Distinct().ToArray();

        List<Payment> allReceipts;
        using (_softDelete.Disable())
        {
            allReceipts = await _payments.GetListAsync(x =>
                (x.ContractId == contractId &&
                 x.Purpose == PaymentPurpose.InvoicePayment) ||
                journalReceiptIds.Contains(x.Id));
        }

        return StagedContractDebtEvaluator.Assess(
            contractId, asOfDate,
            invoices.Select(x => new StagedContractInvoiceDebtFacts(
                x.Id, x.ContractId, x.TotalAmount, x.IssuedAtUtc,
                x.DueDate)).ToArray(),
            journal, allReceipts);
    }
}
