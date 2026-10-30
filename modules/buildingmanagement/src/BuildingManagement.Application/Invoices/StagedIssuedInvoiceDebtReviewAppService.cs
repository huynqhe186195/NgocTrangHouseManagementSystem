using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Payments;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;
using System.Data;
using System.Collections.Generic;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only, read-only staging projection. NOT a customer billing API.
/// All evidence is loaded under one REPEATABLE READ transaction, including
/// soft-deleted receipts. No receipt is recognized without a journal row.
/// Resource-level authorization must be added before ANY remote exposure.
/// </summary>
[RemoteService(IsEnabled = false)]
public class StagedIssuedInvoiceDebtReviewAppService : ApplicationService
{
    private readonly IRepository<IssuedInvoiceLedgerRecord, Guid> _ledger;
    private readonly IRepository<FinalInvoiceIssueEvidenceRecord, Guid> _evidence;
    private readonly IRepository<InvoiceIssuanceReviewTicket, Guid> _reviews;
    private readonly IRepository<InvoicePaymentAllocationJournalEntry, Guid> _journal;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IDataFilter<ISoftDelete> _softDelete;
    private readonly IInvoiceDraftReadIsolationGuard _isolationGuard;

    public StagedIssuedInvoiceDebtReviewAppService(
        IRepository<IssuedInvoiceLedgerRecord, Guid> ledger,
        IRepository<FinalInvoiceIssueEvidenceRecord, Guid> evidence,
        IRepository<InvoiceIssuanceReviewTicket, Guid> reviews,
        IRepository<InvoicePaymentAllocationJournalEntry, Guid> journal,
        IRepository<Payment, Guid> payments,
        IDataFilter<ISoftDelete> softDelete,
        IInvoiceDraftReadIsolationGuard isolationGuard)
    {
        _ledger = ledger;
        _evidence = evidence;
        _reviews = reviews;
        _journal = journal;
        _payments = payments;
        _softDelete = softDelete;
        _isolationGuard = isolationGuard;
    }

    [UnitOfWork(true, IsolationLevel.RepeatableRead)]
    public virtual async Task<StagedIssuedInvoiceDebtReview> GetAsync(
        Guid issuedInvoiceId, DateOnly asOfDate)
    {
        if (issuedInvoiceId == Guid.Empty || asOfDate == default)
            throw new ArgumentException("Issued invoice and as-of date required.");
        await _isolationGuard.RequireRepeatableReadAsync();

        var invoice = await _ledger.GetAsync(issuedInvoiceId);
        var evidence = await _evidence.GetAsync(invoice.StagedEvidenceId);
        var review = await _reviews.GetAsync(invoice.ReviewTicketId);
        invoice.VerifyStoredTerms(evidence, review);

        // Load all contract invoice receipts, including historical soft
        // deletions. Missing journals are a blocking anomaly.
        List<Payment> contractReceipts;
        using (_softDelete.Disable())
        {
            contractReceipts = await _payments.GetListAsync(x =>
                x.ContractId == invoice.ContractId &&
                x.Purpose == PaymentPurpose.InvoicePayment);
        }
        var ids = contractReceipts.Select(x => x.Id).ToArray();
        var journal = await _journal.GetListAsync(x =>
            x.ContractId == invoice.ContractId || ids.Contains(x.PaymentId));
        var journalReceiptIds = journal.Select(x => x.PaymentId)
            .Distinct().ToArray();

        List<Payment> allReceipts;
        using (_softDelete.Disable())
        {
            allReceipts = await _payments.GetListAsync(x =>
                (x.ContractId == invoice.ContractId &&
                 x.Purpose == PaymentPurpose.InvoicePayment) ||
                journalReceiptIds.Contains(x.Id));
        }

        return StagedIssuedInvoiceDebtEvaluator.Assess(
            invoice.Id, invoice.ContractId, invoice.TotalAmount, invoice.DueDate,
            asOfDate, journal, allReceipts);
    }
}
