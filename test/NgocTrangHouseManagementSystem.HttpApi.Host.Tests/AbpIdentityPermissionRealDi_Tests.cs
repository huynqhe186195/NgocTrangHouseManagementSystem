using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Permissions;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
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
        IAbpApplication? application = null;
        try
        {
            application = await AbpApplicationFactory
                .CreateAsync<IdentityPermissionScratchModule>(options =>
                {
                    options.UseAutofac();
                    options.Services.AddLogging();
                    options.Services.Configure<AbpDbConnectionOptions>(o =>
                    {
                        o.ConnectionStrings.Default = cs;
                    });
                });
            await application.InitializeAsync();

            var services = application.ServiceProvider;
            var actor = Guid.NewGuid();
            using (var setupScope = services.CreateScope())
            {
                var db = setupScope.ServiceProvider
                    .GetRequiredService<NgocTrangHouseManagementSystemDbContext>();
                await db.Database.EnsureCreatedAsync();
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
            }

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
                await application.ShutdownAsync();
                application.Dispose();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(filename))
                File.Delete(filename);
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
            Configure<AbpDbContextOptions>(options => options.UseSqlite());
            Configure<AbpUnitOfWorkDefaultOptions>(options =>
                options.TransactionBehavior = UnitOfWorkTransactionBehavior.Enabled);
        }
    }
}
