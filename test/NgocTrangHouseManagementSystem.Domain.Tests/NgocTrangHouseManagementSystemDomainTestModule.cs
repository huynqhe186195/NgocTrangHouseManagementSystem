using Volo.Abp.Modularity;

namespace NgocTrangHouseManagementSystem;

[DependsOn(
    typeof(NgocTrangHouseManagementSystemDomainModule),
    typeof(NgocTrangHouseManagementSystemTestBaseModule)
)]
public class NgocTrangHouseManagementSystemDomainTestModule : AbpModule
{

}
