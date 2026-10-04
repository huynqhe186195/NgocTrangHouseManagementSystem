using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NgocTrangHouseManagementSystem.Data;
using Volo.Abp.DependencyInjection;

namespace NgocTrangHouseManagementSystem.EntityFrameworkCore;

public class EntityFrameworkCoreNgocTrangHouseManagementSystemDbSchemaMigrator
    : INgocTrangHouseManagementSystemDbSchemaMigrator, ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;

    public EntityFrameworkCoreNgocTrangHouseManagementSystemDbSchemaMigrator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task MigrateAsync()
    {
        /* We intentionally resolving the NgocTrangHouseManagementSystemDbContext
         * from IServiceProvider (instead of directly injecting it)
         * to properly get the connection string of the current tenant in the
         * current scope.
         */

        await _serviceProvider
            .GetRequiredService<NgocTrangHouseManagementSystemDbContext>()
            .Database
            .MigrateAsync();
    }
}
