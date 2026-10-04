using BuildingManagement.Localization;
using Volo.Abp.Application.Services;

namespace BuildingManagement;

public abstract class BuildingManagementAppService : ApplicationService
{
    protected BuildingManagementAppService()
    {
        LocalizationResource = typeof(BuildingManagementResource);
        ObjectMapperContext = typeof(BuildingManagementApplicationModule);
    }
}
