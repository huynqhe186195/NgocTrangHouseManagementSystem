using Volo.Abp.Modularity;

namespace BuildingManagement;

[DependsOn(
    typeof(BuildingManagementDomainModule),
    typeof(BuildingManagementTestBaseModule)
)]
public class BuildingManagementDomainTestModule : AbpModule
{

}
