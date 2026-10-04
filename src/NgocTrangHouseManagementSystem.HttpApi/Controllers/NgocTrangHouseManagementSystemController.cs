using NgocTrangHouseManagementSystem.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace NgocTrangHouseManagementSystem.Controllers;

/* Inherit your controllers from this class.
 */
public abstract class NgocTrangHouseManagementSystemController : AbpControllerBase
{
    protected NgocTrangHouseManagementSystemController()
    {
        LocalizationResource = typeof(NgocTrangHouseManagementSystemResource);
    }
}
