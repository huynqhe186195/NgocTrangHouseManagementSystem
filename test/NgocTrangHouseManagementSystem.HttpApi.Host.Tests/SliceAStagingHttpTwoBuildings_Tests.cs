using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.Security;
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
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Libs;
using Volo.Abp.Autofac;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// Real HTTP conventional ABP BuildingAppService controllers + real
/// Identity/Permission EF DI. Only this test module substitutes candidate
/// membership+active-account readers, with three disposable SQLite files.
/// Production keeps its DenyAll registrations, no new public endpoints.
/// </summary>
public sealed class SliceAStagingHttpTwoBuildings_Tests
{
    [Fact]
    public async Task STAGEHTTP01_A_And_B_Only_Read_Their_Buildings_And_Revocation_Denies()
    {
        var prefix = Path.Combine(Path.GetTempPath(),
            "bm_http_" + Guid.NewGuid().ToString("N"));
        var idPath = prefix + "_identity.db";
        var buildingPath = prefix + "_building.db";
        var memberPath = prefix + "_membership.db";
        var idCs = "Data Source=" + idPath;
        var buildingCs = "Data Source=" + buildingPath;
        var memberCs = "Data Source=" + memberPath;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();

        await using (var identity = new NgocTrangHouseManagementSystemDbContext(
            new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
                .UseSqlite(idCs).Options))
            Assert.True(await identity.Database.EnsureCreatedAsync());

        await using (var buildings = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseSqlite(buildingCs).Options))
        {
            Assert.True(await buildings.Database.EnsureCreatedAsync());
            await SeedBuildingSqlAsync(buildings, a, "Staging Building A");
            await SeedBuildingSqlAsync(buildings, b, "Staging Building B");
        }

        await using (var memberships = NewMembershipDb(memberCs))
        {
            Assert.True(await memberships.Database.EnsureCreatedAsync());
            memberships.Buildings.AddRange(
                new Building(a, "Staging Building A", "Only test A"),
                new Building(b, "Staging Building B", "Only test B"));
            memberships.Grants.AddRange(
                BuildingOperatorAccessGrant.Issue(
                    Guid.NewGuid(), actorA, a, actorA,
                    DateTime.UtcNow.AddMinutes(-1), null),
                BuildingOperatorAccessGrant.Issue(
                    Guid.NewGuid(), actorB, b, actorB,
                    DateTime.UtcNow.AddMinutes(-1), null));
            await memberships.SaveChangesAsync();
        }

        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Host.UseAutofac();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = idCs,
                    ["ConnectionStrings:BuildingManagement"] = buildingCs,
                    ["ConnectionStrings:SliceAMembership"] = memberCs
                }).Build();
            builder.Configuration.AddConfiguration(config);
            builder.Services.ReplaceConfiguration(config);

            await builder.AddApplicationAsync<SliceAStagingHttpModule>();
            app = builder.Build();
            await app.InitializeApplicationAsync();
            await app.StartAsync();

            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                Assert.IsType<AbpIdentityBuildingOperatorAccountProbe>(
                    sp.GetRequiredService<IBuildingOperatorActiveAccountReader>());
                Assert.IsType<StagingMembershipReader>(
                    sp.GetRequiredService<IBuildingOperatorGrantReader>());
                var db = sp.GetRequiredService<NgocTrangHouseManagementSystemDbContext>();
                db.Users.AddRange(
                    NewIdentityUser(actorA), NewIdentityUser(actorB));
                await db.SaveChangesAsync();

                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var permissions = sp.GetRequiredService<IPermissionManager>();
                await permissions.SetForUserAsync(actorA,
                    BuildingManagement.Permissions.BuildingManagementPermissions.Buildings.Read,
                    true);
                await permissions.SetForUserAsync(actorB,
                    BuildingManagement.Permissions.BuildingManagementPermissions.Buildings.Read,
                    true);
                await permissions.SetForUserAsync(actorA,
                    BuildingManagement.Permissions.BuildingManagementPermissions.Buildings.Manage,
                    true);
                await uow.CompleteAsync();
            }

            var actions = app.Services
                .GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
                .ToArray();
            var list = Assert.Single(actions, x =>
                x.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
                x.MethodInfo.Name == nameof(BuildingAppService.GetListAsync));
            var get = Assert.Single(actions, x =>
                x.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
                x.MethodInfo.Name == nameof(BuildingAppService.GetAsync));
            var listPath = "/" + Assert.IsType<string>(
                list.AttributeRouteInfo?.Template).TrimStart('/');
            var getPath = Assert.IsType<string>(get.AttributeRouteInfo?.Template);
            Assert.Contains("{", getPath);
            string Url(Guid id) => "/" + Regex.Replace(
                getPath, @"\{[^}]+\}", id.ToString("D")).TrimStart('/');

            using var clientA = app.GetTestClient();
            using var clientB = app.GetTestClient();
            clientA.DefaultRequestHeaders.Add("X-Isolated-Security-User", actorA.ToString("D"));
            clientB.DefaultRequestHeaders.Add("X-Isolated-Security-User", actorB.ToString("D"));

            using (var response = await clientA.GetAsync(listPath))
            {
                var payload = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    $"A list unexpected status {response.StatusCode}: {payload}");
                Assert.Contains(a.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(b.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
            }
            using (var response = await clientB.GetAsync(listPath))
            {
                var payload = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    $"B list unexpected status {response.StatusCode}: {payload}");
                Assert.Contains(b.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(a.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
            }
            using (var response = await clientA.GetAsync(Url(a)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(Url(b)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientA.GetAsync(Url(b)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(Url(a)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Positive HTTP mutation is staging-only. The production CRUD
            // write/revocation race remains separately gated.
            using (var body = new StringContent(
                "{\"name\":\"A updated\",\"address\":\"Staging updated\"}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientA.PutAsync(Url(a), body))
            {
                var payload = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    $"A PUT {response.StatusCode}: {payload}");
                Assert.Contains("A updated", payload);
            }
            using (var body = new StringContent(
                "{\"name\":\"No access\",\"address\":\"Denied\"}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientA.PutAsync(Url(b), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent(
                "{\"name\":\"No manage\",\"address\":\"Denied\"}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(Url(b), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientA.GetAsync(Url(a)))
            {
                var payload = await response.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("A updated", payload);
            }

            await using (var memberships = NewMembershipDb(memberCs))
            {
                var grant = await memberships.Grants.SingleAsync(x =>
                    x.OperatorUserId == actorA);
                grant.Revoke(actorB, DateTime.UtcNow);
                await memberships.SaveChangesAsync();
            }
            using (var response = await clientA.GetAsync(listPath))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(listPath))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { idPath, buildingPath, memberPath })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    private static async Task SeedBuildingSqlAsync(
        BuildingManagementDbContext db, Guid id, string name)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Buildings"
            ("Id","Name","Address","ExtraProperties","ConcurrencyStamp",
             "CreationTime","IsDeleted")
            VALUES ({id}, {name}, {"Staging only"}, {"{}"},
                    {Guid.NewGuid().ToString("N")}, CURRENT_TIMESTAMP, FALSE)
            """);
    }

    private static IdentityUser NewIdentityUser(Guid id)
        => new(id, "staging-" + id.ToString("N"),
            "staging-" + id.ToString("N") + "@example.test");

    private static StagingMembershipDb NewMembershipDb(string cs)
        => new(new DbContextOptionsBuilder<StagingMembershipDb>()
            .UseSqlite(cs).Options);

    private sealed class StagingMembershipDb : DbContext
    {
        public StagingMembershipDb(DbContextOptions<StagingMembershipDb> opts)
            : base(opts) { }

        public DbSet<Building> Buildings => Set<Building>();
        public DbSet<BuildingOperatorAccessGrant> Grants
            => Set<BuildingOperatorAccessGrant>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.ApplyConfiguration(new BuildingConfiguration());
            builder.ApplyConfiguration(
                new BuildingOperatorAccessGrantDesignConfiguration());
        }
    }

    public sealed class StagingMembershipReader : IBuildingOperatorGrantReader
    {
        private readonly string _connection;
        public StagingMembershipReader(IConfiguration configuration)
        {
            _connection = configuration["ConnectionStrings:SliceAMembership"]
                ?? throw new InvalidOperationException("Missing test-only membership.");
            if (!_connection.Contains("bm_http_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe staging membership DB.");
        }

        public async Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid id)
        {
            await using var db = NewMembershipDb(_connection);
            return await new EfBuildingOperatorGrantCandidateReader(
                db, TimeProvider.System).GetActiveBuildingIdsAsync(id);
        }
    }

    [DependsOn(
        typeof(AbpAutofacModule),
        typeof(AbpEntityFrameworkCoreSqliteModule),
        typeof(NgocTrangHouseManagementSystemEntityFrameworkCoreModule),
        typeof(BuildingManagementApplicationModule),
        typeof(BuildingManagementHttpApiModule))]
    public sealed class SliceAStagingHttpModule : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context)
        {
            PreConfigure<AbpAspNetCoreMvcOptions>(options =>
                options.ConventionalControllers.Create(
                    typeof(BuildingManagementApplicationModule).Assembly,
                    settings => settings.RootPath = "building-management"));
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var config = context.Services.GetConfiguration();
            var idCs = config["ConnectionStrings:Default"];
            var buildingCs = config["ConnectionStrings:BuildingManagement"];
            var memberCs = config["ConnectionStrings:SliceAMembership"];
            foreach (var cs in new[] { idCs, buildingCs, memberCs })
                if (string.IsNullOrWhiteSpace(cs) ||
                    !cs.Contains("bm_http_", StringComparison.Ordinal) ||
                    !cs.EndsWith(".db", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unsafe staging test connection.");
            Configure<AbpDbConnectionOptions>(o =>
            {
                o.ConnectionStrings.Default = idCs!;
                o.ConnectionStrings[BuildingManagementDbProperties.ConnectionStringName] =
                    buildingCs!;
            });
            Configure<AbpDbContextOptions>(o => o.UseSqlite());

            context.Services.AddAuthentication("HttpScratch")
                .AddScheme<AuthenticationSchemeOptions, ScratchAuthenticationHandler>(
                    "HttpScratch", _ => { });
            context.Services.AddAuthorization();
            // These overrides live ONLY inside the isolated test module.
            // The production host still resolves DenyAll for both providers.
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingOperatorActiveAccountReader>(sp =>
                    new AbpIdentityBuildingOperatorAccountProbe(
                        sp.GetRequiredService<IIdentityUserRepository>(),
                        sp.GetRequiredService<IdentityUserManager>())));
            context.Services.Replace(
                ServiceDescriptor.Transient<IBuildingOperatorGrantReader,
                    StagingMembershipReader>());

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

    private sealed class ScratchAuthenticationHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public ScratchAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Isolated-Security-User", out var raw) ||
                !Guid.TryParse(raw.ToString(), out var id) || id == Guid.Empty)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(AbpClaimTypes.UserId, id.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, id.ToString("D")),
                new Claim(ClaimTypes.Name, "Staging test only")
            }, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity),
                    Scheme.Name)));
        }
    }
}
