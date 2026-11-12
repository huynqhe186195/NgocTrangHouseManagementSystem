using System;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Invoices;

/// <summary>
/// Actual repository-backed, read-only full-month rent evidence for final
/// invoice preparation. Other service fees and utility draft reconciliation
/// are separate work; this service does not issue a bill or change debt.
/// HTTP-disabled until authenticated building-scope authorization exists.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalMonthRentPreviewAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<Contract, Guid> _contracts;

    public FinalMonthRentPreviewAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<Contract, Guid> contracts)
    {
        _requests = requests;
        _contracts = contracts;
    }

    public virtual async Task<FinalMonthRentPreview> GetAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);
        var request = await _requests.FindAsync(requestId);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var contract = await _contracts.FindAsync(request.ContractId);
        return FinalMonthRentPreview.Assess(request, contract);
    }
}
