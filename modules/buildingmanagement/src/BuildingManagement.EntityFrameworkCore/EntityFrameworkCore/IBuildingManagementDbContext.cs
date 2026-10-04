using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore;

[ConnectionStringName(BuildingManagementDbProperties.ConnectionStringName)]
public interface IBuildingManagementDbContext : IEfCoreDbContext
{
    /* Add DbSet for each Aggregate Root here. Example:
     * DbSet<Question> Questions { get; }
     */
}
