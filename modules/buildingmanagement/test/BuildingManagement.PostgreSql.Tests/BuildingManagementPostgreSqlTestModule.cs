using System;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace BuildingManagement.PostgreSqlTests;

[DependsOn(
    typeof(BuildingManagementApplicationTestModule),
    typeof(BuildingManagementEntityFrameworkCoreModule)
)]
public sealed class BuildingManagementPostgreSqlTestModule : AbpModule
{
    internal const string DatabaseName = "ngoctrang_integration_test";
    internal const string ConnectionVariable = "BM_PG_TEST_CONNECTION";

    internal static string RequireSafeConnectionString()
    {
        var input = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException(
                $"Set {ConnectionVariable} for the dedicated PostgreSQL test database.");
        }

        var builder = new NpgsqlConnectionStringBuilder(input);
        if (!string.Equals(builder.Database, DatabaseName, StringComparison.Ordinal) ||
            !(string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(builder.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) ||
            builder.Port != 5432)
        {
            throw new InvalidOperationException(
                "Safety stop: tests require local PostgreSQL port 5432 " +
                "and database " + DatabaseName);
        }

        return builder.ConnectionString;
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var connection = RequireSafeConnectionString();

        // PostgreSQL timestamptz requires DateTime values with Kind=Utc.
        // ABP uses IClock for auditing fields such as CreationTime.
        Configure<AbpClockOptions>(options =>
        {
            options.Kind = DateTimeKind.Utc;
        });

        // DO NOT import BuildingManagementEntityFrameworkCoreTestModule:
        // that module disables database transactions for SQLite tests.
        Configure<AbpUnitOfWorkDefaultOptions>(options =>
        {
            options.TransactionBehavior = UnitOfWorkTransactionBehavior.Enabled;
        });

        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = connection;
            options.ConnectionStrings[BuildingManagementDbProperties.ConnectionStringName] = connection;
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.UseNpgsql();
        });

        // Create ONLY mapped test tables on the explicitly allowed database.
        // Do not call EnsureDeleted/Migrate against the application's database.
        using var db = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(connection)
                .Options);

        if (db.Model.FindEntityType(typeof(DepositSettlement)) is null)
        {
            throw new InvalidOperationException(
                "DepositSettlement EF mapping is missing; check local project changes.");
        }

        db.Database.EnsureCreated();
    }
}
