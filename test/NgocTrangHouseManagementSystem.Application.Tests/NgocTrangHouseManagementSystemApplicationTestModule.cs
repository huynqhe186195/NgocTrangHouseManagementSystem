using Volo.Abp.Modularity;

namespace NgocTrangHouseManagementSystem;

[DependsOn(
    typeof(NgocTrangHouseManagementSystemApplicationModule),
    typeof(NgocTrangHouseManagementSystemDomainTestModule)
)]
public class NgocTrangHouseManagementSystemApplicationTestModule : AbpModule
{

}
