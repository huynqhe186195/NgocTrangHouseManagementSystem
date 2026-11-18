using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Buildings;

public interface IBuildingPublicInfoAppService : IApplicationService
{
    /// <summary>
    /// The parameter is named <c>id</c> on purpose: ABP's conventional
    /// controller only puts it in the URL path under that name, so
    /// <c>GET …/building-public-info/{id}</c> resolves. It is the building id.
    /// </summary>
    Task<BuildingPublicInfoDto?> GetAsync(Guid id);

    Task<BuildingPublicInfoDto> UpdateAsync(
        Guid id, UpdateBuildingPublicInfoDto input);
}
