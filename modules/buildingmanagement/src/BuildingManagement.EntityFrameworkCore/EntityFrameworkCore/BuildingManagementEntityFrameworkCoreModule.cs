using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.Modularity;

namespace BuildingManagement.EntityFrameworkCore;

[DependsOn(
    typeof(BuildingManagementDomainModule),
    typeof(AbpEntityFrameworkCorePostgreSqlModule)
)]
public class BuildingManagementEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<BuildingManagementDbContext>(options =>
        {
            options.AddDefaultRepositories<IBuildingManagementDbContext>(includeAllEntities: true);

            /* Add custom repositories here. Example:
            * options.AddRepository<Question, EfCoreQuestionRepository>();
            */
        });
    }
}
