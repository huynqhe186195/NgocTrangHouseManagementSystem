using Volo.Abp.Application;
using Volo.Abp.Modularity;
using Volo.Abp.Authorization;

namespace BuildingManagement;

[DependsOn(
    typeof(BuildingManagementDomainSharedModule),
    typeof(AbpDddApplicationContractsModule),
    typeof(AbpAuthorizationModule)
    )]
public class BuildingManagementApplicationContractsModule : AbpModule
{

}
