using Microsoft.Extensions.Localization;
using NgocTrangHouseManagementSystem.Localization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Ui.Branding;

namespace NgocTrangHouseManagementSystem;

[Dependency(ReplaceServices = true)]
public class NgocTrangHouseManagementSystemBrandingProvider : DefaultBrandingProvider
{
    private IStringLocalizer<NgocTrangHouseManagementSystemResource> _localizer;

    public NgocTrangHouseManagementSystemBrandingProvider(IStringLocalizer<NgocTrangHouseManagementSystemResource> localizer)
    {
        _localizer = localizer;
    }

    public override string AppName => _localizer["AppName"];
}
