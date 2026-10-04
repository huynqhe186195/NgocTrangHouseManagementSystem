using Localization.Resources.AbpUi;
using BuildingManagement.Localization;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingManagement;

[DependsOn(
    typeof(BuildingManagementApplicationContractsModule),
    typeof(AbpAspNetCoreMvcModule))]
public class BuildingManagementHttpApiModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<IMvcBuilder>(mvcBuilder =>
        {
            mvcBuilder.AddApplicationPartIfNotExists(typeof(BuildingManagementHttpApiModule).Assembly);
        });
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Get<BuildingManagementResource>()
                .AddBaseTypes(typeof(AbpUiResource));
        });
    }
}
