using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.Security;
using Microsoft.Data.Sqlite;
using Volo.Abp.Authorization;
using System.Security.Claims;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Permissions;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Volo.Abp.Uow;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.Security;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// A real ABP DI container, real Identity and Permission Management EF
/// repositories and user-specific persisted grants. Isolated SQLite file:
/// NEVER the app's Npgsql connection, public DB, or runtime DI override.
/// </summary>
public sealed class AbpIdentityPermissionRealDi_Tests
{
    [Fact]
    public async Task IDREAL01_Actual_DI_Identity_UserManager_And_Permission_Store_Respect_Revocation()
    {
        var filename = Path.Combine(Path.GetTempPath(),
            "bm_idreal_" + Guid.NewGuid().ToString("N") + ".db");
        if (File.Exists(filename))
            throw new InvalidOperationException("Scratch file collision");
        var cs = "Data Source=" + filename;

        // Create the FULL application Identity+Permission model first. ABP
        // modules can create a partial schema during initialization; once
        // ANY SQLite table exists, EnsureCreated() stops creating the rest.
        await using (var schema = new NgocTrangHouseManagementSystemDbContext(
            new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
                .UseSqlite(cs).Options))
        {
            Assert.True(await schema.Database.EnsureCreatedAsync());
            await schema.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"AbpUsers\" LIMIT 1");
        }

        IAbpApplication? application = null;
        try
        {
            application = await AbpApplicationFactory
                .CreateAsync<IdentityPermissionScratchModule>(options =>
                {
                    options.UseAutofac();
                    options.Services.AddLogging();
                    options.Services.ReplaceConfiguration(
                        new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:Default"] = cs
                            }).Build());
                });
            ((IAbpApplicationWithInternalServiceProvider)application).Initialize();

            var services = application.ServiceProvider;
            var actor = Guid.NewGuid();
            using (var setupScope = services.CreateScope())
            {
                var db = setupScope.ServiceProvider
                    .GetRequiredService<NgocTrangHouseManagementSystemDbContext>();
                await db.Database.EnsureCreatedAsync();
                var source = db.Database.GetDbConnection().DataSource;
                Assert.True(!string.IsNullOrWhiteSpace(source),
                    "ABP runtime SQLite connection string was empty.");
                Assert.True(string.Equals(Path.GetFullPath(source),
                    Path.GetFullPath(filename), StringComparison.Ordinal),
                    "ABP Identity DbContext is using unexpected SQLite file: " +
                    source + " expected " + filename);
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"AbpUsers\" LIMIT 1");
                db.Users.Add(new IdentityUser(actor,
                    "isolated-" + actor.ToString("N"),
                    "isolated-" + actor.ToString("N") + "@example.test"));
                await db.SaveChangesAsync();
            }

            using (var scope = services.CreateScope())
            {
                var provider = scope.ServiceProvider;
                // A real ABP Identity EF repository and IdentityUserManager,
                // NOT a DispatchProxy or in-memory user lookup.
                var repository = provider.GetRequiredService<IIdentityUserRepository>();
                var manager = provider.GetRequiredService<IdentityUserManager>();
                var permissions = provider.GetRequiredService<IPermissionChecker>();
                var permissionManager = provider.GetRequiredService<IPermissionManager>();
                var accessor = provider.GetRequiredService<ICurrentPrincipalAccessor>();

                using var principal = accessor.Change(new ClaimsPrincipal(
                    new ClaimsIdentity(new[]
                    {
                        new Claim(AbpClaimTypes.UserId, actor.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString("D"))
                    }, "IsolatedSecurityTest")));
                using (var uow = provider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: false))
                {
                    var account = new AbpIdentityBuildingOperatorAccountProbe(
                        repository, manager);
                    Assert.True(await account.IsActiveAsync(actor));
                    Assert.False(await permissions.IsGrantedAsync(
                        BuildingManagementPermissions.Buildings.BootstrapOwner));
                    await uow.CompleteAsync();
                }

                using (var uow = provider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true))
                {
                    await permissionManager.SetForUserAsync(actor,
                        BuildingManagementPermissions.Buildings.BootstrapOwner,
                        true);
                    await permissionManager.SetForUserAsync(actor,
                        BuildingManagementPermissions.Buildings.Read,
                        true);
                    await uow.CompleteAsync();
                }
            }

            // New scope/UoW verifies persisted permission state through the
            // effective ABP permission checking pipeline.
            using (var scope = services.CreateScope())
            {
                var provider = scope.ServiceProvider;
                var accessor = provider.GetRequiredService<ICurrentPrincipalAccessor>();
                using var principal = accessor.Change(new ClaimsPrincipal(
                    new ClaimsIdentity(new[]
                    {
                        new Claim(AbpClaimTypes.UserId, actor.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString("D"))
                    }, "IsolatedSecurityTest")));
                using (var uow = provider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: false))
                {
                    Assert.True(await provider.GetRequiredService<IPermissionChecker>()
                        .IsGrantedAsync(BuildingManagementPermissions.Buildings.BootstrapOwner));
                    await uow.CompleteAsync();
                }

                var roots = new BuildingOwnerBootstrapSecurityOptions();
                var accountReader = new AbpIdentityBuildingOperatorAccountProbe(
                    provider.GetRequiredService<IIdentityUserRepository>(),
                    provider.GetRequiredService<IdentityUserManager>());
                var issuer = new AbpBuildingOwnerBootstrapPrincipalReader(
                    provider.GetRequiredService<Volo.Abp.Users.ICurrentUser>(),
                    provider.GetRequiredService<IPermissionChecker>(),
                    Microsoft.Extensions.Options.Options.Create(roots));

                // Even a genuine persisted ABP permission cannot grant
                // building creation to an unapproved root identity.
                Assert.Null(await issuer.GetAuthorizedIssuerAsync());
                roots.RootUserIds.Add(actor);
                Assert.Equal(actor, await issuer.GetAuthorizedIssuerAsync());
                Assert.True(await accountReader.IsActiveAsync(actor));

                // One COMPLETE positive/negative building authorization
                // flow with real ABP Identity and permission DI and actual
                // SQLite-persisted memberships (never runtime registration).
                await using var grantConnection =
                    new SqliteConnection("Data Source=:memory:");
                await grantConnection.OpenAsync();
                await using var grantDb = new IsolatedMembershipDb(
                    new DbContextOptionsBuilder<IsolatedMembershipDb>()
                        .UseSqlite(grantConnection).Options);
                await grantDb.Database.EnsureCreatedAsync();
                var buildingA = Guid.NewGuid();
                var buildingB = Guid.NewGuid();
                grantDb.Buildings.AddRange(
                    new Building(buildingA, "A", "Test-only A"),
                    new Building(buildingB, "B", "Test-only B"));
                grantDb.Grants.Add(BuildingOperatorAccessGrant.Issue(
                    Guid.NewGuid(), actor, buildingA, actor,
                    DateTime.UtcNow.AddMinutes(-1), null));
                await grantDb.SaveChangesAsync();

                var policy = new BuildingCrudAccessPolicy(
                    new AbpBuildingBusinessPrincipalReader(
                        provider.GetRequiredService<Volo.Abp.Users.ICurrentUser>(),
                        provider.GetRequiredService<IPermissionChecker>()),
                    accountReader,
                    new MembershipReader(grantDb),
                    new BuildingOnlyScope(grantDb));

                await policy.EnsureBuildingAsync(
                    buildingA, BuildingBusinessCapability.ReadBuilding);
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    policy.EnsureBuildingAsync(
                        buildingB, BuildingBusinessCapability.ReadBuilding));
                Assert.Equal(new[] { buildingA },
                    await policy.GetAuthorizedBuildingIdsAsync(
                        BuildingBusinessCapability.ReadBuilding));

                var liveGrant = await grantDb.Grants.SingleAsync();
                liveGrant.Revoke(Guid.NewGuid(), DateTime.UtcNow);
                await grantDb.SaveChangesAsync();
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    policy.EnsureBuildingAsync(
                        buildingA, BuildingBusinessCapability.ReadBuilding));
            }

            // A fresh ABP Identity repository/UserManager read must react
            // immediately when the persisted user is disabled or locked.
            async Task<bool> AccountActiveAsync()
            {
                using var probeScope = services.CreateScope();
                var provider = probeScope.ServiceProvider;
                using var uow = provider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: false);
                var probe = new AbpIdentityBuildingOperatorAccountProbe(
                    provider.GetRequiredService<IIdentityUserRepository>(),
                    provider.GetRequiredService<IdentityUserManager>());
                var active = await probe.IsActiveAsync(actor);
                await uow.CompleteAsync();
                return active;
            }

            Assert.True(await AccountActiveAsync());
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<
                    NgocTrangHouseManagementSystemDbContext>();
                var stored = await db.Users.SingleAsync(x => x.Id == actor);
                stored.SetIsActive(false);
                await db.SaveChangesAsync();
            }
            Assert.False(await AccountActiveAsync());
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<
                    NgocTrangHouseManagementSystemDbContext>();
                var stored = await db.Users.SingleAsync(x => x.Id == actor);
                stored.SetIsActive(true);
                db.Entry(stored).Property(x => x.LockoutEnabled).CurrentValue = true;
                db.Entry(stored).Property(x => x.LockoutEnd).CurrentValue =
                    DateTimeOffset.UtcNow.AddMinutes(10);
                await db.SaveChangesAsync();
            }
            Assert.False(await AccountActiveAsync());
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<
                    NgocTrangHouseManagementSystemDbContext>();
                var stored = await db.Users.SingleAsync(x => x.Id == actor);
                db.Entry(stored).Property(x => x.LockoutEnd).CurrentValue =
                    DateTimeOffset.UtcNow.AddMinutes(-10);
                await db.SaveChangesAsync();
            }
            Assert.True(await AccountActiveAsync());

            using (var scope = services.CreateScope())
            {
                var provider = scope.ServiceProvider;
                var perm = provider.GetRequiredService<IPermissionManager>();
                using var uow = provider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await perm.SetForUserAsync(actor,
                    BuildingManagementPermissions.Buildings.BootstrapOwner, false);
                await uow.CompleteAsync();
            }

            using (var scope = services.CreateScope())
            {
                var provider = scope.ServiceProvider;
                var accessor = provider.GetRequiredService<ICurrentPrincipalAccessor>();
                using var principal = accessor.Change(new ClaimsPrincipal(
                    new ClaimsIdentity(new[]
                    {
                        new Claim(AbpClaimTypes.UserId, actor.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString("D"))
                    }, "IsolatedSecurityTest")));
                using var uow = provider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: false);
                Assert.False(await provider.GetRequiredService<IPermissionChecker>()
                    .IsGrantedAsync(BuildingManagementPermissions.Buildings.BootstrapOwner));
                await uow.CompleteAsync();
            }
        }
        finally
        {
            if (application is not null)
            {
                ((IAbpApplicationWithInternalServiceProvider)application).Shutdown();
                application.Dispose();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(filename))
                File.Delete(filename);
        }
    }

    private sealed class IsolatedMembershipDb : DbContext
    {
        public IsolatedMembershipDb(
            DbContextOptions<IsolatedMembershipDb> options) : base(options) { }
        public DbSet<Building> Buildings => Set<Building>();
        public DbSet<BuildingOperatorAccessGrant> Grants =>
            Set<BuildingOperatorAccessGrant>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.ApplyConfiguration(new BuildingConfiguration());
            model.ApplyConfiguration(
                new BuildingOperatorAccessGrantDesignConfiguration());
        }
    }

    private sealed class MembershipReader : IBuildingOperatorGrantReader
    {
        private readonly EfBuildingOperatorGrantCandidateReader _reader;
        public MembershipReader(IsolatedMembershipDb db) =>
            _reader = new EfBuildingOperatorGrantCandidateReader(
                db, TimeProvider.System);
        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid id)
            => _reader.GetActiveBuildingIdsAsync(id);
    }

    private sealed class BuildingOnlyScope : IBuildingBusinessResourceScopeReader
    {
        private readonly IsolatedMembershipDb _db;
        public BuildingOnlyScope(IsolatedMembershipDb db) => _db = db;
        public async Task<BuildingBusinessResolvedScope?> ReadAsync(
            BuildingBusinessResourceKind kind, Guid id)
        {
            if (kind != BuildingBusinessResourceKind.Building ||
                id == Guid.Empty ||
                !await _db.Buildings.AsNoTracking()
                    .AnyAsync(b => b.Id == id && !b.IsDeleted))
                return null;
            return new BuildingBusinessResolvedScope(id, id);
        }
    }

    [DependsOn(
        typeof(AbpAutofacModule),
        typeof(AbpEntityFrameworkCoreSqliteModule),
        typeof(NgocTrangHouseManagementSystemEntityFrameworkCoreModule),
        typeof(BuildingManagementApplicationModule))]
    public sealed class IdentityPermissionScratchModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            // Configure the named ABP database AFTER dependent modules have
            // run. Do not rely solely on pre-module IServiceCollection
            // Configure<T>, which can be overwritten by ABP configuration.
            var cs = context.Services.GetConfiguration()["ConnectionStrings:Default"];
            if (string.IsNullOrWhiteSpace(cs) ||
                !cs.Contains("bm_idreal_", StringComparison.Ordinal) ||
                !cs.EndsWith(".db", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe Identity test connection.");
            Configure<AbpDbConnectionOptions>(o => o.ConnectionStrings.Default = cs);
            Configure<AbpDbContextOptions>(options => options.UseSqlite());
            Configure<AbpUnitOfWorkDefaultOptions>(options =>
                options.TransactionBehavior = UnitOfWorkTransactionBehavior.Enabled);
        }
    }
}
