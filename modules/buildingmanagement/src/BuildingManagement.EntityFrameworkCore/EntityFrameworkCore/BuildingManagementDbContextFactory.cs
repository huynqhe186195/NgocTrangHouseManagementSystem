using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BuildingManagement.EntityFrameworkCore;

/* This class is needed for EF Core console commands
 * (like Add-Migration and Update-Database commands) */
public class BuildingManagementDbContextFactory : IDesignTimeDbContextFactory<BuildingManagementDbContext>
{
    public BuildingManagementDbContext CreateDbContext(string[] args)
    {
        // Migration scaffolding is a MODEL-ONLY operation. The explicit
        // offline flag avoids reading live DB credentials, and Port=1
        // prevents accidental access to a real PostgreSQL server.
        // The generation helper sets this flag only for its child process.
        if (Environment.GetEnvironmentVariable("BM_INVOICE_MIGRATION_OFFLINE") == "1")
        {
            var offline = new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=bm_invoice_migration_offline;Username=offline;Password=offline;Timeout=1");
            return new BuildingManagementDbContext(offline.Options);
        }

        var configuration = BuildConfiguration();

        var builder = new DbContextOptionsBuilder<BuildingManagementDbContext>()
            .UseNpgsql(configuration.GetConnectionString(BuildingManagementDbProperties.ConnectionStringName)
                       ?? configuration.GetConnectionString("Default"));

        return new BuildingManagementDbContext(builder.Options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables();

        return builder.Build();
    }
}
