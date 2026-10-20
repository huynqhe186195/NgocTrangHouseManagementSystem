using BuildingManagement.EntityFrameworkCore.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.Modularity;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore.Contracts;

namespace BuildingManagement.EntityFrameworkCore;

[DependsOn(
    typeof(BuildingManagementDomainModule),
    typeof(AbpEntityFrameworkCorePostgreSqlModule)
)]
public class BuildingManagementEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<BuildingManagementDbContext>(
    options =>
    {
        options.AddDefaultRepositories(
            includeAllEntities: true
        );

        options.AddRepository<
            Contract,
            EfCoreContractRepository
        >();
    }
);
    }
}
