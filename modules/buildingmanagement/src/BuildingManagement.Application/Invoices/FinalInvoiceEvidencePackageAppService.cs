using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;

namespace BuildingManagement.Invoices;

/// <summary>
/// Server-loaded, read-only assembly of the historic final-month bill
/// evidence. There is no Issue/Post/Pay/Refund action or database write.
/// HTTP remains disabled until authenticated per-building access is approved.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalInvoiceEvidencePackageAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<InvoiceItem, Guid> _items;
    private readonly IRepository<InvoiceItemMeterSource, Guid> _sources;
    private readonly IClock _clock;

    public FinalInvoiceEvidencePackageAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<Contract, Guid> contracts,
        IRepository<Invoice, Guid> invoices,
        IRepository<InvoiceItem, Guid> items,
        IRepository<InvoiceItemMeterSource, Guid> sources,
        IClock clock)
    {
        _requests = requests;
        _contracts = contracts;
        _invoices = invoices;
        _items = items;
        _sources = sources;
        _clock = clock;
    }

    public virtual async Task<FinalInvoiceEvidencePackage> GetAsync(
        Guid moveOutRequestId)
    {
        if (moveOutRequestId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);
        var request = await _requests.FindAsync(moveOutRequestId);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        if (request.Status != MoveOutRequestStatus.Approved ||
            request.ApprovedMoveOutDate is not DateOnly date)
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        var contract = await _contracts.FindAsync(request.ContractId);
        var drafts = await _invoices.GetListAsync(x =>
            x.ContractId == request.ContractId &&
            x.RoomId == request.RoomId &&
            x.BillingYear == date.Year &&
            x.BillingMonth == date.Month);

        // Choosing one of multiple conflicting invoices would obscure
        // an accounting defect. Require exactly one.
        if (drafts.Count != 1)
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        var draft = drafts[0];
        var items = await _items.GetListAsync(x => x.InvoiceId == draft.Id);
        var itemIds = items.Select(x => x.Id).ToArray();
        var sources = itemIds.Length == 0
            ? new List<InvoiceItemMeterSource>()
            : await _sources.GetListAsync(x => itemIds.Contains(x.InvoiceItemId));

        try
        {
            return FinalInvoiceEvidencePackage.Capture(
                request, contract, draft, items, sources, _clock.Now.ToUniversalTime());
        }
        catch (InvalidOperationException)
        {
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);
        }
    }
}
