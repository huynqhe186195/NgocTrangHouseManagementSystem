using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.OperatorMemberships;
using BuildingManagement.Permissions;
using BuildingManagement.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Libs;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// Disposable PostgreSQL proof for the roster-only profile. The test creates
/// two databases with a unique bm_slicea_staging_* prefix, seeds persisted
/// Identity users/permission rows and persisted Building grants, then calls
/// the real conventional HTTP endpoint with server-resolved test principals.
/// It never touches 15433 or the persistent 5432 instance.
/// </summary>
public sealed class SliceARosterStagingPostgreSqlHttp_Tests
{
    private const string OptInVariable = "BM_ROSTER_PG_HTTP_OPT_IN";
    private const string OptInValue = "I_ACCEPT_DISPOSABLE_PG_ROSTER_HTTP";
    private const string AdminConnectionVariable = "BM_ROSTER_PG_ADMIN_CONNECTION";

    [Fact]
    public async Task ROSTERPGHTTP01_Persisted_A_B_Scope_And_ReadOnly_Routes()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != OptInValue)
            return;

        var source = Environment.GetEnvironmentVariable(AdminConnectionVariable)
            ?? throw new InvalidOperationException(
                $"Missing {AdminConnectionVariable}; refusing non-explicit PostgreSQL access.");
        var adminCs = RequireDisposableAdminConnection(source);
        var suffix = Guid.NewGuid().ToString("N");
        var identityName = "bm_slicea_staging_identity_" + suffix;
        var buildingName = "bm_slicea_staging_building_" + suffix;
        Assert.Matches(@"^bm_slicea_staging_identity_[0-9a-f]{32}$", identityName);
        Assert.Matches(@"^bm_slicea_staging_building_[0-9a-f]{32}$", buildingName);

        await using var admin = new NpgsqlConnection(adminCs.ConnectionString);
        await admin.OpenAsync();
        Assert.Equal("ngoctrang_integration_test", admin.Database);

        var identityCreated = false;
        var buildingCreated = false;
        WebApplication? app = null;
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE \"{identityName}\" TEMPLATE template0");
            identityCreated = true;
            await ExecuteAsync(admin, $"CREATE DATABASE \"{buildingName}\" TEMPLATE template0");
            buildingCreated = true;

            var identityCs = DatabaseConnection(adminCs, identityName);
            var buildingCs = DatabaseConnection(adminCs, buildingName);
            var actorA = Guid.NewGuid();
            var actorB = Guid.NewGuid();
            var actorC = Guid.NewGuid();

            await using (var identity = new NgocTrangHouseManagementSystemDbContext(
                new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
                    .UseNpgsql(identityCs).Options))
            {
                Assert.True(await identity.Database.EnsureCreatedAsync());
            }

            await using (var buildings = new BuildingManagementDbContext(
                new DbContextOptionsBuilder<BuildingManagementDbContext>()
                    .UseNpgsql(buildingCs).Options))
            {
                await buildings.Database.MigrateAsync();
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = identityCs,
                    ["ConnectionStrings:BuildingManagement"] = buildingCs,
                    ["BuildingManagement:SliceARosterStaging:Enabled"] = "true",
                    ["BuildingManagement:SliceARosterStaging:Activation"] =
                        "I_ACCEPT_ISOLATED_SLICE_A_ROSTER_READS",
                    ["BuildingManagement:SliceAStaging:Enabled"] = "false",
                    ["BuildingManagement:OwnerRecovery:Enabled"] = "false"
                })
                .Build();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Staging
            });
            builder.WebHost.UseTestServer();
            builder.Host.UseAutofac();
            builder.Configuration.AddConfiguration(config);
            builder.Services.ReplaceConfiguration(config);
            await builder.AddApplicationAsync<RosterPostgreSqlHttpModule>();
            app = builder.Build();
            await app.InitializeApplicationAsync();
            await app.StartAsync();

            await SeedIdentityUsersAsync(app, actorA, actorB, actorC);
            await SeedPermissionAsync(app, actorA);
            await SeedPermissionAsync(app, actorB);
            var (buildingA, buildingB) = await SeedBuildingAndGrantRowsAsync(
                app, actorA, actorB, actorC);

            using (var scope = app.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                Assert.IsType<AbpIdentityBuildingOperatorAccountProbe>(
                    services.GetRequiredService<IBuildingOperatorActiveAccountReader>());
                Assert.IsType<SliceAStagingGrantReader>(
                    services.GetRequiredService<IBuildingOperatorGrantReader>());
                Assert.IsType<SliceAStagingOperatorMembershipReadStore>(
                    services.GetRequiredService<IOperatorMembershipReadStore>());
                Assert.IsType<DenyAllBuildingOperatorGrantMutationWriter>(
                    services.GetRequiredService<IBuildingOperatorGrantMutationWriter>());
                Assert.IsType<DenyAllBuildingOwnerBootstrapWriter>(
                    services.GetRequiredService<IBuildingOwnerBootstrapWriter>());
                Assert.IsType<DenyAllBuildingScopedWriteBoundary>(
                    services.GetRequiredService<IBuildingScopedWriteBoundary>());
            }

            var actions = app.Services
                .GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .ToArray();
            var rosterAction = Assert.Single(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(OperatorMembershipsAppService) &&
                action.MethodInfo.Name == nameof(
                    IOperatorMembershipsAppService.GetListAsync));
            var route = Assert.IsType<string>(
                rosterAction.AttributeRouteInfo?.Template);
            Assert.Equal("api/building-management/operator-memberships", route);
            Assert.DoesNotContain(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(SliceAStagingOperatorGrantsController));

            using var anonymous = app.GetTestClient();
            using (var response = await anonymous.GetAsync("/" + route))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            using var clientA = app.GetTestClient();
            clientA.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Slice-A-Test-Actor", actorA.ToString("D"));
            clientA.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Actor-Id", actorB.ToString("D"));
            using (var response = await clientA.GetAsync("/" + route))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await AssertOnlyBuildingAsync(response, buildingA);
            }

            using var clientB = app.GetTestClient();
            clientB.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Slice-A-Test-Actor", actorB.ToString("D"));
            using (var response = await clientB.GetAsync("/" + route))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await AssertOnlyBuildingAsync(response, buildingB);
            }

            using (var response = await clientA.GetAsync(
                "/" + route + "?buildingId=" + buildingB.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using var clientC = app.GetTestClient();
            clientC.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Slice-A-Test-Actor", actorC.ToString("D"));
            using (var response = await clientC.GetAsync("/" + route))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using (var response = await clientA.GetAsync(
                "/api/slice-a-staging/operator-grants"))
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            if (app is not null)
            {
                try { await app.StopAsync(); } catch { }
                await app.DisposeAsync();
            }

            await DropDatabaseAsync(admin, buildingName, buildingCreated);
            await DropDatabaseAsync(admin, identityName, identityCreated);
        }
    }

    private static IdentityUser NewUser(Guid id, string name)
        => new(id, name, name + "@roster.invalid");

    private static async Task SeedIdentityUsersAsync(
        WebApplication app, Guid actorA, Guid actorB, Guid actorC)
    {
        using var scope = app.Services.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>()
            .Begin(requiresNew: true, isTransactional: true);
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextProvider<NgocTrangHouseManagementSystemDbContext>>()
            .GetDbContextAsync();
        db.Users.AddRange(
            NewUser(actorA, "roster-a"),
            NewUser(actorB, "roster-b"),
            NewUser(actorC, "roster-c"));
        await db.SaveChangesAsync();
        await uow.CompleteAsync();
    }

    private static async Task SeedPermissionAsync(WebApplication app, Guid actor)
    {
        using var scope = app.Services.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>()
            .Begin(requiresNew: true, isTransactional: true);
        await scope.ServiceProvider.GetRequiredService<IPermissionManager>()
            .SetForUserAsync(actor,
                BuildingManagementPermissions.Buildings.ReadOperators, true);
        await uow.CompleteAsync();
    }

    private static async Task<(Guid BuildingA, Guid BuildingB)>
        SeedBuildingAndGrantRowsAsync(
            WebApplication app, Guid actorA, Guid actorB, Guid actorC)
    {
        var buildingA = Guid.NewGuid();
        var buildingB = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var scope = app.Services.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>()
            .Begin(requiresNew: true, isTransactional: true);
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
            .GetDbContextAsync();
        db.Buildings.AddRange(
            new Building(buildingA, "Roster Building A", "Disposable A"),
            new Building(buildingB, "Roster Building B", "Disposable B"));
        db.OperatorBuildingGrants.AddRange(
            BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), actorA, buildingA, actorA,
                now.AddMinutes(-30), null),
            BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), actorB, buildingB, actorB,
                now.AddMinutes(-30), null));

        var expired = BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), actorB, buildingA, actorA,
            now.AddHours(-3), now.AddHours(-2));
        var revoked = BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), actorC, buildingA, actorA,
            now.AddHours(-2), null);
        revoked.Revoke(actorA, now.AddHours(-1));
        var scheduled = BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), actorC, buildingA, actorA,
            now.AddHours(1), null);
        db.OperatorBuildingGrants.AddRange(expired, revoked, scheduled);
        await db.SaveChangesAsync();
        await uow.CompleteAsync();
        return (buildingA, buildingB);
    }

    private static async Task AssertOnlyBuildingAsync(
        HttpResponseMessage response, Guid expectedBuilding)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items");
        Assert.NotEmpty(items.EnumerateArray());
        foreach (var item in items.EnumerateArray())
            Assert.Equal(expectedBuilding, item.GetProperty("buildingId").GetGuid());
    }

    private static NpgsqlConnectionStringBuilder RequireDisposableAdminConnection(
        string input)
    {
        var builder = new NpgsqlConnectionStringBuilder(input)
        {
            Pooling = false,
            CommandTimeout = 90
        };
        if (builder.Host is not ("127.0.0.1" or "localhost") ||
            builder.Port != 15432 ||
            builder.Database != "ngoctrang_integration_test")
            throw new InvalidOperationException(
                "Roster PostgreSQL test requires local disposable database " +
                "ngoctrang_integration_test on port 15432.");
        return builder;
    }

    private static string DatabaseConnection(
        NpgsqlConnectionStringBuilder source, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = database,
            Pooling = false,
            CommandTimeout = 90
        };
        return builder.ConnectionString;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(
        NpgsqlConnection admin, string database, bool created)
    {
        if (!created) return;
        await ExecuteAsync(admin,
            $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
    }

    [DependsOn(
        typeof(AbpAutofacModule),
        typeof(NgocTrangHouseManagementSystemEntityFrameworkCoreModule),
        typeof(BuildingManagementApplicationModule),
        typeof(BuildingManagementHttpApiModule))]
    public sealed class RosterPostgreSqlHttpModule : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context)
        {
            PreConfigure<Microsoft.Extensions.DependencyInjection.IMvcBuilder>(mvc =>
            {
                mvc.AddApplicationPartIfNotExists(
                    typeof(SliceAStagingOperatorGrantsController).Assembly);
                mvc.ConfigureApplicationPartManager(parts =>
                {
                    foreach (var provider in parts.FeatureProviders
                        .OfType<ControllerFeatureProvider>().ToArray())
                        parts.FeatureProviders.Remove(provider);
                    parts.FeatureProviders.Add(
                        new SliceARosterStagingControllerFeatureProvider(false));
                });
            });
            PreConfigure<AbpAspNetCoreMvcOptions>(options =>
                options.ConventionalControllers.Create(
                    typeof(BuildingManagementApplicationModule).Assembly,
                    settings => settings.RootPath = "building-management"));
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var config = context.Services.GetConfiguration();
            var identityCs = config.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Identity connection missing.");
            var buildingCs = config.GetConnectionString("BuildingManagement")
                ?? throw new InvalidOperationException("Building connection missing.");
            Configure<AbpDbConnectionOptions>(options =>
            {
                options.ConnectionStrings.Default = identityCs;
                options.ConnectionStrings[
                    BuildingManagementDbProperties.ConnectionStringName] = buildingCs;
            });
            Configure<AbpDbContextOptions>(options => options.UseNpgsql());
            context.Services.AddAuthentication("RosterPg")
                .AddScheme<AuthenticationSchemeOptions, RosterPgAuthenticationHandler>(
                    "RosterPg", _ => { });
            context.Services.AddAuthorization();
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOperatorActiveAccountReader>(sp =>
                    new AbpIdentityBuildingOperatorAccountProbe(
                        sp.GetRequiredService<IIdentityUserRepository>(),
                        sp.GetRequiredService<IdentityUserManager>())));
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOperatorGrantReader, SliceAStagingGrantReader>());
            context.Services.Replace(ServiceDescriptor.Transient<
                IOperatorMembershipReadStore,
                SliceAStagingOperatorMembershipReadStore>());
            Configure<AbpMvcLibsOptions>(options => options.CheckLibs = false);
            Configure<MvcOptions>(options => options.Conventions.Add(
                new BuildingManagementAuthenticatedControllerConvention()));
        }

        public override void OnApplicationInitialization(
            ApplicationInitializationContext context)
        {
            var app = context.GetApplicationBuilder();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseConfiguredEndpoints();
        }
    }

    private sealed class RosterPgAuthenticationHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public RosterPgAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Slice-A-Test-Actor", out var raw) ||
                !Guid.TryParse(raw.ToString(), out var actor) || actor == Guid.Empty)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(AbpClaimTypes.UserId, actor.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, actor.ToString("D"))
            }, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
