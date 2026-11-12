using System;
using System.Collections.Generic;
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
/// Internal-only audit of ALL staged allocations associated with a contract
/// and its invoice-payment receipts, including soft-deleted receipts.
/// Querying only the journal would hide completed payments with no entry.
/// Never issues invoices, recognizes allocations, posts debt or refunds.
/// </summary>
[RemoteService(IsEnabled = false)]
public class InvoicePaymentAllocationLedgerReviewAppService : ApplicationService
{
    private readonly IRepository<InvoicePaymentAllocationJournalEntry, Guid> _entries;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public InvoicePaymentAllocationLedgerReviewAppService(
        IRepository<InvoicePaymentAllocationJournalEntry, Guid> entries,
        IRepository<Payment, Guid> payments,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _entries = entries;
        _payments = payments;
        _softDeleteFilter = softDeleteFilter;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<InvoicePaymentAllocationJournalAudit> GetAsync(
        Guid contractId)
    {
        if (contractId == Guid.Empty)
            throw new ArgumentException(
                "Contract ID required for staged allocation audit.",
                nameof(contractId));

        // Load ALL invoice receipts for the contract first. This captures
        // missing journals and allows fetching a row misattributed to a
        // different contract but claiming the same source PaymentId.
        List<Payment> contractReceipts;
        using (_softDeleteFilter.Disable())
        {
            contractReceipts = await _payments.GetListAsync(x =>
                x.ContractId == contractId &&
                x.Purpose == PaymentPurpose.InvoicePayment);
        }
        var receiptIds = contractReceipts.Select(x => x.Id).ToArray();

        var journal = await _entries.GetListAsync(x =>
            x.ContractId == contractId ||
            receiptIds.Contains(x.PaymentId));

        // Also retrieve receipts referenced by this contract's journal,
        // including cross-contract and soft-deleted historical payments.
        var journalPaymentIds = journal.Select(x => x.PaymentId)
            .Distinct().ToArray();
        List<Payment> receipts;
        using (_softDeleteFilter.Disable())
        {
            receipts = await _payments.GetListAsync(x =>
                (x.ContractId == contractId &&
                 x.Purpose == PaymentPurpose.InvoicePayment) ||
                journalPaymentIds.Contains(x.Id));
        }

        return InvoicePaymentAllocationJournalAuditor.Assess(
            journal, receipts, contractId);
    }
}
