using Localization.Resources.AbpUi;
using Microsoft.AspNetCore.Mvc;
using NgocTrangHouseManagementSystem.Filters;
using NgocTrangHouseManagementSystem.Localization;
using Volo.Abp.Account;
using Volo.Abp.SettingManagement;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.Localization;

namespace NgocTrangHouseManagementSystem;

 [DependsOn(
    typeof(NgocTrangHouseManagementSystemApplicationContractsModule),
    typeof(AbpPermissionManagementHttpApiModule),
    typeof(AbpSettingManagementHttpApiModule),
    typeof(AbpAccountHttpApiModule),
    typeof(AbpIdentityHttpApiModule),
    typeof(AbpFeatureManagementHttpApiModule)
    )]
public class NgocTrangHouseManagementSystemHttpApiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        ConfigureLocalization();
        ConfigureResponseFilter();
    }

    private void ConfigureLocalization()
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Get<NgocTrangHouseManagementSystemResource>()
                .AddBaseTypes(
                    typeof(AbpUiResource)
                );
        });
    }

    private void ConfigureResponseFilter() // Apply this filter to the entire MVC/API.
    {
        Configure<MvcOptions>(options =>
        {
            options.Filters.Add<ApiResponseFilter>();
        });
    }
}
