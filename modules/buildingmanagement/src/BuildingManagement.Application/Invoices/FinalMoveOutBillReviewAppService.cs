using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Invoices;

/// <summary>
/// Read-only assembly from real persisted 2G.1 request/contract and 2G.3
/// utility draft header, lines and source evidence. No write methods.
/// Remote-disabled until owner/staff/tenant building-scope authorization.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalMoveOutBillReviewAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<InvoiceItem, Guid> _items;
    private readonly IRepository<InvoiceItemMeterSource, Guid> _sources;

    public FinalMoveOutBillReviewAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<Contract, Guid> contracts,
        IRepository<Invoice, Guid> invoices,
        IRepository<InvoiceItem, Guid> items,
        IRepository<InvoiceItemMeterSource, Guid> sources)
    {
        _requests = requests;
        _contracts = contracts;
        _invoices = invoices;
        _items = items;
        _sources = sources;
    }

    public virtual async Task<FinalMoveOutBillReview> GetAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var request = await _requests.FindAsync(requestId);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var contract = await _contracts.FindAsync(request.ContractId);
        var invoices = new List<Invoice>();
        var items = new List<InvoiceItem>();
        var sources = new List<InvoiceItemMeterSource>();

        // Only evidence from the approved departure MONTH is relevant.
        if (request.Status == MoveOutRequestStatus.Approved &&
            request.ApprovedMoveOutDate is DateOnly date)
        {
            invoices = await _invoices.GetListAsync(x =>
                x.ContractId == request.ContractId &&
                x.RoomId == request.RoomId &&
                x.BillingYear == date.Year &&
                x.BillingMonth == date.Month);
            if (invoices.Count == 1)
            {
                var invoiceId = invoices[0].Id;
                items = await _items.GetListAsync(x => x.InvoiceId == invoiceId);
                var ids = items.Select(x => x.Id).ToArray();
                if (ids.Length > 0)
                    sources = await _sources.GetListAsync(
                        x => ids.Contains(x.InvoiceItemId));
            }
        }

        return FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, invoices, items, sources);
    }
}
