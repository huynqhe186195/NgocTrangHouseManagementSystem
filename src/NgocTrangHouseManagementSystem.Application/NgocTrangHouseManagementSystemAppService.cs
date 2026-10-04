using NgocTrangHouseManagementSystem.Localization;
using Volo.Abp.Application.Services;

namespace NgocTrangHouseManagementSystem;

/* Inherit your application services from this class.
 */
public abstract class NgocTrangHouseManagementSystemAppService : ApplicationService
{
    protected NgocTrangHouseManagementSystemAppService()
    {
        LocalizationResource = typeof(NgocTrangHouseManagementSystemResource);
    }
}
