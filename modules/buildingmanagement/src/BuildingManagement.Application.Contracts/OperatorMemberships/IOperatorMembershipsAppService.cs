using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace BuildingManagement.OperatorMemberships;

/// <summary>
/// Read-only operator-to-building membership roster. It intentionally does not
/// expose assign/revoke commands or the ABP Identity user-management surface.
/// </summary>
public interface IOperatorMembershipsAppService : IApplicationService
{
    Task<PagedResultDto<OperatorMembershipSummaryDto>> GetListAsync(
        Guid? buildingId = null,
        int skipCount = 0,
        int maxResultCount = 50);
}
