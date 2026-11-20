using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using BuildingManagement.OperatorMemberships;
using BuildingManagement.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NgocTrangHouseManagementSystem.Security;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// Startup and MVC inventory checks for the roster-only staging profile.
/// These tests do not connect to PostgreSQL or start the host.
/// </summary>
public sealed class SliceARosterStagingProfile_Tests
{
    private const string Activation = "I_ACCEPT_ISOLATED_SLICE_A_ROSTER_READS";

    [Fact]
    public void ROSTERPROFILE01_Default_Off_Does_Not_Add_Adapters()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();

        global::NgocTrangHouseManagementSystem
            .NgocTrangHouseManagementSystemHttpApiHostModule
            .ConfigureSliceARosterStagingIfEnabled(
                configuration, Environment(Environments.Staging), services, false);

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ImplementationType == typeof(SliceAStagingGrantReader) ||
            descriptor.ImplementationType == typeof(SliceAStagingOperatorMembershipReadStore));
    }

    [Fact]
    public void ROSTERPROFILE02_Valid_Profile_Replaces_Only_Three_Read_Boundaries()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(valid: true);

        global::NgocTrangHouseManagementSystem
            .NgocTrangHouseManagementSystemHttpApiHostModule
            .ConfigureSliceARosterStagingIfEnabled(
                configuration, Environment(Environments.Staging), services, true);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IBuildingOperatorActiveAccountReader));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IBuildingOperatorGrantReader) &&
            descriptor.ImplementationType == typeof(SliceAStagingGrantReader));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IOperatorMembershipReadStore) &&
            descriptor.ImplementationType ==
                typeof(SliceAStagingOperatorMembershipReadStore));

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ImplementationType == typeof(SliceAStagingFirstOwnerWriter) ||
            descriptor.ImplementationType == typeof(SliceAStagingGrantMutationWriter) ||
            descriptor.ImplementationType == typeof(SliceAStagingWriteBoundary) ||
            descriptor.ImplementationType == typeof(SliceAStagingIdentityCommitLease) ||
            descriptor.ImplementationType == typeof(SliceAStagingOperatorGrantQuery));
    }

    [Fact]
    public void ROSTERPROFILE03_Legacy_And_Roster_Profiles_Cannot_Both_Be_Enabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            global::NgocTrangHouseManagementSystem
                .NgocTrangHouseManagementSystemHttpApiHostModule
                .ValidateSliceAProfileSelection(true, true));

        global::NgocTrangHouseManagementSystem
            .NgocTrangHouseManagementSystemHttpApiHostModule
            .ValidateSliceAProfileSelection(false, true);
        global::NgocTrangHouseManagementSystem
            .NgocTrangHouseManagementSystemHttpApiHostModule
            .ValidateSliceAProfileSelection(true, false);
    }

    [Theory]
    [InlineData("Production", Activation)]
    [InlineData("Staging", "wrong")]
    public void ROSTERPROFILE03_Environment_And_Activation_Are_Exact(
        string environment, string activation)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(valid: true, activation: activation);

        Assert.Throws<InvalidOperationException>(() =>
            global::NgocTrangHouseManagementSystem
                .NgocTrangHouseManagementSystemHttpApiHostModule
                .ConfigureSliceARosterStagingIfEnabled(
                    configuration, Environment(environment), services, true));
    }

    [Fact]
    public void ROSTERPROFILE04_Rejects_Recovery_And_Missing_Or_Unsafe_Databases()
    {
        var cases = new[]
        {
            Configuration(valid: true, recovery: true),
            Configuration(valid: false),
            Configuration(valid: true, sameDatabase: true),
            Configuration(valid: true, buildingDatabase: "postgres")
        };

        foreach (var configuration in cases)
        {
            Assert.Throws<InvalidOperationException>(() =>
                global::NgocTrangHouseManagementSystem
                    .NgocTrangHouseManagementSystemHttpApiHostModule
                    .ConfigureSliceARosterStagingIfEnabled(
                        configuration, Environment(Environments.Staging),
                        new ServiceCollection(), true));
        }
    }

    [Fact]
    public void ROSTERPROFILE05_Explicit_Grant_Controller_Is_Hidden_Outside_Legacy_Lane()
    {
        var withoutLegacy = DiscoverControllers(legacySliceAEnabled: false);
        Assert.DoesNotContain(withoutLegacy, type =>
            type == typeof(SliceAStagingOperatorGrantsController));

        var withLegacy = DiscoverControllers(legacySliceAEnabled: true);
        Assert.Contains(withLegacy, type =>
            type == typeof(SliceAStagingOperatorGrantsController));
    }

    [Fact]
    public async Task ROSTERPROFILE06_Mvc_Inventory_And_Request_Are_ReadOnly()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Staging
        });
        builder.WebHost.UseTestServer();
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(SliceAStagingOperatorGrantsController).Assembly)
            .ConfigureApplicationPartManager(parts =>
            {
                foreach (var provider in parts.FeatureProviders
                    .OfType<ControllerFeatureProvider>().ToArray())
                    parts.FeatureProviders.Remove(provider);

                parts.FeatureProviders.Add(
                    new SliceARosterStagingControllerFeatureProvider(false));
            });

        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        var actions = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToArray();
        Assert.DoesNotContain(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(SliceAStagingOperatorGrantsController));
        Assert.DoesNotContain(actions, action =>
            string.Equals(action.AttributeRouteInfo?.Template,
                "api/slice-a-staging/operator-grants",
                StringComparison.OrdinalIgnoreCase));

        using var response = await app.GetTestClient().GetAsync(
            "/api/slice-a-staging/operator-grants");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ROSTERPROFILE07_Production_Default_Provider_Is_DenyAll_For_Roster()
    {
        var store = new DenyAllOperatorMembershipReadStore();
        Assert.IsType<DenyAllOperatorMembershipReadStore>(store);

        var provider = new SliceARosterStagingControllerFeatureProvider(false);
        Assert.NotNull(provider);
    }

    [Fact]
    public void ROSTERPROFILE08_Mutation_Boundaries_Remain_DenyAll()
    {
        var services = new ServiceCollection();
        services.AddTransient<IBuildingOperatorActiveAccountReader,
            DenyAllBuildingOperatorActiveAccountReader>();
        services.AddTransient<IBuildingOperatorGrantReader,
            DenyAllBuildingOperatorGrantReader>();
        services.AddTransient<IOperatorMembershipReadStore,
            DenyAllOperatorMembershipReadStore>();
        services.AddTransient<IBuildingOperatorGrantMutationWriter,
            DenyAllBuildingOperatorGrantMutationWriter>();
        services.AddTransient<IBuildingOwnerBootstrapWriter,
            DenyAllBuildingOwnerBootstrapWriter>();
        services.AddTransient<IBuildingScopedWriteBoundary,
            DenyAllBuildingScopedWriteBoundary>();

        global::NgocTrangHouseManagementSystem
            .NgocTrangHouseManagementSystemHttpApiHostModule
            .ConfigureSliceARosterStagingIfEnabled(
                Configuration(valid: true),
                Environment(Environments.Staging), services, true);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<DenyAllBuildingOperatorGrantMutationWriter>(
            provider.GetRequiredService<IBuildingOperatorGrantMutationWriter>());
        Assert.IsType<DenyAllBuildingOwnerBootstrapWriter>(
            provider.GetRequiredService<IBuildingOwnerBootstrapWriter>());
        Assert.IsType<DenyAllBuildingScopedWriteBoundary>(
            provider.GetRequiredService<IBuildingScopedWriteBoundary>());

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IBuildingOperatorActiveAccountReader) &&
            descriptor.ImplementationFactory is not null);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IBuildingOperatorGrantReader) &&
            descriptor.ImplementationType == typeof(SliceAStagingGrantReader));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IOperatorMembershipReadStore) &&
            descriptor.ImplementationType ==
                typeof(SliceAStagingOperatorMembershipReadStore));
    }

    private static IReadOnlyCollection<Type> DiscoverControllers(bool legacySliceAEnabled)
    {
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(
            typeof(SliceAStagingOperatorGrantsController).Assembly));
        manager.FeatureProviders.Add(
            new SliceARosterStagingControllerFeatureProvider(legacySliceAEnabled));

        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);
        return feature.Controllers.Select(info => info.AsType()).ToArray();
    }

    private static IConfiguration Configuration(
        bool valid = false,
        string activation = Activation,
        bool recovery = false,
        bool sameDatabase = false,
        string? buildingDatabase = null)
    {
        var identity = "bm_slicea_staging_identity_test";
        var building = buildingDatabase ?? "bm_slicea_staging_building_test";
        if (sameDatabase)
            building = identity;
        if (!valid)
        {
            identity = "production_identity";
            building = "production_building";
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BuildingManagement:SliceARosterStaging:Activation"] = activation,
                ["BuildingManagement:OwnerRecovery:Enabled"] =
                    recovery ? "true" : "false",
                ["ConnectionStrings:Default"] =
                    $"Host=127.0.0.1;Port=15432;Database={identity};Username=roster",
                ["ConnectionStrings:BuildingManagement"] =
                    $"Host=127.0.0.1;Port=15432;Database={building};Username=roster"
            })
            .Build();
    }

    private static IHostEnvironment Environment(string name)
        => new TestHostEnvironment { EnvironmentName = name };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Staging;
        public string ApplicationName { get; set; } = "SliceARosterStaging.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
