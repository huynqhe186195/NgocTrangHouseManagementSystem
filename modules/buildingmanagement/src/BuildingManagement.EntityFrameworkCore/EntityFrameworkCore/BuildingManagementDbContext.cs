using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore;

[ConnectionStringName(BuildingManagementDbProperties.ConnectionStringName)]
public class BuildingManagementDbContext : AbpDbContext<BuildingManagementDbContext>, IBuildingManagementDbContext
{
    /* Add DbSet for each Aggregate Root here. Example:
     * public DbSet<Question> Questions { get; set; }
     */
    // The register entity Building into database
    public DbSet<Building> Buildings { get; set; } = default!;
    public DbSet<Floor> Floors { get; set; } = default!;
    public DbSet<Room> Rooms { get; set; } = default!;

    public BuildingManagementDbContext(DbContextOptions<BuildingManagementDbContext> options)
        : base(options)
    {

    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigureBuildingManagement();
    }
}
