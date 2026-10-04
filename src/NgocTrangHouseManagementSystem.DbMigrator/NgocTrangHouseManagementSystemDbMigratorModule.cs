using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using BuildingManagement.EntityFrameworkCore;

namespace NgocTrangHouseManagementSystem.DbMigrator;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(NgocTrangHouseManagementSystemEntityFrameworkCoreModule),
    typeof(NgocTrangHouseManagementSystemApplicationContractsModule),
    typeof(BuildingManagementEntityFrameworkCoreModule)
)]
public class NgocTrangHouseManagementSystemDbMigratorModule : AbpModule
{
}
