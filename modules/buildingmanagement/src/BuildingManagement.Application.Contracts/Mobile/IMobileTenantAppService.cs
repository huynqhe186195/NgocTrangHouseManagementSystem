using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Mobile;

public interface IMobileTenantAppService : IApplicationService
{
    Task<TenantMobileOverviewDto> GetMeAsync();
}
