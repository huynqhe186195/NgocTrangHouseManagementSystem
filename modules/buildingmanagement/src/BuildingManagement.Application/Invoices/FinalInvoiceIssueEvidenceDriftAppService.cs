using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// INTERNAL audit only. Rechecks archived preparation evidence against the
/// current invoice draft and its persisted utility-source graph. No write,
/// no billing authorization, no public API, no debt or deposit settlement.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalInvoiceIssueEvidenceDriftAppService : ApplicationService
{
    private readonly IRepository<FinalInvoiceIssueEvidenceRecord, Guid> _archives;
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<InvoiceItem, Guid> _items;
    private readonly IRepository<InvoiceItemMeterSource, Guid> _sources;

    public FinalInvoiceIssueEvidenceDriftAppService(
        IRepository<FinalInvoiceIssueEvidenceRecord, Guid> archives,
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<Contract, Guid> contracts,
        IRepository<Invoice, Guid> invoices,
        IRepository<InvoiceItem, Guid> items,
        IRepository<InvoiceItemMeterSource, Guid> sources)
    {
        _archives = archives;
        _requests = requests;
        _contracts = contracts;
        _invoices = invoices;
        _items = items;
        _sources = sources;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<FinalInvoiceIssueEvidenceDriftReview> GetAsync(
        Guid stagedEvidenceId)
    {
        if (stagedEvidenceId == Guid.Empty)
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        var archived = await _archives.FindAsync(stagedEvidenceId);
        if (archived is null)
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        var request = await _requests.FindAsync(archived.MoveOutRequestId);
        var contract = await _contracts.FindAsync(archived.ContractId);
        var drafts = await _invoices.GetListAsync(x =>
            x.ContractId == archived.ContractId &&
            x.RoomId == archived.RoomId &&
            x.BillingYear == archived.BillingYear &&
            x.BillingMonth == archived.BillingMonth);

        var items = new List<InvoiceItem>();
        var sources = new List<InvoiceItemMeterSource>();
        if (drafts.Count == 1)
        {
            items = await _items.GetListAsync(x =>
                x.InvoiceId == drafts[0].Id);
            var itemIds = items.Select(x => x.Id).ToArray();
            if (itemIds.Length != 0)
                sources = await _sources.GetListAsync(x =>
                    itemIds.Contains(x.InvoiceItemId));
        }

        return FinalInvoiceIssueEvidenceDriftEvaluator.Assess(
            archived, request, contract, drafts, items, sources);
    }
}
