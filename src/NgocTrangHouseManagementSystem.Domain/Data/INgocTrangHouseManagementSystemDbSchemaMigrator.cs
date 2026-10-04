using System.Threading.Tasks;

namespace NgocTrangHouseManagementSystem.Data;

public interface INgocTrangHouseManagementSystemDbSchemaMigrator
{
    Task MigrateAsync();
}
