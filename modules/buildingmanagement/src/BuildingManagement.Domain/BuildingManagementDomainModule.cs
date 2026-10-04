using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace BuildingManagement;

[DependsOn(
    typeof(AbpDddDomainModule),
    typeof(BuildingManagementDomainSharedModule)
)]
public class BuildingManagementDomainModule : AbpModule
{

}
