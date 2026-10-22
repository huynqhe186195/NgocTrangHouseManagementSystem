using System;
using Volo.Abp.Modularity;
using Volo.Abp.Timing;

namespace BuildingManagement;

[DependsOn(
    typeof(BuildingManagementApplicationModule),
    typeof(BuildingManagementDomainTestModule)
)]
public class BuildingManagementApplicationTestModule : AbpModule
{
    public override void ConfigureServices(
        ServiceConfigurationContext context)
    {
        Configure<AbpClockOptions>(options =>
        {
            options.Kind = DateTimeKind.Utc;
        });
    }
}
