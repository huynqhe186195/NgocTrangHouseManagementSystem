using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Permissions;
using BuildingManagement.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using Npgsql;
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
/// Real ABP authenticated HTTP against TWO throwaway PostgreSQL databases.
/// Executed only by the dedicated opt-in PostgreSQL CI workflow, never by
/// ordinary Full or against a production application/database. The fixture
/// registers the exact opt-in staging persistence adapters but NOT the
/// production Host module and does NOT open any production Deny-All path.
/// </summary>
public sealed class SliceAStagingPostgreSqlHttp_Tests
{
    private const string OptIn = "BM_PG_SLICE_A_HTTP_OPT_IN";
    private const string Accepted = "I_ACCEPT_DISPOSABLE_PG_STAGING_HTTP";

    [Fact]
    public async Task STAGEPGHTTP01_Root_Bootstrap_Real_Permissions_And_Two_Building_Revocation()
    {
        // The normal Windows Full suite has no PostgreSQL service. Its
        // execution is deliberately inert; the isolated PG workflow sets
        // the exact opt-in and FILTERS this test into a genuine run.
        if (Environment.GetEnvironmentVariable(OptIn) != Accepted)
            return;

        var source = Environment.GetEnvironmentVariable("BM_PG_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Missing isolated PG test connection.");
        var adminCs = new NpgsqlConnectionStringBuilder(source)
        {
            Pooling = false,
            CommandTimeout = 90
        };
        if (adminCs.Port != 5432 ||
            adminCs.Database != "ngoctrang_integration_test" ||
            adminCs.Host is not ("127.0.0.1" or "localhost"))
            throw new InvalidOperationException("Refusing non-local PostgreSQL test server.");

        var suffix = Guid.NewGuid().ToString("N");
        var idName = "bm_slicea_staging_identity_" + suffix;
        var buildingName = "bm_slicea_staging_buildings_" + suffix;
        Assert.Matches(@"^bm_slicea_staging_identity_[0-9a-f]{32}$", idName);
        Assert.Matches(@"^bm_slicea_staging_buildings_[0-9a-f]{32}$", buildingName);
        await using var admin = new NpgsqlConnection(adminCs.ConnectionString);
        await admin.OpenAsync();
        Assert.Equal("ngoctrang_integration_test", admin.Database);

        var identityCreated = false;
        var buildingsCreated = false;
        WebApplication? app = null;
        try
        {
            await ExecuteAsync(admin, $"""CREATE DATABASE "{idName}" TEMPLATE template0""");
            identityCreated = true;
            await ExecuteAsync(admin, $"""CREATE DATABASE "{buildingName}" TEMPLATE template0""");
            buildingsCreated = true;
            var idBuilder = new NpgsqlConnectionStringBuilder(adminCs.ConnectionString)
            {
                Database = idName, Pooling = false
            };
            var buildingBuilder = new NpgsqlConnectionStringBuilder(adminCs.ConnectionString)
            {
                Database = buildingName, Pooling = false
            };
            var idCs = idBuilder.ConnectionString;
            var buildingCs = buildingBuilder.ConnectionString;
            var root = Guid.NewGuid();
            var operatorB = Guid.NewGuid();

            // Actual ABP Identity/Permission EF model; actual runtime
            // BuildingManagement migration including the grant table.
            await using (var identity = new NgocTrangHouseManagementSystemDbContext(
                new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
                    .UseNpgsql(idCs).Options))
            {
                Assert.True(await identity.Database.EnsureCreatedAsync());
            }
            await using (var buildings = new BuildingManagementDbContext(
                new DbContextOptionsBuilder<BuildingManagementDbContext>()
                    .UseNpgsql(buildingCs).Options))
                await buildings.Database.MigrateAsync();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = idCs,
                    ["ConnectionStrings:BuildingManagement"] = buildingCs,
                    ["BuildingManagement:Bootstrap:RootUserIds:0"] =
                        root.ToString("D"),
                    ["BuildingManagement:SliceAStaging:Enabled"] = "true",
                    ["BuildingManagement:SliceAStaging:Activation"] =
                        "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES"
                }).Build();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Staging
            });
            builder.WebHost.UseTestServer();
            builder.Host.UseAutofac();
            builder.Configuration.AddConfiguration(config);
            builder.Services.ReplaceConfiguration(config);
            await builder.AddApplicationAsync<SliceAPostgreSqlHttpModule>();
            app = builder.Build();
            await app.InitializeApplicationAsync();
            await app.StartAsync();

            using (var scope = app.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                Assert.IsType<AbpIdentityBuildingOperatorAccountProbe>(
                    services.GetRequiredService<IBuildingOperatorActiveAccountReader>());
                Assert.IsType<SliceAStagingGrantReader>(
                    services.GetRequiredService<IBuildingOperatorGrantReader>());
                Assert.IsType<SliceAStagingFirstOwnerWriter>(
                    services.GetRequiredService<IBuildingOwnerBootstrapWriter>());
                Assert.IsType<SliceAStagingGrantMutationWriter>(
                    services.GetRequiredService<IBuildingOperatorGrantMutationWriter>());
                Assert.IsType<SliceAStagingWriteBoundary>(
                    services.GetRequiredService<IBuildingScopedWriteBoundary>());
                var db = services.GetRequiredService<NgocTrangHouseManagementSystemDbContext>();
                db.Users.AddRange(
                    new IdentityUser(root, "root-" + root.ToString("N"),
                        "root-" + root.ToString("N") + "@example.test"),
                    new IdentityUser(operatorB, "operator-" + operatorB.ToString("N"),
                        "operator-" + operatorB.ToString("N") + "@example.test"));
                await db.SaveChangesAsync();

                using var unit = services.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var permissions = services.GetRequiredService<IPermissionManager>();
                foreach (var name in new[] {
                    BuildingManagementPermissions.Buildings.BootstrapOwner,
                    BuildingManagementPermissions.Buildings.Read,
                    BuildingManagementPermissions.Buildings.Manage,
                    BuildingManagementPermissions.Buildings.AssignOperators,
                    BuildingManagementPermissions.Buildings.RevokeOperators })
                    await permissions.SetForUserAsync(root, name, true);
                await permissions.SetForUserAsync(operatorB,
                    BuildingManagementPermissions.Buildings.Read, true);
                await permissions.SetForUserAsync(operatorB,
                    BuildingManagementPermissions.Buildings.RevokeOperators, true);
                await unit.CompleteAsync();
            }

            var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToArray();
            string Route(string method) => "/" + Assert.IsType<string>(
                Assert.Single(actions, a =>
                    a.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
                    a.MethodInfo.Name == method).AttributeRouteInfo?.Template)
                .TrimStart('/');
            var createPath = Route(nameof(BuildingAppService.CreateAsync));
            var listPath = Route(nameof(BuildingAppService.GetListAsync));
            var getRoute = Route(nameof(BuildingAppService.GetAsync));
            string GetAt(Guid id) => Regex.Replace(
                getRoute, @"\{[^}]+\}", id.ToString("D"));

            using var clientRoot = app.GetTestClient();
            using var clientB = app.GetTestClient();
            clientRoot.DefaultRequestHeaders.Add("X-Slice-A-Test-Actor", root.ToString("D"));
            clientB.DefaultRequestHeaders.Add("X-Slice-A-Test-Actor", operatorB.ToString("D"));

            async Task<Guid> BootstrapAsync(string name)
            {
                using var json = new StringContent(JsonSerializer.Serialize(
                    new { name, address = "Isolated PG only" }), Encoding.UTF8,
                    "application/json");
                using var response = await clientRoot.PostAsync(createPath, json);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
                    $"Bootstrap {response.StatusCode}: {body}");
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("id").GetGuid();
            }

            // Only configured root may create, even when B can read buildings.
            using (var payload = new StringContent(
                """{"name":"Forbidden","address":"Never created"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(createPath, payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var a = await BootstrapAsync("PG Building A");
            var b = await BootstrapAsync("PG Building B");
            Assert.NotEqual(a, b);
            using (var response = await clientB.GetAsync(GetAt(a)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(listPath))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            const string grants = "/api/slice-a-staging/operator-grants";
            using (var payload = new StringContent(JsonSerializer.Serialize(
                new { buildingId = b, operatorUserId = operatorB }),
                Encoding.UTF8, "application/json"))
            using (var response = await clientRoot.PostAsync(grants, payload))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(GetAt(a)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(listPath))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains(b.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(a.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }
            // The PostgreSQL-backed HTTP path must also honor fresh ABP
            // Identity account state and durable user permission changes.
            // A still-valid membership is NEVER sufficient by itself.
            async Task ChangeAccountAsync(bool active, bool locked)
            {
                await using var db = new NgocTrangHouseManagementSystemDbContext(
                    new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
                        .UseNpgsql(idCs).Options);
                var user = await db.Users.SingleAsync(x => x.Id == operatorB);
                user.SetIsActive(active);
                db.Entry(user).Property(x => x.LockoutEnabled).CurrentValue = locked;
                db.Entry(user).Property(x => x.LockoutEnd).CurrentValue =
                    locked ? DateTimeOffset.UtcNow.AddMinutes(15) : (DateTimeOffset?)null;
                await db.SaveChangesAsync();
            }
            async Task SetReadPermissionAsync(bool enabled)
            {
                using var scope = app.Services.CreateScope();
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Buildings.Read, enabled);
                await uow.CompleteAsync();
            }
            await ChangeAccountAsync(active: false, locked: false);
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await ChangeAccountAsync(active: true, locked: true);
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await ChangeAccountAsync(active: true, locked: false);
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await SetReadPermissionAsync(false);
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await SetReadPermissionAsync(true);
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Even the delegated staff with a real RevokeOperators ABP
            // grant may not revoke the immutable founding root membership.
            using (var payload = new StringContent(JsonSerializer.Serialize(
                new { buildingId = b, operatorUserId = root, expectedRevision = 1 }),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(grants + "/revoke", payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using (var payload = new StringContent(JsonSerializer.Serialize(
                new { buildingId = b, operatorUserId = operatorB, expectedRevision = 1 }),
                Encoding.UTF8, "application/json"))
            using (var response = await clientRoot.PostAsync(grants + "/revoke", payload))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            // Names are generated by this method and independently verified
            // above; never drop a caller-provided database or public schema.
            if (buildingsCreated)
                await ExecuteAsync(admin, $"""DROP DATABASE "{buildingName}" WITH (FORCE)""");
            if (identityCreated)
                await ExecuteAsync(admin, $"""DROP DATABASE "{idName}" WITH (FORCE)""");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [DependsOn(
        typeof(AbpAutofacModule),
        typeof(NgocTrangHouseManagementSystemEntityFrameworkCoreModule),
        typeof(BuildingManagementApplicationModule),
        typeof(BuildingManagementHttpApiModule))]
    public sealed class SliceAPostgreSqlHttpModule : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context)
        {
            PreConfigure<Microsoft.Extensions.DependencyInjection.IMvcBuilder>(mvc =>
                mvc.AddApplicationPartIfNotExists(
                    typeof(SliceAStagingOperatorGrantsController).Assembly));
            PreConfigure<AbpAspNetCoreMvcOptions>(options =>
                options.ConventionalControllers.Create(
                    typeof(BuildingManagementApplicationModule).Assembly,
                    settings => settings.RootPath = "building-management"));
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var config = context.Services.GetConfiguration();
            var idCs = config.GetConnectionString("Default");
            var buildingCs = config.GetConnectionString("BuildingManagement");
            if (idCs is null || buildingCs is null ||
                !idCs.Contains("bm_slicea_staging_identity_", StringComparison.Ordinal) ||
                !buildingCs.Contains("bm_slicea_staging_buildings_", StringComparison.Ordinal))
                throw new InvalidOperationException("Not isolated PostgreSQL staging.");

            Configure<AbpDbConnectionOptions>(o =>
            {
                o.ConnectionStrings.Default = idCs;
                o.ConnectionStrings[BuildingManagementDbProperties.ConnectionStringName] =
                    buildingCs;
            });
            Configure<AbpDbContextOptions>(o => o.UseNpgsql());
            context.Services.Configure<BuildingOwnerBootstrapSecurityOptions>(
                config.GetSection("BuildingManagement:Bootstrap"));

            context.Services.AddAuthentication("IsolatedPgHttp")
                .AddScheme<AuthenticationSchemeOptions, PgHttpAuthenticationHandler>(
                    "IsolatedPgHttp", _ => { });
            context.Services.AddAuthorization();
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingOperatorActiveAccountReader>(sp =>
                    new AbpIdentityBuildingOperatorAccountProbe(
                        sp.GetRequiredService<IIdentityUserRepository>(),
                        sp.GetRequiredService<IdentityUserManager>())));
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingOperatorGrantReader,
                    SliceAStagingGrantReader>());
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingOwnerBootstrapWriter,
                    SliceAStagingFirstOwnerWriter>());
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingOperatorGrantMutationWriter,
                    SliceAStagingGrantMutationWriter>());
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingScopedWriteBoundary,
                    SliceAStagingWriteBoundary>());

            Configure<AbpMvcLibsOptions>(o => o.CheckLibs = false);
            Configure<MvcOptions>(o => o.Conventions.Add(
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

    private sealed class PgHttpAuthenticationHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public PgHttpAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Slice-A-Test-Actor", out var raw) ||
                !Guid.TryParse(raw.ToString(), out var actor) || actor == Guid.Empty)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(AbpClaimTypes.UserId, actor.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, actor.ToString("D")),
                new Claim(ClaimTypes.Name, "Isolated PostgreSQL test only")
            }, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity),
                    Scheme.Name)));
        }
    }
}
