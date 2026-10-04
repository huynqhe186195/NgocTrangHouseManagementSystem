using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;

namespace NgocTrangHouseManagementSystem.Data;

/* This is used if database provider does't define
 * INgocTrangHouseManagementSystemDbSchemaMigrator implementation.
 */
public class NullNgocTrangHouseManagementSystemDbSchemaMigrator : INgocTrangHouseManagementSystemDbSchemaMigrator, ITransientDependency
{
    public Task MigrateAsync()
    {
        return Task.CompletedTask;
    }
}
