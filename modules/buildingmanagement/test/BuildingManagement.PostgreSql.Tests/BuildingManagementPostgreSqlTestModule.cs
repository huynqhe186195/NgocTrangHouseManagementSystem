using System;
using System.IO;
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

        // An existing database is not upgraded by EnsureCreated().
        // Keep this targeted compatibility patch strictly inside the safe
        // localhost integration-test connection; never run against app data.
        EnsureMeterReplacementTestSchema(connection);
    }

    private static void EnsureMeterReplacementTestSchema(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // A fresh EnsureCreated() database already contains the current
        // model. The patch is only for older test databases.
        using (var existsCommand = new NpgsqlCommand(
            @"SELECT to_regclass('public.""BuildingManagementMeterReplacements""') IS NOT NULL",
            connection))
        {
            if (Convert.ToBoolean(existsCommand.ExecuteScalar()))
                return;
        }

        using var scriptStream = typeof(BuildingManagementPostgreSqlTestModule)
            .Assembly.GetManifestResourceStream("Phase2G2B1_TestSchema.sql")
            ?? throw new InvalidOperationException(
                "Missing embedded Phase2G2B1_TestSchema.sql test resource.");

        using var reader = new StreamReader(scriptStream);
        var script = reader.ReadToEnd();

        // The script checks current_database(), creates only the missing
        // table and its constraints, and rolls back entirely on failure.
        // It intentionally does NOT write EF migration history.
        using var apply = new NpgsqlCommand(script, connection)
        {
            CommandTimeout = 60
        };
        apply.ExecuteNonQuery();
    }
}
