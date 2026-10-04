using Volo.Abp.Modularity;

namespace BuildingManagement;

[DependsOn(
    typeof(BuildingManagementApplicationModule),
    typeof(BuildingManagementDomainTestModule)
    )]
public class BuildingManagementApplicationTestModule : AbpModule
{

}
