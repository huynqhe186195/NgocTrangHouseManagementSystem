using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace NgocTrangHouseManagementSystem.EntityFrameworkCore;

/* This class is needed for EF Core console commands
 * (like Add-Migration and Update-Database commands) */
public class NgocTrangHouseManagementSystemDbContextFactory : IDesignTimeDbContextFactory<NgocTrangHouseManagementSystemDbContext>
{
    public NgocTrangHouseManagementSystemDbContext CreateDbContext(string[] args)
    {
        // https://www.npgsql.org/efcore/release-notes/6.0.html#opting-out-of-the-new-timestamp-mapping-logic
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        
        var configuration = BuildConfiguration();
        
        NgocTrangHouseManagementSystemEfCoreEntityExtensionMappings.Configure();

        var builder = new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Default"));
        
        return new NgocTrangHouseManagementSystemDbContext(builder.Options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "../NgocTrangHouseManagementSystem.DbMigrator/"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables();

        return builder.Build();
    }
}
