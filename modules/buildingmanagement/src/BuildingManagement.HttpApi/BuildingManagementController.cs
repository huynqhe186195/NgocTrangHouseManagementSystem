using BuildingManagement.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace BuildingManagement;

public abstract class BuildingManagementController : AbpControllerBase
{
    protected BuildingManagementController()
    {
        LocalizationResource = typeof(BuildingManagementResource);
    }
}
