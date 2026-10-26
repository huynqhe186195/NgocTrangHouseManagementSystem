using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BuildingManagement.Security;
using BuildingManagement.Tests.Security;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;

namespace BuildingManagement.EntityFrameworkCore;

[DependsOn(
    typeof(BuildingManagementApplicationTestModule),
    typeof(BuildingManagementEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreSqliteModule)
)]
public class BuildingManagementEntityFrameworkCoreTestModule : AbpModule
{
    private AbpUnitTestSqliteDatabase? _database;

    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<AbpSqliteOptions>(x => x.BusyTimeout = null);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAlwaysDisableUnitOfWorkTransaction();

        // Legacy room life-cycle business tests explicitly opt in to a
        // narrowly scoped test-only room fixture. Every other operation
        // remains DENIED, including all new security regression tests.
        // This registration belongs to the disposable SQLite test module
        // ONLY, never the running application or HTTP host.
        context.Services.Replace(
            ServiceDescriptor.Transient<IBuildingCrudAccessPolicy,
                LegacyRoomBusinessFixturePolicy>());

        _database = new AbpUnitTestSqliteDatabase();
        _database.CreateTables(
            new BuildingManagementDbContext(new DbContextOptionsBuilder<BuildingManagementDbContext>().UseSqlite(_database.ConnectionString).Options));

        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = _database.ConnectionString;
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                configurationContext.UseSqlite();
            });
        });
    }

    public override void OnApplicationShutdown(ApplicationShutdownContext context)
    {
        _database?.Dispose();
    }
}
