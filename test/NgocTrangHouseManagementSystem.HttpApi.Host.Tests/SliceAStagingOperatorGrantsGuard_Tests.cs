using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>Fast, production-safe tests: no network, EF or DB connection.</summary>
public sealed class SliceAStagingOperatorGrantsGuard_Tests
{
    [Fact]
    public async Task STAGEAPI01_Outside_Staging_And_Disabled_Endpoints_Deny_Before_Coordinator()
    {
        var enabled = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["BuildingManagement:SliceAStaging:Enabled"] = "true",
                ["BuildingManagement:SliceAStaging:Activation"] =
                    "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES"
            }).Build();
        var disabled = new ConfigurationBuilder().Build();
        var request = new AssignOperatorGrantRequest(
            Guid.NewGuid(), Guid.NewGuid(), null);
        var revoke = new RevokeOperatorGrantRequest(
            Guid.NewGuid(), Guid.NewGuid(), 1);

        // The coordinator is deliberately NOT constructed. An accidental
        // call to it would throw NRE and fail the test. The environment gate
        // must reject without side effects or privileges.
        var production = new SliceAStagingOperatorGrantsController(
            new StubHostEnvironment { EnvironmentName = Environments.Production },
            enabled, null!);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            production.AssignAsync(request));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            production.RevokeAsync(revoke));

        var stagingDisabled = new SliceAStagingOperatorGrantsController(
            new StubHostEnvironment { EnvironmentName = Environments.Staging },
            disabled, null!);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            stagingDisabled.AssignAsync(request));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            stagingDisabled.RevokeAsync(revoke));
    }

    [Fact]
    public void STAGEAPI02_Only_Explicit_Authenticated_Post_And_Transactional_Actions()
    {
        var t = typeof(SliceAStagingOperatorGrantsController);
        Assert.NotNull(t.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(t.GetCustomAttribute<ApiControllerAttribute>());
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.DeclaringType == t).ToArray();
        Assert.Equal(2, methods.Length);
        foreach (var method in methods)
        {
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<UnitOfWorkAttribute>());
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public void STAGEAPI03_Staging_Activation_Requires_Two_Distinct_Isolated_Postgres_Databases()
    {
        // Test the exact startup guard by reflection: no actual database
        // connection, schema mutation, credentials or production activation.
        var gate = typeof(global::NgocTrangHouseManagementSystem
            .NgocTrangHouseManagementSystemHttpApiHostModule).GetMethod(
                "HaveTwoIsolatedSliceADatabases",
                BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(gate);
        bool Allowed(string? buildings, string? identity)
            => Assert.IsType<bool>(gate!.Invoke(null, new object?[] {
                buildings, identity
            }));

        const string buildings =
            "Host=127.0.0.1;Port=5432;Database=bm_slicea_staging_buildings;Username=ephemeral";
        const string identity =
            "Host=127.0.0.1;Port=5432;Database=bm_slicea_staging_identity;Username=ephemeral";
        Assert.True(Allowed(buildings, identity));
        Assert.False(Allowed(buildings, buildings));
        Assert.False(Allowed(buildings, ""));
        Assert.False(Allowed(buildings, null));
        Assert.False(Allowed(buildings,
            "Host=127.0.0.1;Database=production_identity;Username=ephemeral"));
        Assert.False(Allowed(
            "Host=127.0.0.1;Database=bm_slicea_staging_;Username=ephemeral",
            identity));
        Assert.False(Allowed(
            "Host=127.0.0.1;Database=bm_slicea_staging_buildings",
            identity));
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SliceA.Guard.Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; }
            = new NullFileProvider();
    }
}
