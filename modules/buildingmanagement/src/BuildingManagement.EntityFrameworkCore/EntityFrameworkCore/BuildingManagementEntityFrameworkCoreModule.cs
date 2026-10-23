using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore.Contracts;
using BuildingManagement.EntityFrameworkCore.RoomReservations;
using BuildingManagement.RoomReservations;
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
        options.AddRepository<
        RoomReservation,
        EfCoreRoomReservationRepository>();
        }
);
    }
}
