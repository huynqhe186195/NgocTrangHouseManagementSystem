using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
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
using Microsoft.EntityFrameworkCore.Storage;
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
            var cosigner = Guid.NewGuid();
            var disabledFounder = Guid.NewGuid();

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

            // An independent LOGIN verifies the actual ABP Identity schema,
            // not a mock table. Only these generated disposable databases.
            await AttestIdentityRuntimeLoginAsync(admin, idCs);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = idCs,
                    ["ConnectionStrings:BuildingManagement"] = buildingCs,
                    ["BuildingManagement:Bootstrap:RootUserIds:0"] =
                        root.ToString("D"),
                    ["BuildingManagement:OwnerRecovery:Enabled"] = "true",
                    ["BuildingManagement:OwnerRecovery:CoSignerUserIds:0"] =
                        cosigner.ToString("D"),
                    ["BuildingManagement:SliceAStaging:Enabled"] = "true",
                    ["BuildingManagement:SliceAStaging:Activation"] =
                        "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES"
                }).Build();
            // Capture test-server exceptions only in the isolated fixture.
            // ABP intentionally returns generic 500 bodies; preserve this
            // confidentiality at the HTTP boundary while making failures
            // diagnosable from the local xUnit assertion output.
            var serverErrors = new StagingServerErrorCollector();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Staging
            });
            builder.Logging.AddProvider(serverErrors);
            builder.WebHost.UseTestServer();
            builder.Host.UseAutofac();
            builder.Configuration.AddConfiguration(config);
            builder.Services.ReplaceConfiguration(config);
            // Only this isolated fixture may move the 2H clock to a month-end;
            // the real adapter always defaults to TimeProvider.System.
            var transferClock = new MutableUtcTimeProvider();
            builder.Services.AddSingleton<TimeProvider>(transferClock);
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
                        "operator-" + operatorB.ToString("N") + "@example.test"),
                    new IdentityUser(cosigner, "cosigner-" + cosigner.ToString("N"),
                        "cosigner-" + cosigner.ToString("N") + "@example.test"),
                    new IdentityUser(disabledFounder, "founder-" + disabledFounder.ToString("N"),
                        "founder-" + disabledFounder.ToString("N") + "@example.test"));
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
                await permissions.SetForUserAsync(cosigner,
                    BuildingManagementPermissions.Buildings.CoSignOwnerRecovery, true);
                foreach (var id in new[] { root, operatorB })
                {
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Contracts.Read, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Contracts.Manage, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Floors.Read, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Floors.Manage, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Rooms.Read, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Rooms.Manage, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Utilities.Read, true);
                    await permissions.SetForUserAsync(id,
                        BuildingManagementPermissions.Utilities.Manage, true);
                }
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

            // Bootstrap also must hold the Identity BootstrapOwner grant
            // through Building commit. Otherwise revocation could commit
            // before a new Building + self-issued founder grant is visible.
            Task<int>? revokeBootstrap = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, root.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, root.ToString("D"))
                    }, "CommitBoundBootstrap"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var created = await sp.GetRequiredService<IBuildingOwnerBootstrapWriter>()
                        .CreateWithOwnerAtomicallyAsync(
                            root, "Bootstrap under revocation", "Scratch", null);
                    Assert.NotEqual(Guid.Empty, created.Id);
                    revokeBootstrap = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var command = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U'
                               AND "ProviderKey" = @root
                               AND "Name" = @permission
                            """, connection);
                        command.Parameters.AddWithValue("root", root.ToString("D"));
                        command.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Buildings.BootstrapOwner);
                        return await command.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeBootstrap.IsCompleted,
                        "BootstrapOwner revocation must wait for Building commit.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeBootstrap);
            Assert.Equal(1, await revokeBootstrap!.WaitAsync(TimeSpan.FromSeconds(15)));

            // Even a direct, stale internal writer invocation must now
            // fail closed after permission revocation, before DB mutation.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(AbpClaimTypes.UserId, root.ToString("D")) },
                    "StaleBootstrapGrant"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(
                        () => sp.GetRequiredService<IBuildingOwnerBootstrapWriter>()
                            .CreateWithOwnerAtomicallyAsync(
                                root, "Must not exist", "Scratch", null));
                }
            }

            // Restore scratch fixture permission for the remaining recovery,
            // delegation and CRUD scenarios. No real DB is modified.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    root, BuildingManagementPermissions.Buildings.BootstrapOwner, true);
                await uow.CompleteAsync();
            }
            using (var response = await clientB.GetAsync(GetAt(a)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(listPath))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Concurrent disabling of the sole root account must wait for
            // the first-owner self-issued grant's Building commit.
            Task<int>? disableRoot = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, root.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, root.ToString("D"))
                    }, "RootAccountCommitRace"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var created = await sp.GetRequiredService<IBuildingOwnerBootstrapWriter>()
                        .CreateWithOwnerAtomicallyAsync(
                            root, "Account lock before founder commit", "Scratch", null);
                    Assert.NotEqual(Guid.Empty, created.Id);
                    disableRoot = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var command = new NpgsqlCommand(
                            """UPDATE "AbpUsers" SET "IsActive" = FALSE WHERE "Id" = @id""",
                            connection);
                        command.Parameters.AddWithValue("id", root);
                        return await command.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(disableRoot.IsCompleted,
                        "Root account disable must wait for founder commit.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(disableRoot);
            Assert.Equal(1, await disableRoot!.WaitAsync(TimeSpan.FromSeconds(15)));
            using (var payload = new StringContent(
                """{"name":"Disabled root cannot create","address":"Scratch"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientRoot.PostAsync(createPath, payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await using (var conn = new NpgsqlConnection(idCs))
            {
                await conn.OpenAsync();
                await using var restore = new NpgsqlCommand(
                    """UPDATE "AbpUsers" SET "IsActive" = TRUE WHERE "Id" = @id""",
                    conn);
                restore.Parameters.AddWithValue("id", root);
                Assert.Equal(1, await restore.ExecuteNonQueryAsync());
            }

            // Real durable two-principal signoff against the extra,
            // completely isolated recovery building. No HTTP recovery route.
            var recoveryBuilding = Guid.NewGuid();
            var evidence = Guid.NewGuid();
            await using (var identitySeed = new NpgsqlConnection(idCs))
            {
                await identitySeed.OpenAsync();
                await using var inactive = new NpgsqlCommand(
                    """UPDATE "AbpUsers" SET "IsActive" = FALSE WHERE "Id" = @id""",
                    identitySeed);
                inactive.Parameters.AddWithValue("id", disabledFounder);
                Assert.Equal(1, await inactive.ExecuteNonQueryAsync());
            }
            await using (var seed = new NpgsqlConnection(buildingCs))
            {
                await seed.OpenAsync();
                await using (var buildingInsert = new NpgsqlCommand(
                    """
                    INSERT INTO "Buildings"
                        ("Id","Name","Address","ExtraProperties",
                         "ConcurrencyStamp","CreationTime","IsDeleted")
                    VALUES (@id, 'Recovery-only Building', 'Scratch',
                            '{}', @stamp, CURRENT_TIMESTAMP, FALSE)
                    """, seed))
                {
                    buildingInsert.Parameters.AddWithValue("id", recoveryBuilding);
                    buildingInsert.Parameters.AddWithValue("stamp",
                        Guid.NewGuid().ToString("N"));
                    Assert.Equal(1, await buildingInsert.ExecuteNonQueryAsync());
                }
                await using (var grantInsert = new NpgsqlCommand(
                    """
                    INSERT INTO "BuildingManagementOperatorBuildingGrants"
                        ("Id","OperatorUserId","BuildingId","GrantedByUserId",
                         "GrantedAtUtc","RevisionNumber","ExtraProperties","ConcurrencyStamp")
                    VALUES (@id,@founder,@building,@founder,@at,1,'{}',@stamp)
                    """, seed))
                {
                    grantInsert.Parameters.AddWithValue("id", Guid.NewGuid());
                    grantInsert.Parameters.AddWithValue("founder", disabledFounder);
                    grantInsert.Parameters.AddWithValue("building", recoveryBuilding);
                    grantInsert.Parameters.AddWithValue("at", DateTime.UtcNow.AddMinutes(-1));
                    grantInsert.Parameters.AddWithValue("stamp", Guid.NewGuid().ToString("N"));
                    Assert.Equal(1, await grantInsert.ExecuteNonQueryAsync());
                }
            }

            async Task SignAsync(Guid actor)
            {
                using var scope = app.Services.CreateScope();
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(AbpClaimTypes.UserId, actor.ToString("D")) },
                    "RecoverySigner"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    await sp.GetRequiredService<SliceAStagingOwnerRecoveryEvidenceStore>()
                        .RecordSignoffAsync(evidence, recoveryBuilding,
                            disabledFounder, operatorB, DateTime.UtcNow.AddHours(1));
                    await uow.CompleteAsync();
                }
            }
            await SignAsync(root);
            using (var incomplete = app.Services.CreateScope())
            {
                using var uow = incomplete.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                Assert.Null(await incomplete.ServiceProvider
                    .GetRequiredService<IBuildingOwnerRecoveryApprovalReader>()
                    .ReadApprovedAsync(recoveryBuilding, operatorB));
            }
            // The co-signer's permission cannot be revoked before the
            // independently authenticated durable sign-off commits.
            Task<int>? revokeCoSign = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, cosigner.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, cosigner.ToString("D"))
                    }, "CommitBoundRecoverySignoff"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    await sp.GetRequiredService<SliceAStagingOwnerRecoveryEvidenceStore>()
                        .RecordSignoffAsync(evidence, recoveryBuilding,
                            disabledFounder, operatorB, DateTime.UtcNow.AddHours(1));
                    revokeCoSign = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var command = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U'
                               AND "ProviderKey" = @signer
                               AND "Name" = @permission
                            """, connection);
                        command.Parameters.AddWithValue("signer", cosigner.ToString("D"));
                        command.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Buildings.CoSignOwnerRecovery);
                        return await command.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeCoSign.IsCompleted,
                        "Co-signer revocation must wait for evidence commit.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeCoSign);
            Assert.Equal(1, await revokeCoSign!.WaitAsync(TimeSpan.FromSeconds(15)));
            // Restore isolated evidence fixture privilege for subsequent
            // checks; this is never applied to any deployed account.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    cosigner,
                    BuildingManagementPermissions.Buildings.CoSignOwnerRecovery,
                    true);
                await uow.CompleteAsync();
            }
            Task<int>? revokeRecoveryCoSign = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(AbpClaimTypes.UserId, root.ToString("D")) },
                    "RecoveryRoot"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var approval = await sp
                        .GetRequiredService<IBuildingOwnerRecoveryApprovalReader>()
                        .ReadApprovedAsync(recoveryBuilding, operatorB);
                    Assert.NotNull(approval);
                    Assert.Equal(evidence, approval.EvidenceId);
                    await sp.GetRequiredService<BuildingOwnerRecoveryCoordinator>()
                        .RecoverAsync(recoveryBuilding, operatorB);

                    // The recovery grant and approval consumption must be
                    // committed BEFORE a co-signer permission revocation.
                    revokeRecoveryCoSign = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var cmd = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U'
                               AND "ProviderKey" = @signer
                               AND "Name" = @permission
                            """, connection);
                        cmd.Parameters.AddWithValue("signer", cosigner.ToString("D"));
                        cmd.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Buildings.CoSignOwnerRecovery);
                        return await cmd.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeRecoveryCoSign.IsCompleted,
                        "Co-signer permission revocation must wait for recovery commit.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeRecoveryCoSign);
            Assert.Equal(1, await revokeRecoveryCoSign!
                .WaitAsync(TimeSpan.FromSeconds(15)));
            // Durable proof was consumed, not reused; founder untouched.
            using (var scope = app.Services.CreateScope())
            {
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                Assert.Null(await scope.ServiceProvider
                    .GetRequiredService<IBuildingOwnerRecoveryApprovalReader>()
                    .ReadApprovedAsync(recoveryBuilding, operatorB));
            }
            using (var response = await clientB.GetAsync(GetAt(recoveryBuilding)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientRoot.GetAsync(GetAt(recoveryBuilding)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            const string grants = "/api/slice-a-staging/operator-grants";

            // The owner may delegate, but cannot revoke their self-issued
            // founding grant or accidentally orphan the building.
            using (var payload = new StringContent(JsonSerializer.Serialize(
                new { buildingId = a, operatorUserId = root, expectedRevision = 1 }),
                Encoding.UTF8, "application/json"))
            using (var response = await clientRoot.PostAsync(grants + "/revoke", payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.GetAsync(GetAt(a)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var payload = new StringContent(JsonSerializer.Serialize(
                new { buildingId = b, operatorUserId = operatorB }),
                Encoding.UTF8, "application/json"))
            using (var response = await clientRoot.PostAsync(grants, payload))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Delegation itself (not only Room/Floor CRUD) now holds the
            // real ABP AssignOperators grant lock through Building commit.
            // Revoking permission from the independent Identity DB must wait.
            Task<int>? revokeAssign = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, root.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, root.ToString("D"))
                    }, "CommitBoundAssign"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var writer = sp.GetRequiredService<
                        IBuildingOperatorGrantMutationWriter>();
                    var newGrant = await writer.AssignAtomicallyAsync(
                        root, cosigner, a, null);
                    Assert.NotEqual(Guid.Empty, newGrant);
                    revokeAssign = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var cmd = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U'
                               AND "ProviderKey" = @root
                               AND "Name" = @permission
                            """, connection);
                        cmd.Parameters.AddWithValue("root", root.ToString("D"));
                        cmd.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Buildings.AssignOperators);
                        return await cmd.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeAssign.IsCompleted,
                        "AssignOperators revocation must wait for grant commit.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeAssign);
            Assert.Equal(1, await revokeAssign!.WaitAsync(TimeSpan.FromSeconds(15)));
            // Stale ABP authorization cannot bypass a missing persisted
            // AssignOperators grant even through the internal writer.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(AbpClaimTypes.UserId, root.ToString("D")) },
                    "StaleAssignGrant"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(
                        () => sp.GetRequiredService<IBuildingOperatorGrantMutationWriter>()
                            .AssignAtomicallyAsync(root, cosigner, b, null));
                }
            }
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    root, BuildingManagementPermissions.Buildings.AssignOperators, true);
                await uow.CompleteAsync();
            }

            // A history reader cannot leak membership metadata after
            // Identity grants are revoked. Even read-only history must
            // hold a durable Identity permission through Building UoW end.
            Task<int>? revokeHistoryAssign = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, root.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, root.ToString("D"))
                    }, "CommitBoundGrantHistory"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var records = await sp.GetRequiredService<SliceAStagingOperatorGrantQuery>()
                        .GetAsync(b, 0, 10);
                    Assert.Contains(records, item => item.OperatorUserId == operatorB);
                    revokeHistoryAssign = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var command = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U'
                               AND "ProviderKey" = @root
                               AND "Name" = @permission
                            """, connection);
                        command.Parameters.AddWithValue("root", root.ToString("D"));
                        command.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Buildings.AssignOperators);
                        return await command.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeHistoryAssign.IsCompleted,
                        "Identity permission revocation must wait for grant-history UoW.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeHistoryAssign);
            Assert.Equal(1, await revokeHistoryAssign!
                .WaitAsync(TimeSpan.FromSeconds(15)));

            // RevokeOperators alone is sufficient, but neither permission
            // means DENY even if ABP caches a stale privileged result.
            using (var response = await clientRoot.GetAsync(
                grants + "/" + b.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using (var connection = new NpgsqlConnection(idCs))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    DELETE FROM "AbpPermissionGrants"
                     WHERE "ProviderName" = 'U'
                       AND "ProviderKey" = @root
                       AND "Name" = @permission
                    """, connection);
                command.Parameters.AddWithValue("root", root.ToString("D"));
                command.Parameters.AddWithValue("permission",
                    BuildingManagementPermissions.Buildings.RevokeOperators);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            using (var response = await clientRoot.GetAsync(
                grants + "/" + b.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Restore ephemeral fixture grants for subsequent delegation and
            // CRUD tests; no deployed Identity permissions are touched.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var permissions = sp.GetRequiredService<IPermissionManager>();
                await permissions.SetForUserAsync(root,
                    BuildingManagementPermissions.Buildings.AssignOperators, true);
                await permissions.SetForUserAsync(root,
                    BuildingManagementPermissions.Buildings.RevokeOperators, true);
                await uow.CompleteAsync();
            }

            // Membership in B plus a valid, separate AssignOperators
            // permission does NOT authorize delegating access to A.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Buildings.AssignOperators,
                    true);
                await uow.CompleteAsync();
            }
            using (var payload = new StringContent(JsonSerializer.Serialize(
                new { buildingId = a, operatorUserId = cosigner }),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(grants, payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Buildings.AssignOperators,
                    false);
                await uow.CompleteAsync();
            }

            // HTTP grant history is per-building and uses a separate
            // AssignOperators/RevokeOperators privilege, not Buildings.Read.
            using (var response = await clientRoot.GetAsync(
                grants + "/" + b.ToString("D") + "?skipCount=0&maxResultCount=20"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var items = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var granted = items.RootElement.EnumerateArray().ToArray();
                Assert.Contains(granted, x =>
                    x.GetProperty("operatorUserId").GetGuid() == operatorB &&
                    x.GetProperty("revisionNumber").GetInt32() == 1);
                Assert.DoesNotContain(granted, x =>
                    x.GetProperty("buildingId").GetGuid() != b);
            }
            using (var response = await clientB.GetAsync(
                grants + "/" + a.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                grants + "/" + b.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientRoot.GetAsync(
                grants + "/" + b.ToString("D") + "?maxResultCount=101"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

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
            // Exercise actual PostgreSQL transactional write fences for
            // Floor/Room, not the SQLite-only test boundary. Root legitimately
            // sees both buildings; B must see ONLY its delegated Building B.
            string ChildRoute(Type service, string method) => "/" + Assert.IsType<string>(
                Assert.Single(actions, action =>
                    action.ControllerTypeInfo.AsType() == service &&
                    action.MethodInfo.Name == method).AttributeRouteInfo?.Template)
                .TrimStart('/');
            string ChildId(string route, Guid id) => Regex.Replace(
                route, @"\{[^}]+\}", id.ToString("D"));
            async Task<Guid> CreateChildAsync(HttpClient client, string path, object request)
            {
                using var payload = new StringContent(JsonSerializer.Serialize(request),
                    Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(path, payload);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
                    $"Postgres child create {response.StatusCode}: {body}");
                using var document = JsonDocument.Parse(body);
                return document.RootElement.GetProperty("id").GetGuid();
            }
            var floorCreate = ChildRoute(typeof(BuildingManagement.Floors.FloorAppService),
                "CreateAsync");
            var floorGet = ChildRoute(typeof(BuildingManagement.Floors.FloorAppService),
                "GetAsync");
            var floorList = ChildRoute(typeof(BuildingManagement.Floors.FloorAppService),
                "GetListAsync");
            var floorUpdate = ChildRoute(typeof(BuildingManagement.Floors.FloorAppService),
                "UpdateAsync");
            var floorDelete = ChildRoute(typeof(BuildingManagement.Floors.FloorAppService),
                "DeleteAsync");
            var roomCreate = ChildRoute(typeof(BuildingManagement.Rooms.RoomAppService),
                "CreateAsync");
            var roomGet = ChildRoute(typeof(BuildingManagement.Rooms.RoomAppService),
                "GetAsync");
            var roomList = ChildRoute(typeof(BuildingManagement.Rooms.RoomAppService),
                "GetListAsync");
            var roomUpdate = ChildRoute(typeof(BuildingManagement.Rooms.RoomAppService),
                "UpdateAsync");
            var roomDelete = ChildRoute(typeof(BuildingManagement.Rooms.RoomAppService),
                "DeleteAsync");
            // Slice C FAIL-CLOSED boundary on the ACTUAL MVC-generated
            // routes. Both BootstrapOwner and a valid building operator
            // must use only future separately audited scoped utility APIs.
            // Authentication, Contract permission and Building membership
            // do not grant the old global utility endpoints.
            var legacyRateList = ChildRoute(
                typeof(BuildingManagement.Utilities.UtilityRateAppService),
                "GetListAsync");
            var legacyRateCreate = ChildRoute(
                typeof(BuildingManagement.Utilities.UtilityRateAppService),
                "CreateAsync");
            var legacyMeterList = ChildRoute(
                typeof(BuildingManagement.Utilities.UtilityMeterAppService),
                "GetListAsync");
            var legacyReadingList = ChildRoute(
                typeof(BuildingManagement.Utilities.MeterReadingAppService),
                "GetListAsync");
            foreach (var caller in new[] { clientRoot, clientB })
            {
                foreach (var route in new[] {
                    legacyRateList, legacyMeterList, legacyReadingList })
                {
                    using var utilityReadBlocked = await caller.GetAsync(route);
                    Assert.Equal(HttpStatusCode.Forbidden, utilityReadBlocked.StatusCode);
                }
                using var payload = new StringContent(
                    JsonSerializer.Serialize(new {
                        buildingId = b, utilityType = 1, unitPrice = 3500m,
                        effectiveFrom = "2026-11-01T00:00:00Z"
                    }), Encoding.UTF8, "application/json");
                using var utilityWriteBlocked = await caller.PostAsync(
                    legacyRateCreate, payload);
                Assert.Equal(HttpStatusCode.Forbidden, utilityWriteBlocked.StatusCode);
            }
            using (var scope = app.Services.CreateScope())
            {
                using var unit = scope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await scope.ServiceProvider.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(0, await db.UtilityRates.CountAsync());
                await unit.CompleteAsync();
            }

            // Slice C first usable vertical: ACTUAL separate Utilities.Read
            // and Utilities.Manage, original business validation, and
            // server-locked per-building scope. Legacy global rate endpoints
            // remain denied even after these grants are added.
            Assert.Contains(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(SliceCStagingUtilityRateController) &&
                action.MethodInfo.Name == "CreateAsync");
            var sliceCRates = "/api/slice-c-staging/utility-rates";
            string SliceCRateAt(Guid id) =>
                sliceCRates + "/" + id.ToString("D");
            // A bounded historical interval avoids contaminating the
            // later full financial HTTP scenario (whose valid 2026-08+
            // tariffs are independently tested). Both still exercise the
            // same DB exclusion index for overlapping intervals.
            object RateInput(Guid buildingId, decimal price, int utilityType = 1,
                string effectiveFrom = "2025-12-01T00:00:00Z") =>
                new { buildingId, utilityType, unitPrice = price,
                    effectiveFrom, effectiveTo = "2026-01-31T00:00:00Z",
                    notes = "Staging-only tariff evidence" };
            async Task<(HttpStatusCode Status, string Body)> RatePost(
                HttpClient who, object payload)
            {
                using var content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8,
                    "application/json");
                using var response = await who.PostAsync(sliceCRates, content);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }
            var noGrantRate = await RatePost(clientB,
                RateInput(a, 3000m));
            Assert.Equal(HttpStatusCode.Forbidden, noGrantRate.Status);
            var createdRateB = await RatePost(clientB,
                RateInput(b, 3400m));
            Assert.Equal(HttpStatusCode.OK, createdRateB.Status);
            using var rateBDocument = JsonDocument.Parse(createdRateB.Body);
            var sliceCRateB = rateBDocument.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(b,
                rateBDocument.RootElement.GetProperty("buildingId").GetGuid());
            Assert.DoesNotContain("notes", createdRateB.Body,
                StringComparison.OrdinalIgnoreCase);

            var createdRateA = await RatePost(clientRoot,
                RateInput(a, 3700m));
            Assert.Equal(HttpStatusCode.OK, createdRateA.Status);
            using var rateADocument = JsonDocument.Parse(createdRateA.Body);
            var sliceCRateA = rateADocument.RootElement.GetProperty("id").GetGuid();
            using (var deniedForeignRate = await clientB.GetAsync(SliceCRateAt(sliceCRateA)))
                Assert.Equal(HttpStatusCode.Forbidden, deniedForeignRate.StatusCode);
            using (var allowedOwnRate = await clientB.GetAsync(SliceCRateAt(sliceCRateB)))
                Assert.Equal(HttpStatusCode.OK, allowedOwnRate.StatusCode);

            using (var ratesB = await clientB.GetAsync(sliceCRates + "?maxResultCount=20"))
            {
                Assert.Equal(HttpStatusCode.OK, ratesB.StatusCode);
                var body = await ratesB.Content.ReadAsStringAsync();
                Assert.Contains(sliceCRateB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(sliceCRateA.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var ratesRoot = await clientRoot.GetAsync(sliceCRates))
            {
                Assert.Equal(HttpStatusCode.OK, ratesRoot.StatusCode);
                var body = await ratesRoot.Content.ReadAsStringAsync();
                Assert.Contains(sliceCRateA.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains(sliceCRateB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var forbiddenUpdateBody = new StringContent(
                JsonSerializer.Serialize(RateInput(b, 4000m)),
                Encoding.UTF8, "application/json"))
            using (var forbiddenUpdate = await clientB.PutAsync(
                SliceCRateAt(sliceCRateA), forbiddenUpdateBody))
                Assert.Equal(HttpStatusCode.Forbidden, forbiddenUpdate.StatusCode);

            // A transfer to a new building must NOT be possible through
            // UPDATE even for a root authorized in both buildings.
            using (var transferBody = new StringContent(
                JsonSerializer.Serialize(RateInput(a, 5000m)),
                Encoding.UTF8, "application/json"))
            using (var deniedTransfer = await clientRoot.PutAsync(
                SliceCRateAt(sliceCRateB), transferBody))
                Assert.Equal(HttpStatusCode.Forbidden, deniedTransfer.StatusCode);

            var duplicateRate = await RatePost(clientB, RateInput(b, 3600m));
            Assert.NotEqual(HttpStatusCode.OK, duplicateRate.Status);
            using (var updateBody = new StringContent(
                JsonSerializer.Serialize(RateInput(b, 3550m)),
                Encoding.UTF8, "application/json"))
            using (var updated = await clientB.PutAsync(
                SliceCRateAt(sliceCRateB), updateBody))
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using (var current = await clientB.GetAsync(SliceCRateAt(sliceCRateB)))
            {
                Assert.Equal(HttpStatusCode.OK, current.StatusCode);
                using var parsedRate = JsonDocument.Parse(
                    await current.Content.ReadAsStringAsync());
                Assert.Equal(3550m,
                    parsedRate.RootElement.GetProperty("unitPrice").GetDecimal());
            }
            using (var foreignDelete = await clientB.DeleteAsync(
                SliceCRateAt(sliceCRateA)))
                Assert.Equal(HttpStatusCode.Forbidden, foreignDelete.StatusCode);
            using (var ownDelete = await clientB.DeleteAsync(
                SliceCRateAt(sliceCRateB)))
                Assert.Equal(HttpStatusCode.OK, ownDelete.StatusCode);
            using (var afterDelete = await clientB.GetAsync(SliceCRateAt(sliceCRateB)))
                Assert.Equal(HttpStatusCode.Forbidden, afterDelete.StatusCode);
            // The unscoped conventional routes stay forbidden regardless
            // of distinct utility permissions issued by ABP Identity.
            using (var oldRateList = await clientRoot.GetAsync(legacyRateList))
                Assert.Equal(HttpStatusCode.Forbidden, oldRateList.StatusCode);

            var floorA = await CreateChildAsync(clientRoot, floorCreate,
                new { buildingId = a, floorNumber = 1, name = "PG Floor A1" });
            var floorB = await CreateChildAsync(clientB, floorCreate,
                new { buildingId = b, floorNumber = 1, name = "PG Floor B1" });
            using (var response = await clientB.GetAsync(ChildId(floorGet, floorA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(ChildId(floorGet, floorB)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(floorList))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains(floorB.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(floorA.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }
            var roomA = await CreateChildAsync(clientRoot, roomCreate,
                new { floorId = floorA, roomNumber = "101", name = "PG A101",
                    area = 25.0, capacity = 2, status = 0 });
            var roomB = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "101", name = "PG B101",
                    area = 25.0, capacity = 2, status = 0 });
            using (var response = await clientB.GetAsync(ChildId(roomGet, roomA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(ChildId(roomGet, roomB)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(roomList))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains(roomB.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(roomA.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }
            // Slice C meter-reading vertical, against two independent physical
            // meters in dedicated rooms. This does not pollute the existing
            // move-out and final-invoice metering fixtures.
            Assert.Contains(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(SliceCStagingMeterReadingController) &&
                action.MethodInfo.Name == "CreateAsync");
            // Dedicated physical floors ensure meter history cannot pollute
            // the pre-existing Slice A test which deletes its original A1
            // floor after clearing only Room A101. The new meter histories
            // must remain intact to exercise their own integrity rules.
            var sliceCMeterFloorA = await CreateChildAsync(clientRoot, floorCreate,
                new { buildingId = a, floorNumber = 2, name = "Meter probes A2" });
            var sliceCMeterFloorB = await CreateChildAsync(clientB, floorCreate,
                new { buildingId = b, floorNumber = 2, name = "Meter probes B2" });
            var sliceCProbeRoomA = await CreateChildAsync(clientRoot, roomCreate,
                new { floorId = sliceCMeterFloorA, roomNumber = "206",
                    name = "Meter-reading A", area = 20m, capacity = 1, status = 0 });
            var sliceCProbeRoomB = await CreateChildAsync(clientB, roomCreate,
                new { floorId = sliceCMeterFloorB, roomNumber = "207",
                    name = "Meter-reading B", area = 20m, capacity = 1, status = 0 });
            Assert.Contains(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(SliceCStagingUtilityMeterController) &&
                action.MethodInfo.Name == "ReplaceAsync");
            var sliceCMeterPath = "/api/slice-c-staging/utility-meters";
            string SliceCMeterAt(Guid id) =>
                sliceCMeterPath + "/" + id.ToString("D");
            object SliceCMeterInput(Guid roomId, int utilityType,
                decimal initialReading, string meterCode) =>
                new { roomId, utilityType, initialReading, meterCode,
                    installedDate = "2026-01-01T00:00:00Z",
                    notes = "Internal physical meter note" };
            async Task<(HttpStatusCode Status, string Body)> SliceCMeterPost(
                HttpClient who, object input)
            {
                using var payload = new StringContent(
                    JsonSerializer.Serialize(input), Encoding.UTF8,
                    "application/json");
                using var response = await who.PostAsync(sliceCMeterPath, payload);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }
            var sliceCCodeA = "C-A-" + Guid.NewGuid().ToString("N");
            var sliceCCodeB = "C-B-" + Guid.NewGuid().ToString("N");
            var crossMeterInsert = await SliceCMeterPost(clientB,
                SliceCMeterInput(sliceCProbeRoomA, 1, 10m, sliceCCodeA));
            Assert.Equal(HttpStatusCode.Forbidden, crossMeterInsert.Status);
            var createdMeterA = await SliceCMeterPost(clientRoot,
                SliceCMeterInput(sliceCProbeRoomA, 1, 10m, sliceCCodeA));
            Assert.Equal(HttpStatusCode.OK, createdMeterA.Status);
            using var sliceCMeterDocA = JsonDocument.Parse(createdMeterA.Body);
            var sliceCMeterA = sliceCMeterDocA.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(a,
                sliceCMeterDocA.RootElement.GetProperty("buildingId").GetGuid());
            Assert.DoesNotContain("notes", createdMeterA.Body,
                StringComparison.OrdinalIgnoreCase);
            var createdMeterB = await SliceCMeterPost(clientB,
                SliceCMeterInput(sliceCProbeRoomB, 2, 20m, sliceCCodeB));
            Assert.Equal(HttpStatusCode.OK, createdMeterB.Status);
            using var sliceCMeterDocB = JsonDocument.Parse(createdMeterB.Body);
            var sliceCMeterB = sliceCMeterDocB.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(b,
                sliceCMeterDocB.RootElement.GetProperty("buildingId").GetGuid());

            using (var foreignMeter = await clientB.GetAsync(SliceCMeterAt(sliceCMeterA)))
                Assert.Equal(HttpStatusCode.Forbidden, foreignMeter.StatusCode);
            using (var ownMeter = await clientB.GetAsync(SliceCMeterAt(sliceCMeterB)))
                Assert.Equal(HttpStatusCode.OK, ownMeter.StatusCode);
            using (var ownMeters = await clientB.GetAsync(
                sliceCMeterPath + "?maxResultCount=20"))
            {
                Assert.Equal(HttpStatusCode.OK, ownMeters.StatusCode);
                var body = await ownMeters.Content.ReadAsStringAsync();
                Assert.Contains(sliceCMeterB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(sliceCMeterA.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var foreignMeterUpdateBody = new StringContent(
                JsonSerializer.Serialize(new {
                    meterCode = sliceCCodeA, initialReading = 10m,
                    installedDate = "2026-01-01T00:00:00Z" }),
                Encoding.UTF8, "application/json"))
            using (var foreignMeterUpdate = await clientB.PutAsync(
                SliceCMeterAt(sliceCMeterA), foreignMeterUpdateBody))
                Assert.Equal(HttpStatusCode.Forbidden, foreignMeterUpdate.StatusCode);
            var sliceCReadingPath = "/api/slice-c-staging/meter-readings";
            string SliceCReadingAt(Guid id) =>
                sliceCReadingPath + "/" + id.ToString("D");
            object SliceCReadingInput(Guid meterId, decimal value, int month = 2) =>
                new { utilityMeterId = meterId, billingYear = 2026,
                    billingMonth = month, currentReading = value,
                    readingDate = $"2026-{month:00}-15T00:00:00Z",
                    notes = "Internal-only meter audit notes" };
            async Task<(HttpStatusCode Status, string Body)> SliceCReadingPost(
                HttpClient who, object input)
            {
                using var payload = new StringContent(
                    JsonSerializer.Serialize(input), Encoding.UTF8,
                    "application/json");
                using var response = await who.PostAsync(sliceCReadingPath, payload);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }
            var crossMeterCreate = await SliceCReadingPost(clientB,
                SliceCReadingInput(sliceCMeterA, 12m));
            Assert.Equal(HttpStatusCode.Forbidden, crossMeterCreate.Status);
            var createdReadingA = await SliceCReadingPost(clientRoot,
                SliceCReadingInput(sliceCMeterA, 12m));
            Assert.Equal(HttpStatusCode.OK, createdReadingA.Status);
            using var sliceCReadDocA = JsonDocument.Parse(createdReadingA.Body);
            var sliceCReadingA = sliceCReadDocA.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(a,
                sliceCReadDocA.RootElement.GetProperty("buildingId").GetGuid());
            Assert.DoesNotContain("notes", createdReadingA.Body,
                StringComparison.OrdinalIgnoreCase);
            var createdReadingB = await SliceCReadingPost(clientB,
                SliceCReadingInput(sliceCMeterB, 24m));
            Assert.Equal(HttpStatusCode.OK, createdReadingB.Status);
            using var sliceCReadDocB = JsonDocument.Parse(createdReadingB.Body);
            var sliceCReadingB = sliceCReadDocB.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(b,
                sliceCReadDocB.RootElement.GetProperty("buildingId").GetGuid());

            using (var foreignRead = await clientB.GetAsync(
                SliceCReadingAt(sliceCReadingA)))
                Assert.Equal(HttpStatusCode.Forbidden, foreignRead.StatusCode);
            using (var ownRead = await clientB.GetAsync(
                SliceCReadingAt(sliceCReadingB)))
                Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
            using (var onlyB = await clientB.GetAsync(
                sliceCReadingPath + "?maxResultCount=10"))
            {
                Assert.Equal(HttpStatusCode.OK, onlyB.StatusCode);
                var body = await onlyB.Content.ReadAsStringAsync();
                Assert.Contains(sliceCReadingB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(sliceCReadingA.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            var duplicateReading = await SliceCReadingPost(clientB,
                SliceCReadingInput(sliceCMeterB, 25m));
            Assert.NotEqual(HttpStatusCode.OK, duplicateReading.Status);
            using (var foreignReadingUpdate = new StringContent(
                JsonSerializer.Serialize(new {
                    currentReading = 14m, readingDate = "2026-02-16T00:00:00Z" }),
                Encoding.UTF8, "application/json"))
            using (var forbidden = await clientB.PutAsync(
                SliceCReadingAt(sliceCReadingA), foreignReadingUpdate))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var ownReadingUpdate = new StringContent(
                JsonSerializer.Serialize(new {
                    currentReading = 27m, readingDate = "2026-02-16T00:00:00Z",
                    notes = "Another internal note" }),
                Encoding.UTF8, "application/json"))
            using (var updated = await clientB.PutAsync(
                SliceCReadingAt(sliceCReadingB), ownReadingUpdate))
            {
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                var body = await updated.Content.ReadAsStringAsync();
                Assert.DoesNotContain("notes", body,
                    StringComparison.OrdinalIgnoreCase);
                using var parsed = JsonDocument.Parse(body);
                Assert.Equal(27m,
                    parsed.RootElement.GetProperty("currentReading").GetDecimal());
            }
            using (var legacyReading = await clientRoot.GetAsync(legacyReadingList))
                Assert.Equal(HttpStatusCode.Forbidden, legacyReading.StatusCode);

            // Physical meter replacement and closing usage retain the
            // previous monthly history in one transactional operation.
            object SliceCReplacement(decimal closing, string newCode) =>
                new { newMeterCode = newCode, oldFinalReading = closing,
                    newInitialReading = 5m,
                    replacementDate = "2026-03-01T00:00:00Z",
                    reason = "Staging test: exchanged physical water meter",
                    notes = "Private handover note" };
            async Task<(HttpStatusCode Status, string Body)> SliceCReplace(
                HttpClient who, Guid meter, object replacement)
            {
                using var payload = new StringContent(
                    JsonSerializer.Serialize(replacement), Encoding.UTF8,
                    "application/json");
                using var response = await who.PostAsync(
                    SliceCMeterAt(meter) + "/replace", payload);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }
            var newSliceCCode = "C-B-NEW-" + Guid.NewGuid().ToString("N");
            var crossMeterReplace = await SliceCReplace(clientB,
                sliceCMeterA, SliceCReplacement(30m, newSliceCCode));
            Assert.Equal(HttpStatusCode.Forbidden, crossMeterReplace.Status);
            var invalidSliceCReplacement = await SliceCReplace(clientB,
                sliceCMeterB, SliceCReplacement(21m, newSliceCCode));
            Assert.NotEqual(HttpStatusCode.OK, invalidSliceCReplacement.Status);
            using (var oldStillActive = await clientB.GetAsync(
                SliceCMeterAt(sliceCMeterB)))
            {
                Assert.Equal(HttpStatusCode.OK, oldStillActive.StatusCode);
                using var parsed = JsonDocument.Parse(
                    await oldStillActive.Content.ReadAsStringAsync());
                Assert.True(parsed.RootElement.GetProperty("isActive").GetBoolean());
            }
            var replacedC = await SliceCReplace(clientB,
                sliceCMeterB, SliceCReplacement(30m, newSliceCCode));
            Assert.Equal(HttpStatusCode.OK, replacedC.Status);
            using var replacementDoc = JsonDocument.Parse(replacedC.Body);
            var sliceCReplacementId = replacementDoc.RootElement
                .GetProperty("id").GetGuid();
            var sliceCNewMeterId = replacementDoc.RootElement
                .GetProperty("newMeterId").GetGuid();
            Assert.Equal(27m, replacementDoc.RootElement
                .GetProperty("oldPreviousReading").GetDecimal());
            Assert.DoesNotContain("notes", replacedC.Body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reason", replacedC.Body,
                StringComparison.OrdinalIgnoreCase);
            using (var ownReplacement = await clientB.GetAsync(
                sliceCMeterPath + "/replacements/" + sliceCReplacementId))
                Assert.Equal(HttpStatusCode.OK, ownReplacement.StatusCode);
            using (var ownReplacementList = await clientB.GetAsync(
                sliceCMeterPath + "/rooms/" + sliceCProbeRoomB + "/replacements"))
            {
                Assert.Equal(HttpStatusCode.OK, ownReplacementList.StatusCode);
                var body = await ownReplacementList.Content.ReadAsStringAsync();
                Assert.Contains(sliceCReplacementId.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var deniedReplacementList = await clientB.GetAsync(
                sliceCMeterPath + "/rooms/" + sliceCProbeRoomA + "/replacements"))
                Assert.Equal(HttpStatusCode.Forbidden,
                    deniedReplacementList.StatusCode);
            using (var oldAfterReplacement = await clientB.GetAsync(
                SliceCMeterAt(sliceCMeterB)))
            {
                Assert.Equal(HttpStatusCode.OK, oldAfterReplacement.StatusCode);
                using var parsed = JsonDocument.Parse(
                    await oldAfterReplacement.Content.ReadAsStringAsync());
                Assert.False(parsed.RootElement.GetProperty("isActive").GetBoolean());
            }
            using (var historicalReading = await clientB.GetAsync(
                SliceCReadingAt(sliceCReadingB)))
            {
                Assert.Equal(HttpStatusCode.OK, historicalReading.StatusCode);
                using var parsed = JsonDocument.Parse(
                    await historicalReading.Content.ReadAsStringAsync());
                Assert.Equal(27m,
                    parsed.RootElement.GetProperty("currentReading").GetDecimal());
            }
            using (var postReplacementEdit = new StringContent(
                JsonSerializer.Serialize(new {
                    currentReading = 29m, readingDate = "2026-02-17T00:00:00Z" }),
                Encoding.UTF8, "application/json"))
            using (var frozen = await clientB.PutAsync(
                SliceCReadingAt(sliceCReadingB), postReplacementEdit))
                Assert.NotEqual(HttpStatusCode.OK, frozen.StatusCode);
            using (var deactivateNew = await clientB.PostAsync(
                SliceCMeterAt(sliceCNewMeterId) + "/deactivate", null))
            {
                Assert.Equal(HttpStatusCode.OK, deactivateNew.StatusCode);
                using var parsed = JsonDocument.Parse(
                    await deactivateNew.Content.ReadAsStringAsync());
                Assert.False(parsed.RootElement.GetProperty("isActive").GetBoolean());
            }
            using (var linkedDelete = await clientB.DeleteAsync(
                SliceCMeterAt(sliceCNewMeterId)))
                Assert.NotEqual(HttpStatusCode.OK, linkedDelete.StatusCode);

            // Editing/deleting an unbilled standalone meter still works,
            // but cannot bypass cross-building authorization.
            var sliceCOrphanCode = "C-ORPHAN-" + Guid.NewGuid().ToString("N");
            var orphanC = await SliceCMeterPost(clientB,
                SliceCMeterInput(sliceCProbeRoomB, 1, 0m, sliceCOrphanCode));
            Assert.Equal(HttpStatusCode.OK, orphanC.Status);
            using var orphanDoc = JsonDocument.Parse(orphanC.Body);
            var sliceCOrphanId = orphanDoc.RootElement.GetProperty("id").GetGuid();
            using (var updateOrphan = new StringContent(
                JsonSerializer.Serialize(new {
                    meterCode = "C-EDIT-" + Guid.NewGuid().ToString("N"),
                    initialReading = 0m,
                    installedDate = "2026-01-01T00:00:00Z" }),
                Encoding.UTF8, "application/json"))
            using (var update = await clientB.PutAsync(
                SliceCMeterAt(sliceCOrphanId), updateOrphan))
                Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using (var crossDelete = await clientB.DeleteAsync(
                SliceCMeterAt(sliceCMeterA)))
                Assert.Equal(HttpStatusCode.Forbidden, crossDelete.StatusCode);
            using (var ownDelete = await clientB.DeleteAsync(
                SliceCMeterAt(sliceCOrphanId)))
                Assert.Equal(HttpStatusCode.OK, ownDelete.StatusCode);
            using (var deleted = await clientB.GetAsync(
                SliceCMeterAt(sliceCOrphanId)))
                Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
            using (var unscopedMeter = await clientRoot.GetAsync(legacyMeterList))
                Assert.Equal(HttpStatusCode.Forbidden, unscopedMeter.StatusCode);

            // Slice B first functional vertical: authenticated contract
            // reads filtered in SQL by current Building membership. The
            // legacy ContractAppService remains HTTP deny-all.
            var contractA = Guid.NewGuid();
            var contractB = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var ctx = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                ctx.Contracts.AddRange(
                    new BuildingManagement.Contracts.Contract(
                        contractA, "SLICEB-A-" + contractA.ToString("N"), roomA,
                        new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                        null, 2500000m),
                    new BuildingManagement.Contracts.Contract(
                        contractB, "SLICEB-B-" + contractB.ToString("N"), roomB,
                        new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                        null, 3500000m));
                await ctx.SaveChangesAsync();
                await uow.CompleteAsync();
            }
            // Final-invoice Slice B endpoints MUST be reachable in the
            // isolated authenticated HTTP host, yet reject cross-building
            // callers and invalid/incomplete financial prerequisites.
            // Historically this test host omitted the financial adapter;
            // every endpoint then returned Forbidden without testing its
            // actual policy/transaction implementation.
            Assert.Contains(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(SliceBStagingFinalInvoiceController) &&
                action.MethodInfo.Name == "PrepareAsync");
            using (var serviceScope = app.Services.CreateScope())
                Assert.NotNull(serviceScope.ServiceProvider
                    .GetRequiredService<SliceBStagingFinalInvoiceMutation>());

            string FinalInvoices(Guid contract) =>
                "/api/slice-b-staging/final-invoices/contracts/" +
                contract.ToString("D");
            async Task<(HttpStatusCode Status, string Body)> PostFinalAsync(
                HttpClient caller, string route, string? payload)
            {
                using var content = new StringContent(
                    payload ?? "{}", Encoding.UTF8, "application/json");
                using var response = await caller.PostAsync(route, content);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }
            var missingMoveOut = Guid.NewGuid();
            var missingEvidence = Guid.NewGuid();
            var missingReview = Guid.NewGuid();
            var missingIssued = Guid.NewGuid();

            var foreignPrepare = await PostFinalAsync(clientB,
                FinalInvoices(contractA) + "/move-outs/" +
                    missingMoveOut.ToString("D") + "/prepare", "{}");
            Assert.Equal(HttpStatusCode.Forbidden, foreignPrepare.Status);
            var incompletePrepare = await PostFinalAsync(clientRoot,
                FinalInvoices(contractA) + "/move-outs/" +
                    missingMoveOut.ToString("D") + "/prepare", "{}");
            Assert.Equal(HttpStatusCode.Forbidden, incompletePrepare.Status);

            var foreignReview = await PostFinalAsync(clientB,
                FinalInvoices(contractA) + "/evidence/" +
                    missingEvidence.ToString("D") + "/reviews",
                """{"note":"Unauthorized building review"}""");
            Assert.Equal(HttpStatusCode.Forbidden, foreignReview.Status);
            var foreignDecision = await PostFinalAsync(clientB,
                FinalInvoices(contractA) + "/reviews/" +
                    missingReview.ToString("D") + "/decide",
                """{"acceptForDesign":true,"reason":"Foreign building signoff"}""");
            Assert.Equal(HttpStatusCode.Forbidden, foreignDecision.Status);

            var foreignReceipt = await PostFinalAsync(clientB,
                FinalInvoices(contractA) + "/issued/" +
                    missingIssued.ToString("D") + "/manual-receipts",
                JsonSerializer.Serialize(new {
                    tenantId = Guid.NewGuid(), amount = 100000m,
                    paidAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    transactionReference = "FOREIGN-RECEIPT-0001",
                    bankEvidenceReference = "FOREIGN-BANK-EVIDENCE-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, foreignReceipt.Status);

            var foreignCloseout = await PostFinalAsync(clientB,
                FinalInvoices(contractA) + "/move-outs/" +
                    missingMoveOut.ToString("D") + "/complete",
                JsonSerializer.Serialize(new {
                    confirmManualRefundPaid = true, forfeitAmount = 0m,
                    inspectionEvidenceReference = "UNVERIFIED-INSPECTION",
                    handoverEvidenceReference = "UNVERIFIED-HANDOVER",
                    depositPolicyReference = "UNVERIFIED-DEPOSIT-POLICY"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, foreignCloseout.Status);

            // Missing issued invoice/approved move-out must also deny a
            // correctly scoped ROOT rather than accidentally recognizing a
            // receipt or changing deposit/room/contract state.
            var missingReceipt = await PostFinalAsync(clientRoot,
                FinalInvoices(contractA) + "/issued/" +
                    missingIssued.ToString("D") + "/manual-receipts",
                JsonSerializer.Serialize(new {
                    tenantId = Guid.NewGuid(), amount = 100000m,
                    paidAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    transactionReference = "MISSING-ISSUED-0001",
                    bankEvidenceReference = "MISSING-LEDGER-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, missingReceipt.Status);
            var prematureCloseout = await PostFinalAsync(clientRoot,
                FinalInvoices(contractA) + "/move-outs/" +
                    missingMoveOut.ToString("D") + "/complete",
                JsonSerializer.Serialize(new {
                    confirmManualRefundPaid = true, forfeitAmount = 0m,
                    inspectionEvidenceReference = "MISSING-INSPECTION",
                    handoverEvidenceReference = "MISSING-HANDOVER",
                    depositPolicyReference = "MISSING-APPROVAL"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, prematureCloseout.Status);

            // Every rejected HTTP write must leave the existing finance
            // and closeout tables empty in this exact disposable database.
            using (var serviceScope = app.Services.CreateScope())
            {
                var sp = serviceScope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.False(await db.FinalInvoiceIssueEvidence.AnyAsync());
                Assert.False(await db.InvoiceIssuanceReviews.AnyAsync());
                Assert.False(await db.IssuedInvoiceLedger.AnyAsync());
                Assert.False(await db.InvoicePaymentAllocationJournal.AnyAsync());
                Assert.False(await db.DepositCloseoutApprovals.AnyAsync());
                await uow.CompleteAsync();
            }

            const string scopedContracts = "/api/slice-b-staging/contracts";
            using (var response = await clientB.GetAsync(scopedContracts))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == contractB);
                Assert.DoesNotContain(rows, row => row.GetProperty("id").GetGuid() == contractA);
            }
            using (var response = await clientRoot.GetAsync(scopedContracts))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == contractA);
                Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == contractB);
            }
            using (var response = await clientB.GetAsync(
                scopedContracts + "/" + contractA.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                scopedContracts + "/" + contractB.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(
                scopedContracts + "?maxResultCount=101"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Cross-database revocation cannot overtake contract list
            // disclosure while the read UoW is still open.
            Task<int>? revokeContractRead = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var principal = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (principal.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D"))
                    }, "CommitBoundContractRead"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var results = await sp.GetRequiredService<SliceBStagingContractQuery>()
                        .GetListAsync(0, 10);
                    Assert.Contains(results, row => row.Id == contractB);
                    Assert.DoesNotContain(results, row => row.Id == contractA);
                    revokeContractRead = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var command = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U'
                               AND "ProviderKey" = @actor
                               AND "Name" = @permission
                            """, connection);
                        command.Parameters.AddWithValue("actor", operatorB.ToString("D"));
                        command.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Contracts.Read);
                        return await command.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeContractRead.IsCompleted,
                        "Contracts.Read revocation must wait for contract read UoW.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeContractRead);
            Assert.Equal(1, await revokeContractRead!.WaitAsync(TimeSpan.FromSeconds(15)));
            using (var response = await clientB.GetAsync(scopedContracts))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Contracts.Read, true);
                await uow.CompleteAsync();
            }

            // Genuine PostgreSQL draft creation/update via fenced POST/PUT,
            // not the unscoped legacy ContractAppService public route.
            var contractC = Guid.Empty;
            var contractRace = Guid.Empty;
            string CreateJson(Guid room, string number) => JsonSerializer.Serialize(
                new { contractNumber = number, roomId = room,
                    startDate = "2026-11-01T00:00:00Z",
                    endDate = "2027-10-31T00:00:00Z",
                    monthlyRent = 2700000m, notes = "Isolated slice B" });
            var createNumber = "STAGE-B-" + Guid.NewGuid().ToString("N");
            using (var body = new StringContent(CreateJson(roomA,
                "DENIED-" + Guid.NewGuid().ToString("N")), Encoding.UTF8,
                "application/json"))
            using (var response = await clientB.PostAsync(scopedContracts, body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent(CreateJson(roomB, createNumber),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(scopedContracts, body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                contractC = json.RootElement.GetProperty("id").GetGuid();
                Assert.NotEqual(Guid.Empty, contractC);
            }
            string UpdateJson(Guid room, decimal rent) => JsonSerializer.Serialize(
                new { contractNumber = createNumber, roomId = room,
                    startDate = "2026-11-01T00:00:00Z",
                    endDate = "2027-10-31T00:00:00Z",
                    monthlyRent = rent, notes = "Updated under scope fence" });
            using (var body = new StringContent(UpdateJson(roomB, 2800000m),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(
                scopedContracts + "/" + contractA.ToString("D"), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent(UpdateJson(roomB, 2800000m),
                Encoding.UTF8, "application/json"))
            using (var response = await clientRoot.PutAsync(
                scopedContracts + "/" + contractA.ToString("D"), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent(UpdateJson(roomA, 2800000m),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(
                scopedContracts + "/" + contractC.ToString("D"), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent(UpdateJson(roomB, 2800000m),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(
                scopedContracts + "/" + contractC.ToString("D"), body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal(2800000m, json.RootElement
                    .GetProperty("monthlyRent").GetDecimal());
            }
            using (var response = await clientB.GetAsync(
                scopedContracts + "/" + contractC.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Tenant IDs are globally shared PII identifiers, not proof
            // of belonging to B. Seed A- and B-provenance in the SCRATCH
            // PostgreSQL database and exercise actual authenticated HTTP.
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                db.Tenants.AddRange(
                    new BuildingManagement.Tenants.Tenant(
                        tenantA, "Tenant A scratch", "090" + tenantA.ToString("N")[..7],
                        null, null, null,
                        BuildingManagement.Tenants.IdentityType.CitizenId,
                        "CID-A-" + tenantA.ToString("N")),
                    new BuildingManagement.Tenants.Tenant(
                        tenantB, "Tenant B scratch", "091" + tenantB.ToString("N")[..7],
                        null, null, null,
                        BuildingManagement.Tenants.IdentityType.CitizenId,
                        "CID-B-" + tenantB.ToString("N")));
                db.ContractTenants.AddRange(
                    new BuildingManagement.Contracts.ContractTenant(
                        Guid.NewGuid(), contractA, tenantA,
                        BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant),
                    new BuildingManagement.Contracts.ContractTenant(
                        Guid.NewGuid(), contractB, tenantB,
                        BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant));
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }

            // PHASE 2H OWNER DOCUMENT: genuinely authenticated HTTP against
            // the same two temporary databases and two independently scoped
            // buildings. No real tenant PII or external documents are used.
            // Assert the CURRENT persisted Identity fixture explicitly:
            // Root must have BootstrapOwner, Staff B must not. A failed HTTP
            // authorization check must not be "repaired" by silently giving
            // Staff B a root grant or by skipping its negative request.
            await using (var verifiedIdentity = new NpgsqlConnection(idCs))
            {
                await verifiedIdentity.OpenAsync();
                async Task<long> RootGrantCountAsync(Guid principal)
                {
                    await using var cmd = new NpgsqlCommand(
                        """
                        SELECT count(*) FROM "AbpPermissionGrants"
                         WHERE "ProviderName"='U' AND "ProviderKey"=@actor
                           AND "Name"=@permission
                        """, verifiedIdentity);
                    cmd.Parameters.AddWithValue("actor",principal.ToString("D"));
                    cmd.Parameters.AddWithValue("permission",
                        BuildingManagementPermissions.Buildings.BootstrapOwner);
                    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }
                Assert.Equal(1L,await RootGrantCountAsync(root));
                Assert.Equal(0L,await RootGrantCountAsync(operatorB));
            }
            string ContractExport(Guid id) =>
                scopedContracts + "/" + id.ToString("D") + "/export.docx";
            using (var otherBuilding = await clientB.GetAsync(ContractExport(contractA)))
                Assert.True(otherBuilding.StatusCode==HttpStatusCode.Forbidden,
                    $"B-only staff exporting A DOCX must be 403, actual={otherBuilding.StatusCode}. "+
                    $"Test server diagnostics: {serverErrors.Describe()}");
            // An operator with active Contracts.Read in Building B is NOT
            // BootstrapOwner. Even their OWN building export must be denied.
            using (var staffOnly = await clientB.GetAsync(ContractExport(contractB)))
                Assert.True(staffOnly.StatusCode==HttpStatusCode.Forbidden,
                    $"B-only staff cannot export their own Owner-only DOCX. "+
                    $"Actual={staffOnly.StatusCode}. Test errors: {serverErrors.Describe()}");
            using (var unauthenticated = app.GetTestClient())
            using (var anonymousExport = await unauthenticated.GetAsync(
                ContractExport(contractA)))
                Assert.Equal(HttpStatusCode.Unauthorized,anonymousExport.StatusCode);

            async Task VerifyOriginalOwnerDocxAsync(
                Guid originalContract, string expectedTenant, string excludedTenant,
                string expectedRoom)
            {
                using var response = await clientRoot.GetAsync(ContractExport(originalContract));
                var statusBody = response.StatusCode is HttpStatusCode.OK
                    ? string.Empty : await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    $"Original scoped Owner DOCX: {response.StatusCode} {statusBody}. "+
                    $"Test-server diagnostics: {serverErrors.Describe()}");
                Assert.Equal(
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.TryGetValues("Cache-Control",out var cache) &&
                    cache.Any(x=>x.Contains("no-store",StringComparison.OrdinalIgnoreCase)));
                Assert.True(response.Headers.TryGetValues("X-Content-Type-Options",
                    out var sniff) && sniff.Contains("nosniff"));
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.True(bytes.Length > 100 && bytes[0] == 'P' && bytes[1] == 'K');
                using var archive = new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
                var docEntry = Assert.Single(archive.Entries,
                    entry=>entry.FullName=="word/document.xml");
                using var docReader=new StreamReader(docEntry.Open());
                var xml = XDocument.Parse(await docReader.ReadToEndAsync());
                XNamespace ns =
                    "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                var body = string.Join(" ",
                    xml.Descendants(ns+"t").Select(element=>element.Value));
                Assert.Contains(expectedTenant,body);
                Assert.Contains(expectedRoom,body);
                Assert.DoesNotContain(excludedTenant,body);
                Assert.Contains("CHƯA PHẢI HỢP ĐỒNG ĐÃ KÝ",body);
                Assert.DoesNotContain("CID-A-",body);
                Assert.DoesNotContain("CID-B-",body);
            }

            await VerifyOriginalOwnerDocxAsync(contractA,
                "Tenant A scratch","Tenant B scratch","101");
            await VerifyOriginalOwnerDocxAsync(contractB,
                "Tenant B scratch","Tenant A scratch","101");

            // LIVE BootstrapOwner revocation must block even the root's
            // earlier authorized read. The app must not trust role claims
            // or a cached root response/document for a changed grant.
            using (var revokeScope = app.Services.CreateScope())
            {
                using var revokeUow = revokeScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew:true,isTransactional:true);
                await revokeScope.ServiceProvider.GetRequiredService<IPermissionManager>()
                    .SetForUserAsync(root,
                        BuildingManagementPermissions.Buildings.BootstrapOwner,false);
                await revokeUow.CompleteAsync();
            }
            using (var revokedRoot = await clientRoot.GetAsync(ContractExport(contractA)))
                Assert.True(revokedRoot.StatusCode==HttpStatusCode.Forbidden,
                    $"Persisted BootstrapOwner revocation must deny DOCX: "+
                    $"{revokedRoot.StatusCode}. {serverErrors.Describe()}");
            using (var restoreScope = app.Services.CreateScope())
            {
                using var restoreUow = restoreScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew:true,isTransactional:true);
                await restoreScope.ServiceProvider.GetRequiredService<IPermissionManager>()
                    .SetForUserAsync(root,
                        BuildingManagementPermissions.Buildings.BootstrapOwner,true);
                await restoreUow.CompleteAsync();
            }
            await VerifyOriginalOwnerDocxAsync(contractA,
                "Tenant A scratch","Tenant B scratch","101");

            // D03: REAL authenticated HTTP append and read of per-room
            // immutable tariff versions, with actual EF and PostgreSQL
            // triggers. Staff cannot read or set even their own room's
            // Owner-controlled price, and the same service has independent
            // price histories in A101 and B101.
            var serviceId=Guid.NewGuid();
            var initialEffective=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60));
            var nextEffective=initialEffective.AddDays(35);
            string Tariffs(Guid id) =>
                "/api/slice-b-staging/rooms/"+id.ToString("D")+"/service-tariffs";
            string TariffJson(decimal price,DateOnly effective) =>
                JsonSerializer.Serialize(new {
                    serviceId,unitPriceVnd=price,billingUnit=1,chargingRule=1,
                    effectiveFrom=effective.ToString("yyyy-MM-dd"),
                    signedPolicyReference="OWNER-APPROVED-ROOM-PRICE-D03-001"
                });
            async Task<(HttpStatusCode Status,string Body)> PostTariffAsync(
                HttpClient sender,Guid room,decimal price,DateOnly effective)
            {
                using var body=new StringContent(TariffJson(price,effective),
                    Encoding.UTF8,"application/json");
                using var reply=await sender.PostAsync(Tariffs(room),body);
                return (reply.StatusCode,await reply.Content.ReadAsStringAsync());
            }

            var deniedStaffTariff = await PostTariffAsync(
                clientB,roomB,200_000m,initialEffective);
            Assert.True(deniedStaffTariff.Status==HttpStatusCode.Forbidden,
                $"Staff tariff append must deny with HTTP 403, actual={deniedStaffTariff.Status}. "+
                $"Body={deniedStaffTariff.Body}");
            using (var deniedForeignRead=await clientB.GetAsync(
                Tariffs(roomA)+"?serviceId="+serviceId.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden,deniedForeignRead.StatusCode);
            using (var deniedOwnRead=await clientB.GetAsync(
                Tariffs(roomB)+"?serviceId="+serviceId.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden,deniedOwnRead.StatusCode);

            var priceA=await PostTariffAsync(
                clientRoot,roomA,150_000m,initialEffective);
            Assert.True(priceA.Status is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Owner Room A initial tariff: {priceA.Status} {priceA.Body}. "+
                $"Test server errors: {serverErrors.Describe()}");
            using (var json=JsonDocument.Parse(priceA.Body))
            {
                Assert.Equal(roomA,json.RootElement.GetProperty("roomId").GetGuid());
                Assert.Equal(150_000m,
                    json.RootElement.GetProperty("unitPriceVnd").GetDecimal());
                Assert.Equal(1,json.RootElement.GetProperty("version").GetInt32());
                Assert.False(json.RootElement.GetProperty("isPostedInvoice").GetBoolean());
            }
            var priceB=await PostTariffAsync(
                clientRoot,roomB,200_000m,initialEffective);
            Assert.True(priceB.Status is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Owner Room B independent tariff: {priceB.Status} {priceB.Body}. "+
                $"Test server errors: {serverErrors.Describe()}");
            var advancedA=await PostTariffAsync(
                clientRoot,roomA,180_000m,nextEffective);
            Assert.True(advancedA.Status is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Owner room A version two: {advancedA.Status} {advancedA.Body}");
            // Earlier price is immutable. An attempted same-date replay
            // fails rather than overwriting the original invoice source.
            var replayA=await PostTariffAsync(
                clientRoot,roomA,999_000m,nextEffective);
            Assert.True(replayA.Status==HttpStatusCode.Forbidden,
                $"Duplicate service tariff version must deny with HTTP 403. "+
                $"Actual={replayA.Status}, body={replayA.Body}");

            using (var listA=await clientRoot.GetAsync(
                Tariffs(roomA)+"?serviceId="+serviceId.ToString("D")))
            {
                Assert.Equal(HttpStatusCode.OK,listA.StatusCode);
                using var json=JsonDocument.Parse(
                    await listA.Content.ReadAsStringAsync());
                var versions=json.RootElement.EnumerateArray().ToArray();
                Assert.Equal(2,versions.Length);
                Assert.Equal(150_000m,versions[0].GetProperty("unitPriceVnd").GetDecimal());
                Assert.Equal(180_000m,versions[1].GetProperty("unitPriceVnd").GetDecimal());
                Assert.NotEqual(
                    versions[0].GetProperty("evidenceSha256").GetString(),
                    versions[1].GetProperty("evidenceSha256").GetString());
            }
            using (var listB=await clientRoot.GetAsync(
                Tariffs(roomB)+"?serviceId="+serviceId.ToString("D")))
            {
                Assert.Equal(HttpStatusCode.OK,listB.StatusCode);
                using var json=JsonDocument.Parse(
                    await listB.Content.ReadAsStringAsync());
                var version=Assert.Single(json.RootElement.EnumerateArray());
                Assert.Equal(200_000m,
                    version.GetProperty("unitPriceVnd").GetDecimal());
                Assert.Equal(1,version.GetProperty("version").GetInt32());
            }

            // COMPLETE STAGING FINANCIAL HTTP FLOW:
            // scoped month-end draft -> prepared immutable evidence ->
            // independent reviewer -> root-issued ledger -> completed bank
            // receipt -> journal -> root refund/closeout -> freed room.
            // The actual banks are NEVER contacted. Every database lives
            // under the two random scratch names created in this test.
            var financeRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "109",
                    name = "PG B109 finance end-to-end", area = 28.0,
                    capacity = 2,
                    status = (int)BuildingManagement.Rooms.RoomStatus.Occupied });
            var financeContract = Guid.NewGuid();
            var financeMoveOut = Guid.NewGuid();
            var financeDepositPaymentId = Guid.NewGuid();
            var financeInvoiceDraftId = Guid.NewGuid();
            var financeRateE = Guid.NewGuid();
            var financeRateW = Guid.NewGuid();
            var financeMeterE = Guid.NewGuid();
            var financeMeterW = Guid.NewGuid();
            var financeReadingE = Guid.NewGuid();
            var financeReadingW = Guid.NewGuid();
            const decimal monthlyRent = 3000000m;
            const decimal finalBill = 3083000m;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();

                var activeContract = new BuildingManagement.Contracts.Contract(
                    financeContract,
                    "FIN-HTTP-" + financeContract.ToString("N"),
                    financeRoom,
                    new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, monthlyRent);
                activeContract.Activate();
                db.Contracts.Add(activeContract);
                db.ContractTenants.Add(new BuildingManagement.Contracts.ContractTenant(
                    Guid.NewGuid(), financeContract, tenantB,
                    BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant));
                var request = new BuildingManagement.MoveOuts.MoveOutRequest(
                    financeMoveOut, financeContract, financeRoom,
                    new DateOnly(2026, 9, 30), "Isolated E2E move-out");
                request.Approve(DateTime.UtcNow.AddDays(-5),
                    "Month-end move-out approved for staging");
                db.MoveOutRequests.Add(request);
                foreach (var type in Enum.GetValues<
                    BuildingManagement.MoveOuts.MoveOutChecklistTaskType>())
                {
                    var task = new BuildingManagement.MoveOuts.MoveOutChecklistTask(
                        Guid.NewGuid(), financeMoveOut, type);
                    if (type == BuildingManagement.MoveOuts.MoveOutChecklistTaskType
                            .FinalMeterReading)
                        task.MarkFinalMeterReadingsVerified(
                            DateTime.UtcNow.AddDays(-5));
                    db.MoveOutChecklistTasks.Add(task);
                }

                var installed = new DateTime(2026, 8, 1);
                var readAt = new DateTime(2026, 9, 30);
                db.UtilityMeters.AddRange(
                    new BuildingManagement.Utilities.UtilityMeter(
                        financeMeterE, financeRoom,
                        BuildingManagement.Utilities.UtilityType.Electricity,
                        "PG-E-" + financeMeterE.ToString("N"),
                        10m, installed),
                    new BuildingManagement.Utilities.UtilityMeter(
                        financeMeterW, financeRoom,
                        BuildingManagement.Utilities.UtilityType.Water,
                        "PG-W-" + financeMeterW.ToString("N"),
                        15m, installed));
                db.UtilityRates.AddRange(
                    new BuildingManagement.Utilities.UtilityRate(
                        financeRateE, b,
                        BuildingManagement.Utilities.UtilityType.Electricity,
                        3500m, installed, null),
                    new BuildingManagement.Utilities.UtilityRate(
                        financeRateW, b,
                        BuildingManagement.Utilities.UtilityType.Water,
                        12000m, installed, null));
                db.MeterReadings.AddRange(
                    new BuildingManagement.Utilities.MeterReading(
                        financeReadingE, financeMeterE, 2026, 9,
                        10m, 20m, readAt),
                    new BuildingManagement.Utilities.MeterReading(
                        financeReadingW, financeMeterW, 2026, 9,
                        15m, 19m, readAt));

                var draft = BuildingManagement.Invoices.Invoice.CreateDraft(
                    financeInvoiceDraftId,
                    BuildingManagement.Invoices.InvoiceDraftSnapshot.Capture(
                        financeContract, financeRoom, 2026, 9, new[]
                        {
                            new BuildingManagement.Invoices.InvoiceDraftUtilityLineSnapshot(
                                BuildingManagement.Utilities.UtilityType.Electricity,
                                financeRateE, "SingleRateWholeMonth",
                                10m, 3500m, 35000m,
                                new[] {
                                    new BuildingManagement.Invoices.InvoiceDraftMeterSegmentSnapshot(
                                        financeReadingE, financeMeterE,
                                        BuildingManagement.Utilities.MeterUsageSourceType.MonthlyReading,
                                        readAt, 10m, 20m)
                                },
                                Array.Empty<string>()),
                            new BuildingManagement.Invoices.InvoiceDraftUtilityLineSnapshot(
                                BuildingManagement.Utilities.UtilityType.Water,
                                financeRateW, "SingleRateWholeMonth",
                                4m, 12000m, 48000m,
                                new[] {
                                    new BuildingManagement.Invoices.InvoiceDraftMeterSegmentSnapshot(
                                        financeReadingW, financeMeterW,
                                        BuildingManagement.Utilities.MeterUsageSourceType.MonthlyReading,
                                        readAt, 15m, 19m)
                                },
                                Array.Empty<string>())
                        }), Guid.NewGuid);
                db.Invoices.Add(draft);

                // An independent completed CONTRACT deposit payment and
                // durable settlement prove the funds held. This is not a
                // synthetic refund and never reduces the final invoice.
                var deposit = new BuildingManagement.Payments.Payment(
                    financeDepositPaymentId,
                    "PG-DEPOSIT-" + financeDepositPaymentId.ToString("N"),
                    tenantB,
                    BuildingManagement.Payments.PaymentPurpose.ContractDeposit,
                    BuildingManagement.Payments.BuildingManagement.Payments
                        .PaymentMethod.BankTransfer,
                    BuildingManagement.Payments.PaymentChannel.Manual,
                    monthlyRent, contractId: financeContract,
                    transactionReference: "SCRATCH-INITIAL-DEPOSIT-2026");
                deposit.Complete(DateTime.UtcNow.AddDays(-25),
                    "SCRATCH-INITIAL-DEPOSIT-2026");
                db.Payments.Add(deposit);
                await db.SaveChangesAsync();
                var allocation = new BuildingManagement.DepositSettlements
                    .DepositSettlement(
                        Guid.NewGuid(),
                        BuildingManagement.DepositSettlements.DepositSettlementType
                            .AllocateToContractDeposit,
                        monthlyRent,
                        paymentId: financeDepositPaymentId,
                        targetContractId: financeContract,
                        notes: "Verified isolated held deposit");
                allocation.Complete(DateTime.UtcNow.AddDays(-24));
                db.DepositSettlements.Add(allocation);
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }

            // OI-04 POSITIVE authenticated PostgreSQL HTTP. This scratch
            // contract proves true bank-independent monthly debt and HELD
            // deposit, with no final invoice, to exercise the separate
            // staff review -> Root approval sequence. No offset is posted.
            var oi04RoomId = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "111",
                    name = "PG OI04 owner debt-offset approval",
                    area = 23m, capacity = 2,
                    status = (int)BuildingManagement.Rooms.RoomStatus.Occupied });
            var oi04ContractId = Guid.NewGuid();
            Guid oi04IssuedMonthlyId;
            var oi04RequestId = Guid.NewGuid();
            var oi04DepositPaymentId = Guid.NewGuid();
            var oi04DraftId = Guid.NewGuid();
            var oi04ElectricMeterId = Guid.NewGuid();
            var oi04WaterMeterId = Guid.NewGuid();
            var oi04ElectricReadingId = Guid.NewGuid();
            var oi04WaterReadingId = Guid.NewGuid();
            const decimal oi04MonthlyRent = 3_000_000m;
            using (var oi04SetupScope = app.Services.CreateScope())
            {
                var oi04Sp = oi04SetupScope.ServiceProvider;
                using var oi04Uow = oi04Sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var oi04Db = await oi04Sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var oi04Contract = new BuildingManagement.Contracts.Contract(
                    oi04ContractId, "OI04-HTTP-" + oi04ContractId.ToString("N"),
                    oi04RoomId,
                    new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, oi04MonthlyRent);
                oi04Contract.Activate();
                oi04Db.Contracts.Add(oi04Contract);
                oi04Db.ContractTenants.Add(
                    new BuildingManagement.Contracts.ContractTenant(
                        Guid.NewGuid(), oi04ContractId, tenantB,
                        BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant));
                var oi04MoveOut = new BuildingManagement.MoveOuts.MoveOutRequest(
                    oi04RequestId, oi04ContractId, oi04RoomId,
                    new DateOnly(2026, 10, 31),
                    "Signed end-of-month financial reconciliation");
                oi04MoveOut.Approve(DateTime.UtcNow.AddDays(-1),
                    "Approved standard month-end reconciliation");
                oi04Db.MoveOutRequests.Add(oi04MoveOut);

                var oi04Deposit = new BuildingManagement.Payments.Payment(
                    oi04DepositPaymentId,
                    "OI04-PAID-" + oi04DepositPaymentId.ToString("N"),
                    tenantB, BuildingManagement.Payments.PaymentPurpose.ContractDeposit,
                    BuildingManagement.Payments.BuildingManagement.Payments
                        .PaymentMethod.BankTransfer,
                    BuildingManagement.Payments.PaymentChannel.Manual,
                    oi04MonthlyRent, contractId: oi04ContractId,
                    transactionReference: "OI04-INITIAL-DEPOSIT-PAID-2026");
                oi04Deposit.Complete(DateTime.UtcNow.AddDays(-5),
                    "OI04-INITIAL-DEPOSIT-PAID-2026");
                oi04Db.Payments.Add(oi04Deposit);
                await oi04Db.SaveChangesAsync();
                var oi04InitialAllocation = new
                    BuildingManagement.DepositSettlements.DepositSettlement(
                        Guid.NewGuid(),
                        BuildingManagement.DepositSettlements.DepositSettlementType
                            .AllocateToContractDeposit,
                        oi04MonthlyRent, paymentId: oi04DepositPaymentId,
                        targetContractId: oi04ContractId,
                        notes: "Isolated test completed and accounted held cash");
                oi04InitialAllocation.Complete(DateTime.UtcNow.AddDays(-4));
                oi04Db.DepositSettlements.Add(oi04InitialAllocation);

                // Preserve the REAL original Draft, physical meters,
                // posted readings and previously approved building rates.
                // The official candidate references this exact draft ID.
                var oi04PhysicalAt = new DateTime(2026, 10, 31);
                oi04Db.UtilityMeters.AddRange(
                    new BuildingManagement.Utilities.UtilityMeter(
                        oi04ElectricMeterId,oi04RoomId,
                        BuildingManagement.Utilities.UtilityType.Electricity,
                        "OI04-E-" + oi04ElectricMeterId.ToString("N"),0m,
                        new DateTime(2026,9,1)),
                    new BuildingManagement.Utilities.UtilityMeter(
                        oi04WaterMeterId,oi04RoomId,
                        BuildingManagement.Utilities.UtilityType.Water,
                        "OI04-W-" + oi04WaterMeterId.ToString("N"),0m,
                        new DateTime(2026,9,1)));
                oi04Db.MeterReadings.AddRange(
                    new BuildingManagement.Utilities.MeterReading(
                        oi04ElectricReadingId,oi04ElectricMeterId,2026,10,
                        0m,10m,oi04PhysicalAt),
                    new BuildingManagement.Utilities.MeterReading(
                        oi04WaterReadingId,oi04WaterMeterId,2026,10,
                        0m,4m,oi04PhysicalAt));
                oi04Db.Invoices.Add(BuildingManagement.Invoices.Invoice.CreateDraft(
                    oi04DraftId,
                    BuildingManagement.Invoices.InvoiceDraftSnapshot.Capture(
                        oi04ContractId,oi04RoomId,2026,10,
                        new [] {
                            new BuildingManagement.Invoices.InvoiceDraftUtilityLineSnapshot(
                                BuildingManagement.Utilities.UtilityType.Electricity,
                                financeRateE,"SingleRateWholeMonth",10m,
                                3500m,35_000m,
                                new [] {new BuildingManagement.Invoices.InvoiceDraftMeterSegmentSnapshot(
                                    oi04ElectricReadingId,oi04ElectricMeterId,
                                    BuildingManagement.Utilities.MeterUsageSourceType.MonthlyReading,
                                    oi04PhysicalAt,0m,10m)},
                                Array.Empty<string>()),
                            new BuildingManagement.Invoices.InvoiceDraftUtilityLineSnapshot(
                                BuildingManagement.Utilities.UtilityType.Water,
                                financeRateW,"SingleRateWholeMonth",4m,
                                12000m,48_000m,
                                new [] {new BuildingManagement.Invoices.InvoiceDraftMeterSegmentSnapshot(
                                    oi04WaterReadingId,oi04WaterMeterId,
                                    BuildingManagement.Utilities.MeterUsageSourceType.MonthlyReading,
                                    oi04PhysicalAt,0m,4m)},
                                Array.Empty<string>())
                        }),Guid.NewGuid));
                var oi04At = DateTime.UtcNow.AddMinutes(-15);
                var oi04Source = BuildingManagement.Invoices
                    .MonthlyInvoiceCandidateLedgerRecord.Capture(Guid.NewGuid(),
                    new BuildingManagement.Invoices.MonthlyInvoiceCandidateSnapshot(
                        oi04DraftId, oi04ContractId, oi04RoomId, b,
                        2026, 10, root, oi04At,
                        new DateOnly(2026, 11, 7),
                        "SIGNED-OI04-MONTHLY-DUE-DATE-POLICY",
                        oi04MonthlyRent, 35_000m, 48_000m,
                        BuildingManagement.Invoices.AuxiliaryFeeAssessmentDecision
                            .ExplicitlyNoCharges,
                        "SIGNED-OI04-NO-ADDITIONAL-SERVICES",
                        Array.Empty<BuildingManagement.Invoices
                            .FinalInvoiceAuxiliaryChargeProposal>(),
                        0m, 3_083_000m,
                        new[] {
                            new BuildingManagement.Invoices.MonthlyInvoiceCandidateUtilityLine(
                                BuildingManagement.Utilities.UtilityType.Electricity,
                                financeRateE,10m,3500m,35_000m,
                                new[] { new BuildingManagement.Invoices.MonthlyInvoiceCandidateMeterSource(
                                    oi04ElectricReadingId,oi04ElectricMeterId,
                                    BuildingManagement.Utilities.MeterUsageSourceType.MonthlyReading,
                                    new DateTime(2026,10,31),0m,10m) }),
                            new BuildingManagement.Invoices.MonthlyInvoiceCandidateUtilityLine(
                                BuildingManagement.Utilities.UtilityType.Water,
                                financeRateW,4m,12000m,48_000m,
                                new[] { new BuildingManagement.Invoices.MonthlyInvoiceCandidateMeterSource(
                                    oi04WaterReadingId,oi04WaterMeterId,
                                    BuildingManagement.Utilities.MeterUsageSourceType.MonthlyReading,
                                    new DateTime(2026,10,31),0m,4m) })
                        }));
                var oi04IndependentReview = BuildingManagement.Invoices
                    .MonthlyInvoiceIndependentReviewRecord.Capture(
                        Guid.NewGuid(),oi04Source,operatorB,oi04At.AddMinutes(1),
                        true,"Signed independent reconciliation of October rent utilities");
                var oi04Issued = BuildingManagement.Invoices.MonthlyIssuedInvoiceRecord.Issue(
                    Guid.NewGuid(),"NT-OI04-PG-202610",root,oi04At.AddMinutes(2),
                    oi04Source,oi04IndependentReview);
                oi04IssuedMonthlyId = oi04Issued.Id;
                var oi04Posted = BuildingManagement.Invoices
                    .OfficialInvoiceReceivablePostingRecord.PostMonthly(
                        Guid.NewGuid(),oi04Issued,oi04Source,oi04IndependentReview,
                        root,oi04At.AddMinutes(3));
                oi04Db.MonthlyInvoiceCandidates.Add(oi04Source);
                oi04Db.MonthlyInvoiceReviews.Add(oi04IndependentReview);
                oi04Db.MonthlyIssuedInvoices.Add(oi04Issued);
                // The official posting SQL trigger insists that issuance
                // and independently reviewed frozen evidence already exist.
                // Flush sources in THIS same transaction before posting.
                await oi04Db.SaveChangesAsync();
                oi04Db.OfficialInvoiceReceivables.Add(oi04Posted);
                await oi04Db.SaveChangesAsync();
                await oi04Uow.CompleteAsync();
            }
            var oi04BaseRoute = FinalInvoices(oi04ContractId) +
                "/move-outs/" + oi04RequestId.ToString("D") + "/debt-offset";
            var oi04ReviewInput = new {
                signedReviewReference = "SIGNED-INDEPENDENT-OI04-SOURCE-REVIEW-001"
            };
            // Independent STAFF must sign; Owner later requires a
            // different authenticated actor and bootstrap privilege.
            var oi04StaffReview = await PostFinalAsync(clientB,
                oi04BaseRoute + "/review",
                JsonSerializer.Serialize(oi04ReviewInput));
            Assert.True(oi04StaffReview.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                "Independent OI04 review: " +
                oi04StaffReview.Status + " " + oi04StaffReview.Body);
            Guid oi04ReviewId;
            using (var oi04ReviewJson = JsonDocument.Parse(oi04StaffReview.Body))
            {
                oi04ReviewId = oi04ReviewJson.RootElement.GetProperty("reviewId").GetGuid();
                Assert.Equal(3_083_000m, oi04ReviewJson.RootElement
                    .GetProperty("verifiedOutstandingVnd").GetDecimal());
                Assert.False(oi04ReviewJson.RootElement
                    .GetProperty("canPostDepositOffset").GetBoolean());
            }
            var oi04ApproveRoute = oi04BaseRoute + "/reviews/" +
                oi04ReviewId.ToString("D") + "/approve";
            var oi04ApprovalPayload = JsonSerializer.Serialize(new {
                signedPolicyReference = "SIGNED-OI04-OWNER-RENT-UTILITY-PRIORITY-001"
            });
            var oi04StaffCannotApprove = await PostFinalAsync(clientB,
                oi04ApproveRoute,oi04ApprovalPayload);
            Assert.Equal(HttpStatusCode.Forbidden,oi04StaffCannotApprove.Status);
            var oi04OwnerApproval = await PostFinalAsync(clientRoot,
                oi04ApproveRoute,oi04ApprovalPayload);
            Assert.True(oi04OwnerApproval.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                "OI04 Owner authorization: " +
                oi04OwnerApproval.Status + " " + oi04OwnerApproval.Body);
            Guid oi04OwnerApprovalId;
            using (var oi04ApprovedJson=JsonDocument.Parse(oi04OwnerApproval.Body))
            {
                oi04OwnerApprovalId = oi04ApprovedJson.RootElement
                    .GetProperty("approvalId").GetGuid();
                Assert.Equal(3_000_000m,oi04ApprovedJson.RootElement
                    .GetProperty("approvedOffsetVnd").GetDecimal());
                Assert.Equal(0m,oi04ApprovedJson.RootElement
                    .GetProperty("expectedRemainingDepositVnd").GetDecimal());
                Assert.Equal(83_000m,oi04ApprovedJson.RootElement
                    .GetProperty("expectedUncoveredDebtVnd").GetDecimal());
                Assert.False(oi04ApprovedJson.RootElement
                    .GetProperty("isMoneyPosted").GetBoolean());
                Assert.False(oi04ApprovedJson.RootElement
                    .GetProperty("canCompleteMoveOut").GetBoolean());
            }
            var oi04ApproveReplay = await PostFinalAsync(clientRoot,
                oi04ApproveRoute,oi04ApprovalPayload);
            Assert.Equal(HttpStatusCode.OK,oi04ApproveReplay.Status);
            var oi04ChangedPolicy = await PostFinalAsync(clientRoot,
                oi04ApproveRoute,
                JsonSerializer.Serialize(new {
                    signedPolicyReference = "SIGNED-OI04-DIFFERENT-PRIORITY-REPLAY"
                }));
            Assert.Equal(HttpStatusCode.Forbidden,oi04ChangedPolicy.Status);
            using (var oi04InspectScope=app.Services.CreateScope())
            {
                using var oi04Uow=oi04InspectScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew:true,isTransactional:true);
                var oi04Db=await oi04InspectScope.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Single(await oi04Db.DebtOffsetReviews
                    .AsNoTracking().Where(x=>x.ContractId==oi04ContractId).ToArrayAsync());
                Assert.Single(await oi04Db.DebtOffsetApprovals
                    .AsNoTracking().Where(x=>x.ContractId==oi04ContractId).ToArrayAsync());
                Assert.False(await oi04Db.DepositSettlements.AnyAsync(x =>
                    x.SourceContractId==oi04ContractId &&
                    x.Type==BuildingManagement.DepositSettlements.DepositSettlementType.Forfeit));
                await oi04Uow.CompleteAsync();
            }

            // Phase 2G.4 real dual-ledger INTERNAL posting: different
            // from Owner authorization and never an external bank refund.
            var oi04PostingRoute = oi04BaseRoute + "/approvals/" +
                oi04OwnerApprovalId.ToString("D") + "/post";
            var oi04StaffCannotPost = await PostFinalAsync(clientB,
                oi04PostingRoute,"{}");
            Assert.Equal(HttpStatusCode.Forbidden,oi04StaffCannotPost.Status);
            var oi04PostedResponse = await PostFinalAsync(clientRoot,
                oi04PostingRoute,"{}");
            Assert.True(oi04PostedResponse.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                "OI04 atomic internal posting: " +
                oi04PostedResponse.Status + " " + oi04PostedResponse.Body);
            Guid oi04PostedId;
            using (var oi04PostedJson=JsonDocument.Parse(oi04PostedResponse.Body))
            {
                var postingRead=oi04PostedJson.RootElement;
                oi04PostedId=postingRead.GetProperty("postingId").GetGuid();
                Assert.Equal(3_000_000m,postingRead
                    .GetProperty("debitHeldDepositVnd").GetDecimal());
                Assert.Equal(3_000_000m,postingRead
                    .GetProperty("creditOfficialDebtVnd").GetDecimal());
                Assert.Equal(0m,postingRead
                    .GetProperty("remainingHeldDepositVnd").GetDecimal());
                Assert.Equal(83_000m,postingRead
                    .GetProperty("remainingOfficialDebtVnd").GetDecimal());
                Assert.True(postingRead.GetProperty("isInternalOffsetPosted").GetBoolean());
                Assert.False(postingRead.GetProperty("isBankRefundExecuted").GetBoolean());
                Assert.False(postingRead.GetProperty("canCompleteMoveOut").GetBoolean());
            }
            var oi04PostReplay=await PostFinalAsync(clientRoot,oi04PostingRoute,"{}");
            Assert.Equal(HttpStatusCode.OK,oi04PostReplay.Status);
            using (var replay=JsonDocument.Parse(oi04PostReplay.Body))
                Assert.Equal(oi04PostedId,replay.RootElement
                    .GetProperty("postingId").GetGuid());

            Guid oi04OfficialReceivableId;
            using (var oi04CheckScope=app.Services.CreateScope())
            {
                using var oi04CheckUow=oi04CheckScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew:true,isTransactional:true);
                var verifiedDb=await oi04CheckScope.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var postedRow=Assert.Single(await verifiedDb.DebtOffsetPostings
                    .AsNoTracking().Where(x=>x.ContractId==oi04ContractId).ToArrayAsync());
                Assert.Equal(oi04PostedId,postedRow.Id);
                Assert.Equal(3_000_000m,postedRow.CreditOfficialDebtVnd);
                oi04OfficialReceivableId=(await verifiedDb.OfficialInvoiceReceivables
                    .AsNoTracking().SingleAsync(x=>x.ContractId==oi04ContractId)).Id;
                Assert.False(await verifiedDb.DepositSettlements
                    .AsNoTracking().AnyAsync(x=>x.SourceContractId==oi04ContractId));
                await oi04CheckUow.CompleteAsync();
            }
            var oi04PostingHistory=await clientRoot.GetAsync(
                oi04BaseRoute+"/posting");
            Assert.Equal(HttpStatusCode.OK,oi04PostingHistory.StatusCode);
            // Official invoice balance must account for internal credit,
            // without inventing a completed manual bank receipt.
            var oi04PortfolioResponse=await clientRoot.GetAsync(
                FinalInvoices(oi04ContractId)+"/official-debt");
            Assert.Equal(HttpStatusCode.OK,oi04PortfolioResponse.StatusCode);
            using (var oi04PortfolioJson=JsonDocument.Parse(
                await oi04PortfolioResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal(3_083_000m,oi04PortfolioJson.RootElement
                    .GetProperty("totalAdjustedReceivables").GetDecimal());
                Assert.Equal(3_000_000m,oi04PortfolioJson.RootElement
                    .GetProperty("internalDepositOffsetVnd").GetDecimal());
                Assert.Equal(83_000m,oi04PortfolioJson.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
                Assert.Equal(0m,oi04PortfolioJson.RootElement
                    .GetProperty("independentlyReviewedManualPayments").GetDecimal());
            }
            // An accounting offset alone cannot close the rental or
            // fabricate a bank refund. Read all remaining source blockers.
            var oi04ReadinessResponse=await clientRoot.GetAsync(
                oi04BaseRoute+"/closeout-readiness");
            Assert.Equal(HttpStatusCode.OK,oi04ReadinessResponse.StatusCode);
            using (var readiness=JsonDocument.Parse(
                await oi04ReadinessResponse.Content.ReadAsStringAsync()))
            {
                var facts=readiness.RootElement;
                Assert.Equal(3_000_000m,facts.GetProperty(
                    "postedInternalDebtCreditVnd").GetDecimal());
                Assert.Equal(83_000m,facts.GetProperty(
                    "officialOutstandingDebtVnd").GetDecimal());
                Assert.Equal(0m,facts.GetProperty(
                    "remainingHeldDepositVnd").GetDecimal());
                Assert.True(facts.GetProperty("hasUnpaidOfficialDebt").GetBoolean());
                Assert.False(facts.GetProperty("canExecuteRefund").GetBoolean());
                Assert.False(facts.GetProperty("canCompleteMoveOut").GetBoolean());
                var reasons=facts.GetProperty("blockers").EnumerateArray()
                    .Select(x=>x.GetString()).ToArray();
                Assert.Contains("OutstandingOfficialDebtRequiresVerifiedCollection",
                    reasons);
                Assert.Contains("OneSignedFinalInvoiceRequired",reasons);
            }
            var oi04InvoiceBalanceResponse=await clientRoot.GetAsync(
                FinalInvoices(oi04ContractId)+"/receivables/"+
                oi04OfficialReceivableId.ToString("D")+"/balance");
            Assert.Equal(HttpStatusCode.OK,oi04InvoiceBalanceResponse.StatusCode);
            using (var oi04BalanceJson=JsonDocument.Parse(
                await oi04InvoiceBalanceResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal(3_000_000m,oi04BalanceJson.RootElement
                    .GetProperty("internalDepositOffsetVnd").GetDecimal());
                Assert.Equal(83_000m,oi04BalanceJson.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
                Assert.False(oi04BalanceJson.RootElement
                    .GetProperty("isExternallyVerifiedBankTransfer").GetBoolean());
            }

            // POST-OFFSET COLLECTION: use the original Payment + monthly
            // receipt journal, independent bank review, and complete fee split.
            // No competing deposit ledger, no source invoice rewrite.
            var oi04CollectPath=FinalInvoices(oi04ContractId)+
                "/monthly-issued/"+oi04IssuedMonthlyId.ToString("D")+
                "/manual-receipts";
            var oi04PaymentInput=JsonSerializer.Serialize(new {
                tenantId=tenantB,amount=83_000m,paidAtUtc=DateTime.UtcNow,
                transactionReference="OI04-NET-COLLECT-"+oi04ContractId.ToString("N"),
                bankEvidenceReference="SIGNED-OI04-BANK-NET-COLLECTION-001"
            });
            var oi04StaffCannotCollect=await PostFinalAsync(
                clientB,oi04CollectPath,oi04PaymentInput);
            Assert.Equal(HttpStatusCode.Forbidden,oi04StaffCannotCollect.Status);
            var oi04Collection=await PostFinalAsync(
                clientRoot,oi04CollectPath,oi04PaymentInput);
            Assert.True(oi04Collection.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                "Post-offset source Payment: "+oi04Collection.Status+" "+
                oi04Collection.Body);
            Guid oi04CollectionAllocation;
            using (var pay=JsonDocument.Parse(oi04Collection.Body))
                oi04CollectionAllocation=pay.RootElement
                    .GetProperty("allocationJournalId").GetGuid();

            var oi04ReviewPath=FinalInvoices(oi04ContractId)+
                "/receivables/"+oi04OfficialReceivableId.ToString("D")+
                "/allocations/"+oi04CollectionAllocation.ToString("D")+"/review";
            var oi04BankReview=await PostFinalAsync(clientB,oi04ReviewPath,
                JsonSerializer.Serialize(new {
                    accept=true,
                    bankStatementEvidenceReference=
                        "SIGNED-OI04-INDEPENDENT-STATEMENT-001",
                    reason="Independent bank statement confirms cash against residual utility debt"
                }));
            Assert.True(oi04BankReview.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                "Post-offset bank review: "+oi04BankReview.Status+" "+
                oi04BankReview.Body);
            Guid oi04BankReviewId;
            using(var review=JsonDocument.Parse(oi04BankReview.Body))
                oi04BankReviewId=review.RootElement.GetProperty("reviewId").GetGuid();

            // A reviewed bank transfer is NOT sufficient until its
            // physical utility charge allocation is signed and persisted.
            var oi04BeforeSplit=await clientRoot.GetAsync(
                oi04BaseRoute+"/closeout-readiness");
            Assert.Equal(HttpStatusCode.OK,oi04BeforeSplit.StatusCode);
            using(var incomplete=JsonDocument.Parse(
                await oi04BeforeSplit.Content.ReadAsStringAsync()))
            {
                var reasons=incomplete.RootElement
                    .GetProperty("blockers").EnumerateArray()
                    .Select(x=>x.GetString()).ToArray();
                Assert.Contains("AcceptedMonthlyBankReceiptMissingSignedFeeSplit",
                    reasons);
                Assert.False(incomplete.RootElement
                    .GetProperty("isFullyFinanciallyReconciled").GetBoolean());
            }
            var oi04SplitPath=FinalInvoices(oi04ContractId)+
                "/receivables/"+oi04OfficialReceivableId.ToString("D")+
                "/receipt-reviews/"+oi04BankReviewId.ToString("D")+
                "/line-split";
            var oi04Split=await PostFinalAsync(clientRoot,oi04SplitPath,
                JsonSerializer.Serialize(new {
                    signedAllocationReference="OI04-OWNER-NET-UTILITY-RECONCILIATION-001",
                    lines=new[]{
                        new {category=2,amountVnd=35_000m},
                        new {category=3,amountVnd=48_000m}
                    }
                }));
            Assert.True(oi04Split.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                "Post-offset approved utility split: "+oi04Split.Status+" "+
                oi04Split.Body);
            using(var split=JsonDocument.Parse(oi04Split.Body))
                Assert.Equal(83_000m,split.RootElement
                    .GetProperty("allocatedAmountVnd").GetDecimal());

            var oi04CollectedBalance=await clientRoot.GetAsync(
                FinalInvoices(oi04ContractId)+"/receivables/"+
                oi04OfficialReceivableId.ToString("D")+"/balance");
            Assert.Equal(HttpStatusCode.OK,oi04CollectedBalance.StatusCode);
            using(var net=JsonDocument.Parse(
                await oi04CollectedBalance.Content.ReadAsStringAsync()))
            {
                var balance=net.RootElement;
                Assert.Equal(3_000_000m,balance.GetProperty(
                    "internalDepositOffsetVnd").GetDecimal());
                Assert.Equal(83_000m,balance.GetProperty(
                    "independentlyReviewedManualPayments").GetDecimal());
                Assert.Equal(0m,balance.GetProperty(
                    "outstandingAmount").GetDecimal());
                Assert.False(balance.GetProperty(
                    "isExternallyVerifiedBankTransfer").GetBoolean());
            }
            var oi04DebtCollectedReadiness=await clientRoot.GetAsync(
                oi04BaseRoute+"/closeout-readiness");
            Assert.Equal(HttpStatusCode.OK,oi04DebtCollectedReadiness.StatusCode);
            using(var netReadiness=JsonDocument.Parse(
                await oi04DebtCollectedReadiness.Content.ReadAsStringAsync()))
            {
                var proof=netReadiness.RootElement;
                Assert.Equal(0m,proof.GetProperty(
                    "officialOutstandingDebtVnd").GetDecimal());
                Assert.Equal(83_000m,proof.GetProperty(
                    "independentlyReviewedBankReceiptsVnd").GetDecimal());
                Assert.DoesNotContain(
                    "AcceptedMonthlyBankReceiptMissingSignedFeeSplit",
                    proof.GetProperty("blockers").EnumerateArray()
                        .Select(x=>x.GetString()).ToArray());
                Assert.False(proof.GetProperty("canCompleteMoveOut").GetBoolean());
                Assert.Contains("OneSignedFinalInvoiceRequired",
                    proof.GetProperty("blockers").EnumerateArray()
                        .Select(x=>x.GetString()).ToArray());
            }

            // Phase 2G.3 regular billing preview: a scoped, repeatable-read
            // calculation uses only the known full-month rent and source-
            // backed utilities. Other service fees are explicitly UNKNOWN.
            var monthlyPreviewRoute = scopedContracts + "/" +
                financeContract.ToString("D") +
                "/monthly-invoice-preview/2026/9";
            using (var previewResponse = await clientB.GetAsync(monthlyPreviewRoute))
            {
                Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
                var body = await previewResponse.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal(financeContract,
                    json.RootElement.GetProperty("contractId").GetGuid());
                Assert.Equal(financeRoom,
                    json.RootElement.GetProperty("roomId").GetGuid());
                Assert.Equal(monthlyRent,
                    json.RootElement.GetProperty("knownFullMonthRentAmount").GetDecimal());
                Assert.False(json.RootElement.GetProperty("otherServiceFeesReviewed").GetBoolean());
                Assert.False(json.RootElement.GetProperty("canIssue").GetBoolean());
                Assert.False(json.RootElement.GetProperty("isOfficialReceivable").GetBoolean());
                Assert.Contains("OtherServiceFeesRequireReviewBeforeIssuance", body);
                Assert.DoesNotContain(tenantB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SCRATCH-INITIAL-DEPOSIT-2026", body);
                // If historical period coverage is ambiguous, refuse to
                // manufacture a definitive total rather than guessing.
                if (json.RootElement.TryGetProperty(
                    "provisionalRentAndUtilitiesAmount", out var amount) &&
                    amount.ValueKind == JsonValueKind.Number)
                    Assert.Equal(finalBill, amount.GetDecimal());
                else
                    Assert.Contains("MonthlyUtilitiesNotFullyVerified", body);
            }
            using (var foreignPreview = await clientB.GetAsync(
                scopedContracts + "/" + contractA.ToString("D") +
                "/monthly-invoice-preview/2026/9"))
                Assert.Equal(HttpStatusCode.Forbidden, foreignPreview.StatusCode);
            using (var invalidPreview = await clientB.GetAsync(
                scopedContracts + "/" + financeContract.ToString("D") +
                "/monthly-invoice-preview/2026/13"))
                Assert.Equal(HttpStatusCode.Forbidden, invalidPreview.StatusCode);

            // Phase 2G.3 true PostgreSQL persisted utility draft via scoped
            // HTTP Contracts.Manage; rent/services are intentionally NOT in
            // this draft and no debt may be recognized from its creation.
            var monthlyDraftRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "110",
                    name = "Monthly utility draft B110", area = 22m,
                    capacity = 2, status = (int)BuildingManagement.Rooms.RoomStatus.Occupied });
            var monthlyDraftContract = Guid.NewGuid();
            var monthlyElectricMeter = Guid.NewGuid();
            var monthlyWaterMeter = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var contract = new BuildingManagement.Contracts.Contract(
                    monthlyDraftContract, "MONTH-DRAFT-" + monthlyDraftContract.ToString("N"),
                    monthlyDraftRoom, new DateTime(2026, 8, 1), null, 2500000m);
                contract.Activate();
                db.Contracts.Add(contract);
                db.ContractTenants.Add(new BuildingManagement.Contracts.ContractTenant(
                    Guid.NewGuid(), monthlyDraftContract, tenantB,
                    BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant));
                db.UtilityMeters.AddRange(
                    new BuildingManagement.Utilities.UtilityMeter(
                        monthlyElectricMeter, monthlyDraftRoom,
                        BuildingManagement.Utilities.UtilityType.Electricity,
                        "MD-E-" + monthlyElectricMeter.ToString("N"), 0m,
                        new DateTime(2026, 7, 1)),
                    new BuildingManagement.Utilities.UtilityMeter(
                        monthlyWaterMeter, monthlyDraftRoom,
                        BuildingManagement.Utilities.UtilityType.Water,
                        "MD-W-" + monthlyWaterMeter.ToString("N"), 0m,
                        new DateTime(2026, 7, 1)));
                db.MeterReadings.AddRange(
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), monthlyElectricMeter, 2026, 8,
                        0m, 10m, new DateTime(2026, 8, 31)),
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), monthlyWaterMeter, 2026, 8,
                        0m, 15m, new DateTime(2026, 8, 31)),
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), monthlyElectricMeter, 2026, 9,
                        10m, 20m, new DateTime(2026, 9, 30)),
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), monthlyWaterMeter, 2026, 9,
                        15m, 19m, new DateTime(2026, 9, 30)));
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }
            var draftCreateRoute = scopedContracts + "/" +
                monthlyDraftContract.ToString("D") +
                "/monthly-utility-drafts/2026/9";
            using (var foreignDraft = await clientB.PostAsync(
                scopedContracts + "/" + contractA.ToString("D") +
                "/monthly-utility-drafts/2026/9", null))
                Assert.Equal(HttpStatusCode.Forbidden, foreignDraft.StatusCode);
            using (var invalidPeriodDraft = await clientB.PostAsync(
                scopedContracts + "/" + monthlyDraftContract.ToString("D") +
                "/monthly-utility-drafts/2026/13", null))
                Assert.Equal(HttpStatusCode.Forbidden, invalidPeriodDraft.StatusCode);
            Guid persistedDraftId;
            using (var persistedDraft = await clientB.PostAsync(draftCreateRoute, null))
            {
                var jsonText = await persistedDraft.Content.ReadAsStringAsync();
                Assert.True(persistedDraft.StatusCode is HttpStatusCode.OK or
                    HttpStatusCode.Created,
                    $"2G.3 persisted monthly utility draft: {persistedDraft.StatusCode}: {jsonText}");
                using var json = JsonDocument.Parse(jsonText);
                persistedDraftId = json.RootElement.GetProperty("id").GetGuid();
                Assert.Equal(monthlyDraftContract,
                    json.RootElement.GetProperty("contractId").GetGuid());
                Assert.Equal(monthlyDraftRoom,
                    json.RootElement.GetProperty("roomId").GetGuid());
                Assert.Equal(83000m,
                    json.RootElement.GetProperty("provisionalUtilityTotal").GetDecimal());
                Assert.Equal(2,
                    json.RootElement.GetProperty("items").GetArrayLength());
                Assert.False(json.RootElement.GetProperty("isIssued").GetBoolean());
                Assert.False(json.RootElement.GetProperty("hasRecognizedDebt").GetBoolean());
                Assert.DoesNotContain(tenantB.ToString("D"), jsonText,
                    StringComparison.OrdinalIgnoreCase);
            }
            var draftReadRoute = scopedContracts + "/" +
                monthlyDraftContract.ToString("D") + "/monthly-utility-drafts/" +
                persistedDraftId.ToString("D");
            using (var saved = await clientB.GetAsync(draftReadRoute))
            {
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                var body = await saved.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal(persistedDraftId, json.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(monthlyDraftContract,
                    json.RootElement.GetProperty("contractId").GetGuid());
                Assert.Equal(2, json.RootElement.GetProperty("items").GetArrayLength());
                Assert.Equal(83000m,
                    json.RootElement.GetProperty("provisionalUtilityTotal").GetDecimal());
                Assert.False(json.RootElement.GetProperty("isIssued").GetBoolean());
                Assert.False(json.RootElement.GetProperty("hasRecognizedDebt").GetBoolean());
                Assert.DoesNotContain(tenantB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var foreignDraftRead = await clientB.GetAsync(
                scopedContracts + "/" + contractA.ToString("D") +
                "/monthly-utility-drafts/" + persistedDraftId.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, foreignDraftRead.StatusCode);
            using (var missingDraftRead = await clientB.GetAsync(
                scopedContracts + "/" + monthlyDraftContract.ToString("D") +
                "/monthly-utility-drafts/" + Guid.NewGuid().ToString("D")))
                Assert.NotEqual(HttpStatusCode.OK, missingDraftRead.StatusCode);

            using (var duplicateDraft = await clientB.PostAsync(draftCreateRoute, null))
                Assert.NotEqual(HttpStatusCode.OK, duplicateDraft.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                Assert.Equal(1, await db.Invoices.CountAsync(x =>
                    x.Id == persistedDraftId &&
                    x.ContractId == monthlyDraftContract &&
                    x.BillingYear == 2026 && x.BillingMonth == 9));
                Assert.Equal(2, await db.InvoiceItems.CountAsync(x =>
                    x.InvoiceId == persistedDraftId));
                Assert.False(await db.IssuedInvoiceLedger.AnyAsync(x =>
                    x.ContractId == monthlyDraftContract));
                await uow.CompleteAsync();
            }

            // Regular recurring month: from a physically verified utility
            // draft to ONE immutable, root-reviewed financial candidate.
            // A candidate never changes draft Status or posts official debt.
            var monthlyCandidateRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-drafts/" + persistedDraftId.ToString("D") +
                "/candidate";
            var monthlyCandidatePayload = new
            {
                contractId = monthlyDraftContract,
                roomId = monthlyDraftRoom,
                billingYear = 2026,
                billingMonth = 9,
                paymentDueDate = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")))
                    .AddDays(7),
                dueDatePolicyReference = "APPROVED-MONTHLY-DUE-REVIEW",
                auxiliaryFeeDecision = 1,
                auxiliaryFeePolicyReference = "APPROVED-EXPLICIT-NO-MONTHLY-FEES",
                auxiliaryFeeReviewerId = root,
                auxiliaryCharges = Array.Empty<object>()
            };
            var foreignCandidate = await PostFinalAsync(
                clientB, monthlyCandidateRoute,
                JsonSerializer.Serialize(monthlyCandidatePayload));
            Assert.Equal(HttpStatusCode.Forbidden, foreignCandidate.Status);
            var candidateCreated = await PostFinalAsync(
                clientRoot, monthlyCandidateRoute,
                JsonSerializer.Serialize(monthlyCandidatePayload));
            Assert.True(candidateCreated.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Monthly candidate: {candidateCreated.Status}: {candidateCreated.Body}");
            Guid monthlyCandidateId;
            using (var json = JsonDocument.Parse(candidateCreated.Body))
            {
                monthlyCandidateId = json.RootElement.GetProperty("candidateId").GetGuid();
                Assert.Equal(persistedDraftId,
                    json.RootElement.GetProperty("draftInvoiceId").GetGuid());
                Assert.Equal(2_583_000m,
                    json.RootElement.GetProperty("frozenCandidateAmount").GetDecimal());
                Assert.False(json.RootElement.GetProperty("isOfficialReceivable").GetBoolean());
            }
            var duplicateCandidate = await PostFinalAsync(
                clientRoot, monthlyCandidateRoute,
                JsonSerializer.Serialize(monthlyCandidatePayload));
            Assert.Equal(HttpStatusCode.Forbidden, duplicateCandidate.Status);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var stored = await db.MonthlyInvoiceCandidates
                    .SingleAsync(x => x.Id == monthlyCandidateId);
                var proof = stored.VerifyStoredEvidence();
                Assert.Equal(2, proof.UtilityLines.Count);
                Assert.Equal(2_583_000m, stored.TotalAmount);
                Assert.False(stored.IsOfficialReceivable);
                Assert.Equal(1, await db.MonthlyInvoiceCandidates.CountAsync(
                    x => x.ContractId == monthlyDraftContract));
                Assert.False(await db.IssuedInvoiceLedger.AnyAsync(
                    x => x.ContractId == monthlyDraftContract));
                Assert.Equal(0, (int)(await db.Invoices.SingleAsync(
                    x => x.Id == persistedDraftId)).Status);
                await uow.CompleteAsync();
            }

            // The MONTHLY issuance workflow requires a different person
            // from the archive preparer to accept immutable terms.
            var monthlyReviewRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-candidates/" + monthlyCandidateId.ToString("D") +
                "/review";
            var monthlyIssueRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-candidates/" + monthlyCandidateId.ToString("D") +
                "/issue";
            var reviewPayload = new {
                accept = true,
                reason = "Independently audited meter readings rent and fees"
            };
            var selfReview = await PostFinalAsync(clientRoot,
                monthlyReviewRoute, JsonSerializer.Serialize(reviewPayload));
            Assert.Equal(HttpStatusCode.Forbidden, selfReview.Status);
            var acceptedReview = await PostFinalAsync(clientB,
                monthlyReviewRoute, JsonSerializer.Serialize(reviewPayload));
            Assert.True(acceptedReview.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Monthly approval: {acceptedReview.Status}: {acceptedReview.Body}");
            Guid monthlyReviewId;
            using (var json = JsonDocument.Parse(acceptedReview.Body))
            {
                monthlyReviewId = json.RootElement.GetProperty("reviewId").GetGuid();
                Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
                Assert.False(json.RootElement.GetProperty("isOfficialReceivable").GetBoolean());
            }
            var repeatedReview = await PostFinalAsync(clientB,
                monthlyReviewRoute, JsonSerializer.Serialize(reviewPayload));
            Assert.Equal(HttpStatusCode.Forbidden, repeatedReview.Status);

            var staffIssue = await PostFinalAsync(clientB, monthlyIssueRoute, "{}");
            Assert.Equal(HttpStatusCode.Forbidden, staffIssue.Status);
            var issuedMonth = await PostFinalAsync(
                clientRoot, monthlyIssueRoute, "{}");
            Assert.True(issuedMonth.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Monthly issuance: {issuedMonth.Status}: {issuedMonth.Body}");
            Guid monthlyIssuedId;
            using (var json = JsonDocument.Parse(issuedMonth.Body))
            {
                monthlyIssuedId = json.RootElement
                    .GetProperty("issuedInvoiceId").GetGuid();
                Assert.Equal(2_583_000m,
                    json.RootElement.GetProperty("amount").GetDecimal());
                Assert.False(json.RootElement
                    .GetProperty("isOfficialReceivable").GetBoolean());
                Assert.StartsWith("NTM-",
                    json.RootElement.GetProperty("invoiceNumber").GetString());
            }
            var repeatedIssue = await PostFinalAsync(
                clientRoot, monthlyIssueRoute, "{}");
            Assert.Equal(HttpStatusCode.Forbidden, repeatedIssue.Status);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var monthlySourceArchive = await db.MonthlyInvoiceCandidates
                    .SingleAsync(x => x.Id == monthlyCandidateId);
                var decision = await db.MonthlyInvoiceReviews
                    .SingleAsync(x => x.Id == monthlyReviewId);
                var month = await db.MonthlyIssuedInvoices
                    .SingleAsync(x => x.Id == monthlyIssuedId);
                month.VerifyAgainst(monthlySourceArchive, decision);
                Assert.Equal(monthlyDraftContract, month.ContractId);
                Assert.True(month.IsStagingIssued);
                Assert.False(month.IsOfficialReceivable);
                Assert.False(await db.IssuedInvoiceLedger.AnyAsync(
                    x => x.ContractId == monthlyDraftContract));
                Assert.Equal(0, (int)(await db.Invoices.SingleAsync(
                    x => x.Id == persistedDraftId)).Status);
                await uow.CompleteAsync();
            }

            var monthlyBalanceRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-issued/" + monthlyIssuedId.ToString("D") +
                "/balance";
            using (var balance = await clientB.GetAsync(monthlyBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, balance.StatusCode);
                using var json = JsonDocument.Parse(
                    await balance.Content.ReadAsStringAsync());
                Assert.Equal(2_583_000m, json.RootElement
                    .GetProperty("outstandingBeforeAdjustments").GetDecimal());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("completedPaymentsAmount").GetDecimal());
                Assert.Equal((int)BuildingManagement.Invoices
                    .IssuedInvoicePaymentStatus.Unpaid,
                    json.RootElement.GetProperty("paymentStatus").GetInt32());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
            }
            using (var foreignBalance = await clientB.GetAsync(
                FinalInvoices(contractA) + "/monthly-issued/" +
                monthlyIssuedId.ToString("D") + "/balance"))
                Assert.Equal(HttpStatusCode.Forbidden, foreignBalance.StatusCode);

            // Month-specific staged receipts use their own immutable
            // journal, not the FINAL invoice allocation table.
            var monthReceiptRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-issued/" + monthlyIssuedId.ToString("D") +
                "/manual-receipts";
            var monthPaidAtUtc = DateTime.UtcNow;
            // One bank transfer cannot fund a contract deposit and then
            // be counted as a separate invoice payment via a case variant.
            var reusedDepositOnMonth = await PostFinalAsync(
                clientRoot, monthReceiptRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB, amount = 10_000m,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "scratch-initial-deposit-2026",
                    bankEvidenceReference = "REUSED-DEPOSIT-BANK-PROOF-MONTH-001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, reusedDepositOnMonth.Status);

            var partialMonthEvidence = new {
                tenantId = tenantB,
                amount = 500_000m,
                paidAtUtc = monthPaidAtUtc,
                transactionReference = "SCRATCH-MONTH-RECEIPT-001",
                bankEvidenceReference = "MONTHLY-SIGNED-BANK-EVIDENCE-001"
            };
            var unauthorizedMonthReceipt = await PostFinalAsync(
                clientB, monthReceiptRoute,
                JsonSerializer.Serialize(partialMonthEvidence));
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedMonthReceipt.Status);

            var monthPartial = await PostFinalAsync(
                clientRoot, monthReceiptRoute,
                JsonSerializer.Serialize(partialMonthEvidence));
            Assert.True(monthPartial.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Monthly receipt: {monthPartial.Status}: {monthPartial.Body}");
            Guid monthPaymentId;
            Guid monthAllocationId;
            using (var json = JsonDocument.Parse(monthPartial.Body))
            {
                monthPaymentId = json.RootElement.GetProperty("paymentId").GetGuid();
                monthAllocationId = json.RootElement
                    .GetProperty("allocationJournalId").GetGuid();
                Assert.Equal(500_000m,
                    json.RootElement.GetProperty("stagedAmount").GetDecimal());
                Assert.False(json.RootElement
                    .GetProperty("isOfficialReceivableSettled").GetBoolean());
            }
            var repeatedMonth = await PostFinalAsync(
                clientRoot, monthReceiptRoute,
                JsonSerializer.Serialize(partialMonthEvidence));
            Assert.True(repeatedMonth.Status is HttpStatusCode.OK or
                HttpStatusCode.Created);
            using (var json = JsonDocument.Parse(repeatedMonth.Body))
            {
                Assert.Equal(monthPaymentId,
                    json.RootElement.GetProperty("paymentId").GetGuid());
                Assert.Equal(monthAllocationId,
                    json.RootElement.GetProperty("allocationJournalId").GetGuid());
            }
            using (var partialBalance = await clientB.GetAsync(monthlyBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, partialBalance.StatusCode);
                using var json = JsonDocument.Parse(
                    await partialBalance.Content.ReadAsStringAsync());
                Assert.Equal(500_000m, json.RootElement
                    .GetProperty("completedPaymentsAmount").GetDecimal());
                Assert.Equal(2_083_000m, json.RootElement
                    .GetProperty("outstandingBeforeAdjustments").GetDecimal());
                Assert.Equal((int)BuildingManagement.Invoices
                    .IssuedInvoicePaymentStatus.PartiallyPaid,
                    json.RootElement.GetProperty("paymentStatus").GetInt32());
                Assert.False(json.RootElement
                    .GetProperty("bankTransferIndependentlyVerified").GetBoolean());
            }
            var futureMonthlyDebt = await clientB.GetAsync(
                monthlyBalanceRoute + "?asOfDate=" +
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.UtcNow.AddDays(1),
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")))
                    .ToString("yyyy-MM-dd"));
            Assert.Equal(HttpStatusCode.Forbidden, futureMonthlyDebt.StatusCode);

            var alteredMonth = await PostFinalAsync(
                clientRoot, monthReceiptRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB,
                    amount = 500_001m, paidAtUtc = monthPaidAtUtc,
                    transactionReference = partialMonthEvidence.transactionReference,
                    bankEvidenceReference = partialMonthEvidence.bankEvidenceReference
                }));
            Assert.Equal(HttpStatusCode.Forbidden, alteredMonth.Status);

            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var payment = await db.Payments.SingleAsync(x => x.Id == monthPaymentId);
                var journal = await db.MonthlyInvoicePaymentAllocations
                    .SingleAsync(x => x.Id == monthAllocationId);
                journal.VerifySource(payment);
                Assert.Equal(monthlyIssuedId, journal.IssuedInvoiceId);
                Assert.False(journal.IsPostedDebtSettlement);
                Assert.False(await db.InvoicePaymentAllocationJournal.AnyAsync(
                    x => x.PaymentId == monthPaymentId));
                var summary = BuildingManagement.Invoices
                    .IssuedInvoicePaymentReconciler.Assess(
                        monthlyIssuedId, monthlyDraftContract,
                        2_583_000m, DateOnly.FromDateTime(
                            TimeZoneInfo.ConvertTimeFromUtc(
                                DateTime.UtcNow.AddDays(7),
                                TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))),
                        DateOnly.FromDateTime(
                            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                                TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))),
                        new[] { payment });
                Assert.Equal(500_000m, summary.CompletedPaymentsAmount);
                Assert.Equal(2_083_000m, summary.OutstandingBeforeAdjustments);
                Assert.Equal(BuildingManagement.Invoices.IssuedInvoicePaymentStatus.PartiallyPaid,
                    summary.Status);
                await uow.CompleteAsync();
            }
            // Full payment uses a SECOND bank reference; the balance
            // becomes paid but this remains an observed staging amount,
            // not independent bank confirmation or legal debt clearance.
            var monthRemaining = await PostFinalAsync(
                clientRoot, monthReceiptRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB, amount = 2_083_000m,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "SCRATCH-MONTH-FULL-REMAINING-001",
                    bankEvidenceReference = "MONTHLY-SIGNED-REMAINDER-PROOF-001"
                }));
            Assert.True(monthRemaining.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Monthly final receipt: {monthRemaining.Status}: {monthRemaining.Body}");
            using (var finalMonthlyBalance = await clientB.GetAsync(monthlyBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, finalMonthlyBalance.StatusCode);
                using var json = JsonDocument.Parse(
                    await finalMonthlyBalance.Content.ReadAsStringAsync());
                Assert.Equal(2_583_000m, json.RootElement
                    .GetProperty("completedPaymentsAmount").GetDecimal());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("outstandingBeforeAdjustments").GetDecimal());
                Assert.Equal(2, json.RootElement
                    .GetProperty("completedPaymentCount").GetInt32());
                Assert.Equal((int)BuildingManagement.Invoices
                    .IssuedInvoicePaymentStatus.Paid,
                    json.RootElement.GetProperty("paymentStatus").GetInt32());
                Assert.False(json.RootElement
                    .GetProperty("isOfficialDebtPosted").GetBoolean());
            }

            // Audited post-issuance correction: proposer and approver
            // are distinct, and the immutable ORIGINAL amount is never
            // overwritten even when the corrected balance changes.
            var monthAdjustmentsRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-issued/" + monthlyIssuedId.ToString("D") +
                "/adjustments";
            var debitRequest = new {
                signedDeltaAmount = 100_000m,
                reason = "Additional service charge signed by tenant and verified",
                sourceEvidenceReference = "SIGNED-RECURRING-SERVICE-ADDENDUM-001"
            };
            var staffAdjustment = await PostFinalAsync(
                clientB, monthAdjustmentsRoute,
                JsonSerializer.Serialize(debitRequest));
            Assert.Equal(HttpStatusCode.Forbidden, staffAdjustment.Status);
            var proposed = await PostFinalAsync(
                clientRoot, monthAdjustmentsRoute,
                JsonSerializer.Serialize(debitRequest));
            Assert.True(proposed.Status is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Monthly adjustment proposal: {proposed.Status}: {proposed.Body}");
            Guid monthlyAdjustmentId;
            using (var json = JsonDocument.Parse(proposed.Body))
            {
                monthlyAdjustmentId = json.RootElement.GetProperty("adjustmentId").GetGuid();
                Assert.True(json.RootElement.GetProperty("isDebitNote").GetBoolean());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
            }
            var reviewAdjustmentRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-adjustments/" + monthlyAdjustmentId.ToString("D") +
                "/review";
            var adjustmentDecision = new {
                accept = true,
                reason = "Independent check of signed customer service agreement"
            };
            var selfApprove = await PostFinalAsync(
                clientRoot, reviewAdjustmentRoute,
                JsonSerializer.Serialize(adjustmentDecision));
            Assert.Equal(HttpStatusCode.Forbidden, selfApprove.Status);
            using (var pendingAdjustmentBalance =
                await clientB.GetAsync(monthlyBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, pendingAdjustmentBalance.StatusCode);
                using var json = JsonDocument.Parse(
                    await pendingAdjustmentBalance.Content.ReadAsStringAsync());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("approvedSignedAdjustment").GetDecimal());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("outstandingAfterAdjustments").GetDecimal());
            }
            var approvedAdjustment = await PostFinalAsync(
                clientB, reviewAdjustmentRoute,
                JsonSerializer.Serialize(adjustmentDecision));
            Assert.True(approvedAdjustment.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Independent adjustment approval: {approvedAdjustment.Status}: {approvedAdjustment.Body}");
            Guid approvedAdjustmentDecisionId;
            using (var json = JsonDocument.Parse(approvedAdjustment.Body))
            {
                approvedAdjustmentDecisionId = json.RootElement.GetProperty("decisionId").GetGuid();
                Assert.Equal(100_000m,
                    json.RootElement.GetProperty("approvedSignedDeltaAmount").GetDecimal());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
            }
            var duplicateApproval = await PostFinalAsync(
                clientB, reviewAdjustmentRoute,
                JsonSerializer.Serialize(adjustmentDecision));
            Assert.Equal(HttpStatusCode.Forbidden, duplicateApproval.Status);
            using (var correctedBalance = await clientB.GetAsync(monthlyBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, correctedBalance.StatusCode);
                using var json = JsonDocument.Parse(
                    await correctedBalance.Content.ReadAsStringAsync());
                Assert.Equal(2_583_000m,
                    json.RootElement.GetProperty("issuedAmount").GetDecimal());
                Assert.Equal(100_000m,
                    json.RootElement.GetProperty("approvedSignedAdjustment").GetDecimal());
                Assert.Equal(2_683_000m,
                    json.RootElement.GetProperty("adjustedAmount").GetDecimal());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("outstandingBeforeAdjustments").GetDecimal());
                Assert.Equal(100_000m,
                    json.RootElement.GetProperty("outstandingAfterAdjustments").GetDecimal());
                Assert.Equal(1, json.RootElement
                    .GetProperty("approvedAdjustmentCount").GetInt32());
            }

            // A late credit below already-recorded money MUST be rejected,
            // preserving the debit adjustment and original source journal.
            var creditRequest = new {
                signedDeltaAmount = -200_000m,
                reason = "Proposed correction would exceed existing receipts",
                sourceEvidenceReference = "SIGNED-RECURRING-CREDIT-PROOF-002"
            };
            var creditProposed = await PostFinalAsync(
                clientRoot, monthAdjustmentsRoute,
                JsonSerializer.Serialize(creditRequest));
            Assert.True(creditProposed.Status is HttpStatusCode.OK or
                HttpStatusCode.Created);
            Guid monthlyCreditId;
            using (var json = JsonDocument.Parse(creditProposed.Body))
                monthlyCreditId = json.RootElement
                    .GetProperty("adjustmentId").GetGuid();
            var creditReviewRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-adjustments/" + monthlyCreditId.ToString("D") +
                "/review";
            var invalidCreditApproval = await PostFinalAsync(
                clientB, creditReviewRoute, JsonSerializer.Serialize(new {
                    accept = true,
                    reason = "Would erase previously captured payment funds"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, invalidCreditApproval.Status);
            var declinedCredit = await PostFinalAsync(
                clientB, creditReviewRoute, JsonSerializer.Serialize(new {
                    accept = false,
                    reason = "Credit declined because payment evidence exceeds new total"
                }));
            Assert.True(declinedCredit.Status is HttpStatusCode.OK or
                HttpStatusCode.Created);

            var differencePaid = await PostFinalAsync(
                clientRoot, monthReceiptRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB, amount = 100_000m,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "SCRATCH-MONTH-CORRECTION-PAID-001",
                    bankEvidenceReference = "BANK-SIGNED-MONTH-CORRECTION-RECEIPT-001"
                }));
            Assert.True(differencePaid.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Correction payment: {differencePaid.Status}: {differencePaid.Body}");
            using (var paidAdjusted = await clientB.GetAsync(monthlyBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, paidAdjusted.StatusCode);
                using var json = JsonDocument.Parse(
                    await paidAdjusted.Content.ReadAsStringAsync());
                Assert.Equal(2_683_000m,
                    json.RootElement.GetProperty("completedPaymentsAmount").GetDecimal());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("outstandingAfterAdjustments").GetDecimal());
                Assert.Equal(100_000m,
                    json.RootElement.GetProperty("approvedSignedAdjustment").GetDecimal());
                Assert.Equal((int)BuildingManagement.Invoices
                    .IssuedInvoicePaymentStatus.Paid,
                    json.RootElement.GetProperty("paymentStatus").GetInt32());
                Assert.False(json.RootElement
                    .GetProperty("isOfficialDebtPosted").GetBoolean());
            }
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var original = await db.MonthlyIssuedInvoices
                    .SingleAsync(x => x.Id == monthlyIssuedId);
                var accepted = await db.MonthlyInvoiceAdjustmentDecisions
                    .SingleAsync(x => x.Id == approvedAdjustmentDecisionId);
                Assert.Equal(2_583_000m, original.TotalAmount);
                Assert.Equal(100_000m, accepted.ApprovedSignedDeltaAmount);
                Assert.Equal(2, await db.MonthlyInvoiceAdjustmentDecisions
                    .CountAsync(x => x.IssuedInvoiceId == monthlyIssuedId));
                await uow.CompleteAsync();
            }

            var overMonth = await PostFinalAsync(
                clientRoot, monthReceiptRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB, amount = 2_083_001m,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "SCRATCH-MONTH-OVERPAY-001",
                    bankEvidenceReference = "MONTHLY-BANK-OVERPAY-EVIDENCE-001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, overMonth.Status);

            async Task<JsonDocument> PostValidFinanceAsync(
                HttpClient caller, string route, object payload)
            {
                var (status, body) = await PostFinalAsync(
                    caller, route, JsonSerializer.Serialize(payload));
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created,
                    $"Slice B E2E {route}: {status}, {body}");
                return JsonDocument.Parse(body);
            }

            var financeRoot = FinalInvoices(financeContract);
            using var prepared = await PostValidFinanceAsync(
                clientRoot,
                financeRoot + "/move-outs/" + financeMoveOut.ToString("D") +
                    "/prepare", new { });
            var evidenceId = prepared.RootElement.GetProperty("id").GetGuid();
            var evidenceHash = prepared.RootElement
                .GetProperty("sha256").GetString();
            Assert.Equal(financeInvoiceDraftId,
                prepared.RootElement.GetProperty("draftInvoiceId").GetGuid());
            Assert.NotNull(evidenceHash);

            using var reviewSubmission = await PostValidFinanceAsync(
                clientRoot, financeRoot + "/evidence/" +
                    evidenceId.ToString("D") + "/reviews",
                new { note = "Independent scoped finance review" });
            var reviewId = reviewSubmission.RootElement.GetGuid();
            Assert.NotEqual(Guid.Empty, reviewId);

            // The submitter may NOT self-review, while the independent
            // B operator may review current, matching evidence.
            var ownReview = await PostFinalAsync(
                clientRoot, financeRoot + "/reviews/" +
                    reviewId.ToString("D") + "/decide",
                """{"acceptForDesign":true,"reason":"Cannot approve my own submission"}""");
            Assert.Equal(HttpStatusCode.Forbidden, ownReview.Status);
            using var reviewDecision = await PostValidFinanceAsync(
                clientB, financeRoot + "/reviews/" +
                    reviewId.ToString("D") + "/decide",
                new { acceptForDesign = true,
                    reason = "Independent meter, rent and policy evidence verified" });
            Assert.Equal(reviewId, reviewDecision.RootElement.GetGuid());

            using var issued = await PostValidFinanceAsync(
                clientRoot, financeRoot + "/evidence/" +
                    evidenceId.ToString("D") + "/issue-candidate",
                new {
                    draftInvoiceId = financeInvoiceDraftId,
                    contractId = financeContract,
                    roomId = financeRoom,
                    evidenceSha256 = evidenceHash,
                    paymentDueDate = "2026-11-05",
                    dueDatePolicyReference = "AGREED-DUE-DATE-POLICY-2026",
                    auxiliaryFeeDecision = 1,
                    auxiliaryFeePolicyReference = "NO-AUXILIARY-FEES-POLICY-2026",
                    auxiliaryFeeReviewerId = root,
                    auxiliaryCharges = Array.Empty<object>()
                });
            var financeIssuedId = issued.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(finalBill,
                issued.RootElement.GetProperty("amount").GetDecimal());

            // Phase 2G.3: before a receipt, the scoped issued ledger must
            // report the full amount outstanding and UNPAID. It is not a
            // production debt or a verified external bank balance.
            var financeBalanceRoute = scopedContracts + "/" +
                financeContract.ToString("D") + "/issued-invoices/" +
                financeIssuedId.ToString("D") + "/balance";
            using (var unpaidResponse = await clientB.GetAsync(financeBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, unpaidResponse.StatusCode);
                var body = await unpaidResponse.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal((int)BuildingManagement.Invoices.IssuedInvoicePaymentStatus.Unpaid,
                    json.RootElement.GetProperty("observedPaymentStatus").GetInt32());
                Assert.Equal(finalBill,
                    json.RootElement.GetProperty("observedOutstandingBeforeAdjustments").GetDecimal());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("observedCompletedPaymentsAmount").GetDecimal());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
                Assert.False(json.RootElement.GetProperty("bankTransferIndependentlyVerified").GetBoolean());
                Assert.DoesNotContain(tenantB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var foreignBalance = await clientB.GetAsync(
                scopedContracts + "/" + contractA.ToString("D") +
                "/issued-invoices/" + financeIssuedId.ToString("D") + "/balance"))
                Assert.Equal(HttpStatusCode.Forbidden, foreignBalance.StatusCode);
            using (var wrongInvoice = await clientB.GetAsync(
                scopedContracts + "/" + financeContract.ToString("D") +
                "/issued-invoices/" + Guid.NewGuid().ToString("D") + "/balance"))
                Assert.Equal(HttpStatusCode.Forbidden, wrongInvoice.StatusCode);

            // Phase 2G.3 contract-level debt portfolio must be server scoped,
            // show the same unpaid issued invoice, and never leak Tenant PII.
            var portfolioRoute = scopedContracts + "/" +
                financeContract.ToString("D") + "/issued-invoices/debt";
            // Historical debt must not include an invoice which had not
            // yet been issued. A future as-of query must never invent
            // forecasts or disclose unrestricted ledger information.
            var localBusinessToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
            var previousBusinessDay = localBusinessToday.AddDays(-1);
            using (var beforeIssuance = await clientB.GetAsync(
                portfolioRoute + "?asOfDate=" +
                    previousBusinessDay.ToString("yyyy-MM-dd")))
                Assert.Equal(HttpStatusCode.Forbidden, beforeIssuance.StatusCode);
            using (var futureDebt = await clientB.GetAsync(
                portfolioRoute + "?asOfDate=" +
                    localBusinessToday.AddDays(1).ToString("yyyy-MM-dd")))
                Assert.Equal(HttpStatusCode.Forbidden, futureDebt.StatusCode);

            using (var unpaidPortfolio = await clientB.GetAsync(portfolioRoute))
            {
                Assert.Equal(HttpStatusCode.OK, unpaidPortfolio.StatusCode);
                var body = await unpaidPortfolio.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal(1, json.RootElement.GetProperty("issuedInvoiceCount").GetInt32());
                Assert.Equal(finalBill,
                    json.RootElement.GetProperty("observedOutstandingBeforeAdjustments").GetDecimal());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("observedCompletedPaymentsAmount").GetDecimal());
                var row = json.RootElement.GetProperty("invoices")[0];
                Assert.Equal(financeIssuedId, row.GetProperty("issuedInvoiceId").GetGuid());
                Assert.Equal((int)BuildingManagement.Invoices.IssuedInvoicePaymentStatus.Unpaid,
                    row.GetProperty("observedPaymentStatus").GetInt32());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
                Assert.DoesNotContain(tenantB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var foreignPortfolio = await clientB.GetAsync(
                scopedContracts + "/" + contractA.ToString("D") +
                "/issued-invoices/debt"))
                Assert.Equal(HttpStatusCode.Forbidden, foreignPortfolio.StatusCode);

            // One valid PARTIAL payment must lower only the OBSERVED debt.
            // It does not authorize a deposit refund or complete move-out.
            const decimal partialAmount = 500000m;
            var partialPaidAtUtc = DateTime.UtcNow;
            var partialEvidence = new {
                tenantId = tenantB, amount = partialAmount,
                paidAtUtc = partialPaidAtUtc,
                transactionReference = "SCRATCH-BANK-PARTIAL-0001",
                bankEvidenceReference = "SCRATCH-PARTIAL-SIGNED-PROOF-0001"
            };
            var partialRoute = financeRoot + "/issued/" +
                financeIssuedId.ToString("D") + "/manual-receipts";
            var reusedDepositOnFinal = await PostFinalAsync(
                clientRoot, partialRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB, amount = 10_000m,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "scratch-initial-deposit-2026",
                    bankEvidenceReference = "REUSED-DEPOSIT-BANK-PROOF-FINAL-001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, reusedDepositOnFinal.Status);

            using var partiallyPaid = await PostValidFinanceAsync(
                clientRoot, partialRoute, partialEvidence);
            var partialReceiptId = partiallyPaid.RootElement
                .GetProperty("paymentId").GetGuid();
            var partialJournalId = partiallyPaid.RootElement
                .GetProperty("allocationJournalId").GetGuid();

            // Replaying the *same* bank evidence must return the exact
            // original IDs. The receipt/journal are append-only and must
            // not be duplicated, even when the HTTP caller retries.
            using (var retry = await PostValidFinanceAsync(
                clientRoot, partialRoute, partialEvidence))
            {
                Assert.Equal(partialReceiptId,
                    retry.RootElement.GetProperty("paymentId").GetGuid());
                Assert.Equal(partialJournalId,
                    retry.RootElement.GetProperty("allocationJournalId").GetGuid());
            }

            // PostgreSQL enforces unique bank evidence after trimming
            // and case folding, and the staging adapter resolves the
            // historical original rather than inserting a second receipt.
            using (var caseVariant = await PostValidFinanceAsync(
                clientRoot, partialRoute, new {
                    tenantId = tenantB, amount = partialAmount,
                    paidAtUtc = partialPaidAtUtc,
                    transactionReference = "  scratch-bank-partial-0001  ",
                    bankEvidenceReference = partialEvidence.bankEvidenceReference
                }))
            {
                Assert.Equal(partialReceiptId,
                    caseVariant.RootElement.GetProperty("paymentId").GetGuid());
                Assert.Equal(partialJournalId,
                    caseVariant.RootElement.GetProperty("allocationJournalId").GetGuid());
            }

            // Official 2G.3 posting never mutates the original
            // issued bill. Unreviewed manually recorded receipts are NOT
            // official debt settlement, despite their Completed status.
            var monthlyPostRoute = FinalInvoices(monthlyDraftContract) +
                "/monthly-issued/" + monthlyIssuedId.ToString("D") +
                "/post-receivable";
            var staffMonthPosting = await PostFinalAsync(
                clientB, monthlyPostRoute, "{}");
            Assert.Equal(HttpStatusCode.Forbidden, staffMonthPosting.Status);
            var monthPosted = await PostFinalAsync(
                clientRoot, monthlyPostRoute, "{}");
            Assert.True(monthPosted.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Monthly posting: {monthPosted.Status}: {monthPosted.Body}");
            Guid monthPostingId;
            using (var json = JsonDocument.Parse(monthPosted.Body))
            {
                monthPostingId = json.RootElement.GetProperty("postingId").GetGuid();
                Assert.True(json.RootElement.GetProperty(
                    "isOfficialDebtPosted").GetBoolean());
            }
            var officialMonthBalance = FinalInvoices(monthlyDraftContract) +
                "/receivables/" + monthPostingId.ToString("D") + "/balance";
            using (var beforeReview = await clientB.GetAsync(officialMonthBalance))
            {
                Assert.Equal(HttpStatusCode.OK, beforeReview.StatusCode);
                using var json = JsonDocument.Parse(
                    await beforeReview.Content.ReadAsStringAsync());
                Assert.Equal(2_683_000m, json.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("independentlyReviewedManualPayments").GetDecimal());
                Assert.False(json.RootElement
                    .GetProperty("isExternallyVerifiedBankTransfer").GetBoolean());
            }
            Guid[] monthJournalIds;
            using (var officialScope = app.Services.CreateScope())
            {
                using var officialUow = officialScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var officialDb = await officialScope.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                monthJournalIds = await officialDb.MonthlyInvoicePaymentAllocations
                    .AsNoTracking().Where(x => x.IssuedInvoiceId == monthlyIssuedId)
                    .Select(x => x.Id).ToArrayAsync();
                await officialUow.CompleteAsync();
            }
            Assert.Equal(3, monthJournalIds.Length);
            foreach (var journalId in monthJournalIds)
            {
                var reviewRoute = FinalInvoices(monthlyDraftContract) +
                    "/receivables/" + monthPostingId.ToString("D") +
                    "/allocations/" + journalId.ToString("D") + "/review";
                var reviewBody = JsonSerializer.Serialize(new {
                    accept = true,
                    bankStatementEvidenceReference = "INDEPENDENT-BANK-STATEMENT-" +
                        journalId.ToString("N"),
                    reason = "Bank statement manually checked against immutable receipt"
                });
                var selfOfficialReceiptReview = await PostFinalAsync(
                    clientRoot, reviewRoute, reviewBody);
                Assert.Equal(HttpStatusCode.Forbidden, selfOfficialReceiptReview.Status);
                var approved = await PostFinalAsync(
                    clientB, reviewRoute, reviewBody);
                Assert.True(approved.Status is HttpStatusCode.OK or
                    HttpStatusCode.Created,
                    $"Monthly review: {approved.Status} {approved.Body}");
            }
            using (var officiallyPaid = await clientB.GetAsync(officialMonthBalance))
            {
                Assert.Equal(HttpStatusCode.OK, officiallyPaid.StatusCode);
                using var json = JsonDocument.Parse(
                    await officiallyPaid.Content.ReadAsStringAsync());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
                Assert.Equal(2_683_000m, json.RootElement
                    .GetProperty("independentlyReviewedManualPayments").GetDecimal());
                Assert.Equal(3, json.RootElement
                    .GetProperty("acceptedPaymentCount").GetInt32());
            }
            using (var monthPortfolio = await clientB.GetAsync(
                FinalInvoices(monthlyDraftContract) + "/official-debt"))
            {
                Assert.Equal(HttpStatusCode.OK, monthPortfolio.StatusCode);
                using var json = JsonDocument.Parse(
                    await monthPortfolio.Content.ReadAsStringAsync());
                Assert.Equal(1, json.RootElement
                    .GetProperty("invoiceCount").GetInt32());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
            }

            // A reused bank reference with changed money, tenant,
            // timestamp, or evidence is NOT an idempotent retry.
            var contradictory = await PostFinalAsync(
                clientRoot, partialRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB,
                    amount = partialAmount + 1m,
                    paidAtUtc = partialPaidAtUtc,
                    transactionReference = partialEvidence.transactionReference,
                    bankEvidenceReference = partialEvidence.bankEvidenceReference
                }));
            Assert.Equal(HttpStatusCode.Forbidden, contradictory.Status);

            var changedProof = await PostFinalAsync(
                clientRoot, partialRoute, JsonSerializer.Serialize(new {
                    tenantId = tenantB,
                    amount = partialAmount,
                    paidAtUtc = partialPaidAtUtc,
                    transactionReference = partialEvidence.transactionReference,
                    bankEvidenceReference = "DIFFERENT-SIGNED-RECEIPT-001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, changedProof.Status);
            using (var partialBalance = await clientB.GetAsync(financeBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, partialBalance.StatusCode);
                using var json = JsonDocument.Parse(
                    await partialBalance.Content.ReadAsStringAsync());
                Assert.Equal((int)BuildingManagement.Invoices.IssuedInvoicePaymentStatus.PartiallyPaid,
                    json.RootElement.GetProperty("observedPaymentStatus").GetInt32());
                Assert.Equal(partialAmount,
                    json.RootElement.GetProperty("observedCompletedPaymentsAmount").GetDecimal());
                Assert.Equal(finalBill - partialAmount,
                    json.RootElement.GetProperty("observedOutstandingBeforeAdjustments").GetDecimal());
            }
            using (var partialPortfolio = await clientB.GetAsync(portfolioRoute))
            {
                Assert.Equal(HttpStatusCode.OK, partialPortfolio.StatusCode);
                var body = await partialPortfolio.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal(partialAmount,
                    json.RootElement.GetProperty("observedCompletedPaymentsAmount").GetDecimal());
                Assert.Equal(finalBill - partialAmount,
                    json.RootElement.GetProperty("observedOutstandingBeforeAdjustments").GetDecimal());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
                Assert.DoesNotContain("SCRATCH-BANK-PARTIAL-0001", body);
            }

            // This receipt is a MANUAL bank-evidence attestation; test data
            // never initiates an actual bank transfer or payment gateway.
            using var paid = await PostValidFinanceAsync(
                clientRoot, financeRoot + "/issued/" +
                    financeIssuedId.ToString("D") + "/manual-receipts",
                new {
                    tenantId = tenantB, amount = finalBill - partialAmount,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "SCRATCH-BANK-FINAL-BILL-0001",
                    bankEvidenceReference = "SCRATCH-SIGNED-RECEIPT-0001"
                });
            var receiptId = paid.RootElement.GetProperty("paymentId").GetGuid();
            Assert.Equal(financeIssuedId,
                paid.RootElement.GetProperty("issuedInvoiceId").GetGuid());

            using (var paidResponse = await clientB.GetAsync(financeBalanceRoute))
            {
                Assert.Equal(HttpStatusCode.OK, paidResponse.StatusCode);
                var body = await paidResponse.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal((int)BuildingManagement.Invoices.IssuedInvoicePaymentStatus.Paid,
                    json.RootElement.GetProperty("observedPaymentStatus").GetInt32());
                Assert.Equal(finalBill,
                    json.RootElement.GetProperty("observedCompletedPaymentsAmount").GetDecimal());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("observedOutstandingBeforeAdjustments").GetDecimal());
                Assert.Equal(2,
                    json.RootElement.GetProperty("completedPaymentCount").GetInt32());
                Assert.False(json.RootElement.GetProperty("isOfficialDebtPosted").GetBoolean());
                Assert.False(json.RootElement.GetProperty("bankTransferIndependentlyVerified").GetBoolean());
                Assert.DoesNotContain("SCRATCH-BANK-FINAL-BILL-0001", body);
            }

            using (var paidPortfolio = await clientB.GetAsync(portfolioRoute))
            {
                Assert.Equal(HttpStatusCode.OK, paidPortfolio.StatusCode);
                var body = await paidPortfolio.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.Equal(1,
                    json.RootElement.GetProperty("issuedInvoiceCount").GetInt32());
                Assert.Equal(finalBill,
                    json.RootElement.GetProperty("observedIssuedAmount").GetDecimal());
                Assert.Equal(finalBill,
                    json.RootElement.GetProperty("observedCompletedPaymentsAmount").GetDecimal());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("observedOutstandingBeforeAdjustments").GetDecimal());
                Assert.Equal((int)BuildingManagement.Invoices.IssuedInvoicePaymentStatus.Paid,
                    json.RootElement.GetProperty("invoices")[0]
                        .GetProperty("observedPaymentStatus").GetInt32());
                Assert.False(json.RootElement.GetProperty("bankTransferIndependentlyVerified").GetBoolean());
                Assert.DoesNotContain(tenantB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SCRATCH-BANK-FINAL-BILL-0001", body);
                Assert.DoesNotContain("SCRATCH-BANK-PARTIAL-0001", body);
            }

            // Duplicate allocation cannot silently overpay a final bill.
            var duplicate = await PostFinalAsync(clientRoot,
                financeRoot + "/issued/" + financeIssuedId.ToString("D") +
                    "/manual-receipts",
                JsonSerializer.Serialize(new {
                    tenantId = tenantB, amount = 1m,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "SCRATCH-BANK-OVERPAY-0001",
                    bankEvidenceReference = "SCRATCH-OVERPAY-PROOF-0001"
                }));
            Assert.NotEqual(HttpStatusCode.OK, duplicate.Status);

            // Final move-out bill is also postable as a distinct official
            // receivable. Two original manual receipts need independent
            // bank-statement review before they count as settlement.
            var finalPostingRoute = financeRoot + "/issued/" +
                financeIssuedId.ToString("D") + "/post-receivable";
            var postedFinal = await PostFinalAsync(
                clientRoot, finalPostingRoute, "{}");
            Assert.True(postedFinal.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Final posting: {postedFinal.Status} {postedFinal.Body}");
            Guid officialFinalPostingId;
            using (var json = JsonDocument.Parse(postedFinal.Body))
                officialFinalPostingId = json.RootElement
                    .GetProperty("postingId").GetGuid();
            var officialFinalBalance = financeRoot + "/receivables/" +
                officialFinalPostingId.ToString("D") + "/balance";
            using (var unpaidFinal = await clientB.GetAsync(officialFinalBalance))
            {
                Assert.Equal(HttpStatusCode.OK, unpaidFinal.StatusCode);
                using var json = JsonDocument.Parse(
                    await unpaidFinal.Content.ReadAsStringAsync());
                Assert.Equal(finalBill, json.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
            }
            // Fully staged payments do not suffice for CLOSEOUT once
            // an official receivable exists. Independent manual review is
            // required before the final bill is considered settled.
            var earlyOfficialCloseout = await PostFinalAsync(
                clientRoot,
                financeRoot + "/move-outs/" + financeMoveOut.ToString("D") +
                    "/complete",
                JsonSerializer.Serialize(new {
                    forfeitAmount = 0m,
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = DateTime.UtcNow,
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, earlyOfficialCloseout.Status);
            // 2G.4 cannot approve a refund while official payment review
            // is missing, even though the staging journal shows paid.
            var depositApproveRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/approve";
            var depositPlanBody = JsonSerializer.Serialize(new {
                forfeitAmount = 0m,
                depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026"
            });
            var earlyDepositPlan = await PostFinalAsync(
                clientRoot, depositApproveRoute, depositPlanBody);
            Assert.Equal(HttpStatusCode.Forbidden, earlyDepositPlan.Status);

            Guid[] finalJournalIds;
            using (var finalScope = app.Services.CreateScope())
            {
                using var finalUow = finalScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var finalDb = await finalScope.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                finalJournalIds = await finalDb.InvoicePaymentAllocationJournal
                    .AsNoTracking().Where(x => x.IssuedInvoiceId == financeIssuedId)
                    .Select(x => x.Id).ToArrayAsync();
                await finalUow.CompleteAsync();
            }
            Assert.Equal(2, finalJournalIds.Length);
            foreach (var journalId in finalJournalIds)
            {
                var route = financeRoot + "/receivables/" +
                    officialFinalPostingId.ToString("D") +
                    "/allocations/" + journalId.ToString("D") + "/review";
                var response = await PostFinalAsync(clientB, route,
                    JsonSerializer.Serialize(new {
                        accept = true,
                        bankStatementEvidenceReference =
                            "MANUALLY-RECONCILED-BANK-STATEMENT-" + journalId.ToString("N"),
                        reason = "Independent review confirmed original bank statement"
                    }));
                Assert.True(response.Status is HttpStatusCode.OK or
                    HttpStatusCode.Created,
                    $"Final payment review: {response.Status} {response.Body}");
            }
            using (var settledFinal = await clientB.GetAsync(officialFinalBalance))
            {
                Assert.Equal(HttpStatusCode.OK, settledFinal.StatusCode);
                using var json = JsonDocument.Parse(
                    await settledFinal.Content.ReadAsStringAsync());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("outstandingAmount").GetDecimal());
                Assert.Equal(finalBill, json.RootElement
                    .GetProperty("independentlyReviewedManualPayments").GetDecimal());
                Assert.False(json.RootElement
                    .GetProperty("isExternallyVerifiedBankTransfer").GetBoolean());
            }

            // Phase 2G.4 is a DISTINCT owner decision before 2G.5.
            // It records intended refund/forfeit but does not send money,
            // complete checklists or mutate Room and Contract.
            var staffDepositDecision = await PostFinalAsync(
                clientB, depositApproveRoute, depositPlanBody);
            Assert.Equal(HttpStatusCode.Forbidden, staffDepositDecision.Status);
            var unjustifiedForfeit = await PostFinalAsync(
                clientRoot, depositApproveRoute, JsonSerializer.Serialize(new {
                    forfeitAmount = 250_000m,
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, unjustifiedForfeit.Status);
            var disputeRoot = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/debt-disputes";
            var disputeCreated = await PostFinalAsync(
                clientB, disputeRoot, JsonSerializer.Serialize(new {
                    postingId = officialFinalPostingId,
                    reason = "Tenant disputes the final invoice charge",
                    evidenceReference = "SIGNED-TENANT-DISPUTE-DOCUMENT-0001"
                }));
            Assert.True(disputeCreated.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Debt dispute create: {disputeCreated.Status} {disputeCreated.Body}");
            var disputeId = Guid.Parse(disputeCreated.Body.Trim('"'));
            var whileDisputed = await PostFinalAsync(
                clientRoot, depositApproveRoute, depositPlanBody);
            Assert.Equal(HttpStatusCode.Forbidden, whileDisputed.Status);
            using(var active = await clientB.GetAsync(
                financeRoot + "/move-outs/" + financeMoveOut.ToString("D") +
                "/deposit-settlement/debt-dispute-status"))
            {
                Assert.Equal(HttpStatusCode.OK, active.StatusCode);
                using var json = JsonDocument.Parse(
                    await active.Content.ReadAsStringAsync());
                Assert.Equal(1, json.RootElement.GetProperty("openCount").GetInt64());
                Assert.False(json.RootElement
                    .GetProperty("canConsiderDepositOffset").GetBoolean());
            }
            var resolutionRoute = disputeRoot + "/" +
                disputeId.ToString("D") + "/resolve";
            var resolutionBody = JsonSerializer.Serialize(new {
                debtAccepted = true,
                signedOwnerResolutionReference =
                    "SIGNED-OWNER-RECONCILED-DEBT-0001"
            });
            var staffRelease = await PostFinalAsync(
                clientB, resolutionRoute, resolutionBody);
            Assert.Equal(HttpStatusCode.Forbidden, staffRelease.Status);
            serverErrors.Clear();
            var ownerRelease = await PostFinalAsync(
                clientRoot, resolutionRoute, resolutionBody);
            Assert.True(ownerRelease.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Owner dispute resolution: {ownerRelease.Status} {ownerRelease.Body}. " +
                $"Test-server diagnostics (isolated DB only): {serverErrors.Describe()}");
            // Positive cross-clock regression: the immutable Owner event
            // must be timestamped from the SAME PostgreSQL clock as its
            // signed dispute trigger, never the host OS wall clock.
            await using (var clockDb = new NpgsqlConnection(buildingCs))
            {
                await clockDb.OpenAsync();
                await using var check = new NpgsqlCommand(
                    """
                    SELECT r."ResolvedAtUtc" >= d."OpenedAtUtc"
                       AND r."ResolvedAtUtc" <= clock_timestamp()
                       AND r."DebtAccepted"=TRUE
                      FROM "BuildingManagementContractDepositDebtDisputeResolutions" r
                      JOIN "BuildingManagementContractDepositDebtDisputes" d
                        ON d."Id"=r."DisputeId"
                     WHERE r."DisputeId"=@dispute
                    """, clockDb);
                check.Parameters.AddWithValue("dispute",disputeId);
                Assert.True(await check.ExecuteScalarAsync() is true,
                    "Owner resolution must be persistently signed and based on database clock.");
            }
            var decisionResponse = await PostFinalAsync(
                clientRoot, depositApproveRoute, depositPlanBody);
            Assert.True(decisionResponse.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Deposit decision: {decisionResponse.Status} {decisionResponse.Body}");
            Guid decisionId;
            using (var json = JsonDocument.Parse(decisionResponse.Body))
            {
                decisionId = json.RootElement.GetProperty("decisionId").GetGuid();
                Assert.Equal(monthlyRent, json.RootElement
                    .GetProperty("approvedRefundAmount").GetDecimal());
                Assert.Equal(0m, json.RootElement
                    .GetProperty("approvedDebtOffsetAmount").GetDecimal());
                Assert.False(json.RootElement
                    .GetProperty("isRefundExecuted").GetBoolean());
            }
            var repeatedDecision = await PostFinalAsync(
                clientRoot, depositApproveRoute, depositPlanBody);
            Assert.True(repeatedDecision.Status is HttpStatusCode.OK or
                HttpStatusCode.Created);
            using (var json = JsonDocument.Parse(repeatedDecision.Body))
                Assert.Equal(decisionId,
                    json.RootElement.GetProperty("decisionId").GetGuid());
            var conflictingDecision = await PostFinalAsync(
                clientRoot, depositApproveRoute, JsonSerializer.Serialize(new {
                    forfeitAmount = 250_000m,
                    damageReason = "Documented broken window glass",
                    damageEvidenceReference = "SIGNED-DAMAGE-REPORT-001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, conflictingDecision.Status);
            // Negative HTTP/PG proof: independent bank review cannot
            // certify a transfer before an Owner-signed payee instruction.
            var earlyReviewRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/refund-bank-review";
            var beforePayeeApproval = await PostFinalAsync(
                clientB, earlyReviewRoute, JsonSerializer.Serialize(new {
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "SCRATCH-REFUND-BEFORE-PAYEE-0001",
                    bankStatementEvidenceReference =
                        "SIGNED-BANK-BEFORE-PAYEE-REVIEW-0001",
                    signedIndependentReviewReference =
                        "SIGNED-REVIEW-BEFORE-PAYEE-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, beforePayeeApproval.Status);

            // D04: destination must be signed by the Owner, with an
            // explicit statutory-holiday calendar, before bank review.
            var destinationRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/refund-destination";
            var destinationBody = JsonSerializer.Serialize(new {
                beneficiaryName = "Nguyen Van A",
                bankName = "Isolated Test Bank",
                accountNumber = "123456789012",
                signedDestinationReference = "SIGNED-BENEFICIARY-APPROVAL-0001",
                holidayCalendarReference = "SIGNED-VIETNAM-HOLIDAYS-2026",
                statutoryHolidays = Array.Empty<string>()
            });
            var unauthorizedPayee = await PostFinalAsync(
                clientB, destinationRoute, destinationBody);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedPayee.Status);
            var approvedDestination = await PostFinalAsync(
                clientRoot, destinationRoute, destinationBody);
            Assert.True(approvedDestination.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Approved destination: {approvedDestination.Status} {approvedDestination.Body}");
            var sameDestination = await PostFinalAsync(
                clientRoot, destinationRoute, destinationBody);
            Assert.Equal(approvedDestination.Body, sameDestination.Body);
            // The reference alone is insufficient. Changing the signed
            // statutory holiday dates must not be an idempotent replay.
            var changedHoliday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")))
                .AddDays(1);
            var changedCalendar = await PostFinalAsync(
                clientRoot, destinationRoute, JsonSerializer.Serialize(new {
                    beneficiaryName = "Nguyen Van A",
                    bankName = "Isolated Test Bank",
                    accountNumber = "123456789012",
                    signedDestinationReference = "SIGNED-BENEFICIARY-APPROVAL-0001",
                    holidayCalendarReference = "SIGNED-VIETNAM-HOLIDAYS-2026",
                    statutoryHolidays = new [] { changedHoliday.ToString("yyyy-MM-dd") }
                }));
            Assert.Equal(HttpStatusCode.Forbidden, changedCalendar.Status);

            var conflictingDestination = await PostFinalAsync(
                clientRoot, destinationRoute, JsonSerializer.Serialize(new {
                    beneficiaryName = "Different Recipient",
                    bankName = "Isolated Test Bank",
                    accountNumber = "000000000000",
                    signedDestinationReference = "SIGNED-BENEFICIARY-APPROVAL-0002",
                    holidayCalendarReference = "SIGNED-VIETNAM-HOLIDAYS-2026",
                    statutoryHolidays = Array.Empty<string>()
                }));
            Assert.NotEqual(HttpStatusCode.OK, conflictingDestination.Status);
            var failureRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/refund-failed-transfer";
            var failedTransfer = await PostFinalAsync(
                clientRoot, failureRoute, JsonSerializer.Serialize(new {
                    bankReference = "SCRATCH-FAILED-TRANSFER-0001",
                    reason = "Bank returned transfer; recipient unreachable",
                    bankFailureEvidenceReference =
                        "BANK-FAILURE-RETURNED-REFUND-0001"
                }));
            Assert.True(failedTransfer.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Failed bank trace: {failedTransfer.Status} {failedTransfer.Body}");
            var refundStatusRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/refund-operating-status";
            using (var pending = await clientB.GetAsync(refundStatusRoute))
            {
                Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
                using var json = JsonDocument.Parse(
                    await pending.Content.ReadAsStringAsync());
                Assert.Equal(3, json.RootElement.GetProperty("status").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("failedTransferCount").GetInt64());
                Assert.False(json.RootElement.GetProperty("isIndependentlyVerified").GetBoolean());
            }
            var readPlanRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") + "/deposit-settlement";
            using (var readPlan = await clientB.GetAsync(readPlanRoute))
            {
                Assert.Equal(HttpStatusCode.OK, readPlan.StatusCode);
                using var json = JsonDocument.Parse(
                    await readPlan.Content.ReadAsStringAsync());
                Assert.Equal(decisionId,
                    json.RootElement.GetProperty("decisionId").GetGuid());
            }
            using (var wrongBuildingPlan = await clientB.GetAsync(
                FinalInvoices(contractA) + "/move-outs/" +
                financeMoveOut.ToString("D") + "/deposit-settlement"))
                Assert.Equal(HttpStatusCode.Forbidden, wrongBuildingPlan.StatusCode);
            using (var checkDecisionOnly = app.Services.CreateScope())
            {
                using var checkUow = checkDecisionOnly.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var checkDb = await checkDecisionOnly.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Active,
                    (await checkDb.Contracts.SingleAsync(x => x.Id == financeContract)).Status);
                Assert.False(await checkDb.DepositCloseoutApprovals.AsNoTracking()
                    .AnyAsync(x => x.ContractId == financeContract));
                Assert.False(await checkDb.DepositSettlements.AsNoTracking()
                    .AnyAsync(x => x.SourceContractId == financeContract &&
                        (x.Type == BuildingManagement.DepositSettlements
                            .DepositSettlementType.Refund ||
                         x.Type == BuildingManagement.DepositSettlements
                            .DepositSettlementType.Forfeit)));
                await checkUow.CompleteAsync();
            }

            // A signed statement must not exist before actual approval.
            var signedCloseoutRoute = scopedContracts + "/" +
                financeContract.ToString("D") + "/move-out-requests/" +
                financeMoveOut.ToString("D") + "/closeout";
            using (var beforeCloseout = await clientB.GetAsync(signedCloseoutRoute))
                Assert.Equal(HttpStatusCode.Forbidden, beforeCloseout.StatusCode);

            // A plain SQL UPDATE must not bypass the authenticated
            // Phase 2G.5 handover, even AFTER a deposit plan was approved.
            await using (var bypass = new NpgsqlConnection(buildingCs))
            {
                await bypass.OpenAsync();
                await using (var directContractEnd = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementContracts"
                       SET "Status" = 3, "ActualEndedAt" = now(),
                           "EndReason" = 1
                     WHERE "Id" = @id
                    """, bypass))
                {
                    directContractEnd.Parameters.AddWithValue("id", financeContract);
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        async () => { await directContractEnd.ExecuteNonQueryAsync(); });
                    Assert.Equal("23514", rejected.SqlState);
                }
                await using (var directRequestComplete = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementMoveOutRequests"
                       SET "Status" = 4, "CompletedAt" = now()
                     WHERE "Id" = @id
                    """, bypass))
                {
                    directRequestComplete.Parameters.AddWithValue("id", financeMoveOut);
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        async () => { await directRequestComplete.ExecuteNonQueryAsync(); });
                    Assert.Equal("23514", rejected.SqlState);
                }
            }

            var unsignedMaintenance = await PostFinalAsync(
                clientRoot, financeRoot + "/move-outs/" +
                    financeMoveOut.ToString("D") + "/complete",
                JsonSerializer.Serialize(new {
                    forfeitAmount = 0m,
                    requiresMaintenance = true,
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = DateTime.UtcNow,
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, unsignedMaintenance.Status);

            var contradictoryComplete = await PostFinalAsync(
                clientRoot, financeRoot + "/move-outs/" +
                    financeMoveOut.ToString("D") + "/complete",
                JsonSerializer.Serialize(new {
                    forfeitAmount = 250_000m,
                    damageReason = "Documented broken window glass",
                    damageEvidenceReference = "SIGNED-DAMAGE-REPORT-001",
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = DateTime.UtcNow,
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, contradictoryComplete.Status);

            var backdatedRefund = await PostFinalAsync(
                clientRoot, financeRoot + "/move-outs/" +
                    financeMoveOut.ToString("D") + "/complete",
                JsonSerializer.Serialize(new {
                    forfeitAmount = 0m,
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = DateTime.UtcNow.AddDays(-1),
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, backdatedRefund.Status);

            var refundBankPaidAt = DateTime.UtcNow;
            refundBankPaidAt = refundBankPaidAt.AddTicks(
                -(refundBankPaidAt.Ticks % 10L));
            var refundReviewRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") +
                "/deposit-settlement/refund-bank-review";
            var fullRefundReviewBody = JsonSerializer.Serialize(new {
                paidAtUtc = refundBankPaidAt,
                transactionReference = "SCRATCH-MANUAL-REFUND-0001",
                bankStatementEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001",
                signedIndependentReviewReference =
                    "SIGNED-INDEPENDENT-REFUND-REVIEW-0001"
            });
            var selfReviewed = await PostFinalAsync(
                clientRoot, refundReviewRoute, fullRefundReviewBody);
            Assert.Equal(HttpStatusCode.Forbidden, selfReviewed.Status);
            var notReviewedYet = await PostFinalAsync(
                clientRoot,
                financeRoot + "/move-outs/" + financeMoveOut.ToString("D") +
                    "/complete",
                JsonSerializer.Serialize(new {
                    forfeitAmount = 0m,
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = refundBankPaidAt,
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, notReviewedYet.Status);
            var fullRefundReviewed = await PostFinalAsync(
                clientB, refundReviewRoute, fullRefundReviewBody);
            Assert.True(fullRefundReviewed.Status is HttpStatusCode.OK or
                HttpStatusCode.Created,
                $"Independent full-refund review: {fullRefundReviewed.Status} {fullRefundReviewed.Body}");
            var replayedReview = await PostFinalAsync(
                clientB, refundReviewRoute, fullRefundReviewBody);
            Assert.Equal(fullRefundReviewed.Body, replayedReview.Body);

            using var closeout = await PostValidFinanceAsync(
                clientRoot,
                financeRoot + "/move-outs/" + financeMoveOut.ToString("D") +
                    "/complete",
                new {
                    forfeitAmount = 0m,
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = refundBankPaidAt,
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                });
            Assert.Equal(financeContract,
                closeout.RootElement.GetProperty("contractId").GetGuid());
            Assert.Equal(monthlyRent,
                closeout.RootElement.GetProperty("refundAmount").GetDecimal());
            Assert.Equal((int)BuildingManagement.Rooms.RoomStatus.Available,
                closeout.RootElement.GetProperty("roomStatus").GetInt32());
            Guid roomDispositionId = closeout.RootElement
                .GetProperty("roomDispositionId").GetGuid();
            Assert.NotEqual(Guid.Empty, roomDispositionId);
            using (var persistedHandover = app.Services.CreateScope())
            {
                using var checkUow = persistedHandover.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var checkDb = await persistedHandover.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var record = await checkDb.MoveOutRoomDispositions.AsNoTracking()
                    .SingleAsync(x => x.Id == roomDispositionId);
                record.VerifyStoredDisposition();
                Assert.Equal(financeContract, record.ContractId);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Available,
                    record.ResultRoomStatus);
                await checkUow.CompleteAsync();
            }

            Assert.False(closeout.RootElement
                .GetProperty("hasFutureCommitment").GetBoolean());
            var roomDispositionRoute = financeRoot + "/move-outs/" +
                financeMoveOut.ToString("D") + "/room-disposition";
            using (var archivedDisposition =
                await clientB.GetAsync(roomDispositionRoute))
            {
                Assert.Equal(HttpStatusCode.OK, archivedDisposition.StatusCode);
                using var json = JsonDocument.Parse(
                    await archivedDisposition.Content.ReadAsStringAsync());
                Assert.Equal(roomDispositionId,
                    json.RootElement.GetProperty("dispositionId").GetGuid());
                Assert.Equal((int)BuildingManagement.Rooms.RoomStatus.Available,
                    json.RootElement.GetProperty("resultRoomStatus").GetInt32());
                Assert.Equal(64,
                    json.RootElement.GetProperty("dispositionSha256")
                        .GetString()!.Length);
                Assert.False(json.RootElement.GetProperty(
                    "requiresMaintenance").GetBoolean());
            }
            using (var foreignDisposition = await clientB.GetAsync(
                FinalInvoices(contractA) + "/move-outs/" +
                financeMoveOut.ToString("D") + "/room-disposition"))
                Assert.Equal(HttpStatusCode.Forbidden,
                    foreignDisposition.StatusCode);


            // Authenticated building-scoped retrieval AFTER commit:
            // permanent approval and money totals are visible, but the
            // raw manual transfer reference and tenant identity are not.
            using (var signed = await clientB.GetAsync(signedCloseoutRoute))
            {
                Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
                var body = await signed.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var record = json.RootElement;
                Assert.Equal(financeContract,
                    record.GetProperty("contractId").GetGuid());
                Assert.Equal(financeMoveOut,
                    record.GetProperty("moveOutRequestId").GetGuid());
                Assert.Equal(financeIssuedId,
                    record.GetProperty("issuedInvoiceId").GetGuid());
                Assert.Equal(monthlyRent,
                    record.GetProperty("verifiedHeldAmount").GetDecimal());
                Assert.Equal(monthlyRent,
                    record.GetProperty("refundAmount").GetDecimal());
                Assert.Equal(0m,
                    record.GetProperty("forfeitAmount").GetDecimal());
                Assert.Equal((int)BuildingManagement.Rooms.RoomStatus.Available,
                    record.GetProperty("roomStatusAtCloseout").GetInt32());
                Assert.Equal((int)BuildingManagement.Rooms.RoomStatus.Available,
                    record.GetProperty("currentRoomStatus").GetInt32());
                Assert.True(record.GetProperty(
                    "refundManuallyAttested").GetBoolean());
                Assert.False(record.GetProperty(
                    "bankTransferIndependentlyVerified").GetBoolean());
                Assert.Equal(64,
                    record.GetProperty("approvalSha256").GetString()!.Length);
                Assert.DoesNotContain("SCRATCH-MANUAL-REFUND-0001", body);
                Assert.DoesNotContain("SCRATCH-REFUND-SIGNED-PROOF-0001", body);
                Assert.DoesNotContain(tenantB.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var afterSettlement = await clientB.GetAsync(readPlanRoute))
            {
                Assert.Equal(HttpStatusCode.OK, afterSettlement.StatusCode);
                using var json = JsonDocument.Parse(
                    await afterSettlement.Content.ReadAsStringAsync());
                Assert.True(json.RootElement
                    .GetProperty("isRefundExecuted").GetBoolean());
                Assert.Equal(monthlyRent, json.RootElement
                    .GetProperty("approvedRefundAmount").GetDecimal());
            }
            using (var rootSigned = await clientRoot.GetAsync(signedCloseoutRoute))
                Assert.Equal(HttpStatusCode.OK, rootSigned.StatusCode);
            using (var foreign = await clientB.GetAsync(
                scopedContracts + "/" + contractA.ToString("D") +
                "/move-out-requests/" + financeMoveOut.ToString("D") +
                "/closeout"))
                Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            using (var missing = await clientB.GetAsync(
                scopedContracts + "/" + financeContract.ToString("D") +
                "/move-out-requests/" + Guid.NewGuid().ToString("D") +
                "/closeout"))
                Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);

            var repeatedCloseout = await PostFinalAsync(
                clientRoot,
                financeRoot + "/move-outs/" + financeMoveOut.ToString("D") +
                    "/complete",
                JsonSerializer.Serialize(new {
                    forfeitAmount = 0m,
                    inspectionEvidenceReference = "SCRATCH-SIGNED-INSPECTION-0001",
                    handoverEvidenceReference = "SCRATCH-SIGNED-HANDOVER-0001",
                    depositPolicyReference = "SCRATCH-CONTRACT-DEPOSIT-POLICY-2026",
                    confirmManualRefundPaid = true,
                    refundPaidAtUtc = DateTime.UtcNow,
                    refundTransactionReference = "SCRATCH-MANUAL-REFUND-0001",
                    refundBankEvidenceReference = "SCRATCH-REFUND-SIGNED-PROOF-0001"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, repeatedCloseout.Status);

            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Ended,
                    (await db.Contracts.SingleAsync(x => x.Id == financeContract)).Status);
                Assert.Equal(BuildingManagement.MoveOuts.MoveOutRequestStatus.Completed,
                    (await db.MoveOutRequests.SingleAsync(x => x.Id == financeMoveOut)).Status);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Available,
                    (await db.Rooms.SingleAsync(x => x.Id == financeRoom)).Status);
                Assert.Equal(5, await db.MoveOutChecklistTasks.CountAsync(x =>
                    x.MoveOutRequestId == financeMoveOut && x.IsCompleted));
                Assert.Equal(1, await db.IssuedInvoiceLedger.CountAsync(x =>
                    x.Id == financeIssuedId && x.ContractId == financeContract));
                Assert.Equal(2, await db.InvoicePaymentAllocationJournal
                    .CountAsync(x => x.IssuedInvoiceId == financeIssuedId &&
                        (x.PaymentId == receiptId ||
                         x.PaymentId == partialReceiptId)));
                Assert.Equal(finalBill,
                    await db.InvoicePaymentAllocationJournal
                        .Where(x => x.IssuedInvoiceId == financeIssuedId)
                        .SumAsync(x => x.Amount));
                Assert.Equal(1, await db.DepositCloseoutApprovals.CountAsync(x =>
                    x.ContractId == financeContract &&
                    x.ApprovedRefundAmount == monthlyRent &&
                    x.ApprovedForfeitAmount == 0m));
                Assert.Equal(1, await db.DepositSettlements.CountAsync(x =>
                    x.SourceContractId == financeContract &&
                    x.Type == BuildingManagement.DepositSettlements
                        .DepositSettlementType.Refund &&
                    x.Amount == monthlyRent));
                await uow.CompleteAsync();
            }
            // Prove a journaled payment is frozen even through raw SQL
            // after a successful signed move-out in the isolated database.
            await using (var auditCash = new NpgsqlConnection(buildingCs))
            {
                await auditCash.OpenAsync();
                await using var rewrite = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementPayments"
                       SET "Amount" = 1 WHERE "Id" = @id
                    """, auditCash);
                rewrite.Parameters.AddWithValue("id", receiptId);
                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => rewrite.ExecuteNonQueryAsync());
                Assert.Equal("23514", refused.SqlState);

                // The final room release is a permanent immutable
                // financial/legal handover, not editable after commit.
                await using var rewriteRoomHandover = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementMoveOutRoomDispositions"
                       SET "ResultRoomStatus" = 3
                     WHERE "Id" = @id
                    """, auditCash);
                rewriteRoomHandover.Parameters.AddWithValue("id", roomDispositionId);
                var dispositionDenied = await Assert.ThrowsAsync<PostgresException>(
                    async () => { await rewriteRoomHandover.ExecuteNonQueryAsync(); });
                Assert.Equal("23514", dispositionDenied.SqlState);

                // A signed handover must also freeze both financial contract
                // terms and the original approved move-out date, not merely
                // its receipt. Keep direct SQL probes in the disposable DB.
                await using var amendContract = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementContracts"
                       SET "DepositAmount" = 1 WHERE "Id" = @contract
                    """, auditCash);
                amendContract.Parameters.AddWithValue("contract", financeContract);
                var contractDenied = await Assert.ThrowsAsync<PostgresException>(
                    () => amendContract.ExecuteNonQueryAsync());
                Assert.Equal("23514", contractDenied.SqlState);

                await using var amendRequest = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementMoveOutRequests"
                       SET "ApprovedMoveOutDate" = '2026-12-31'
                     WHERE "Id" = @request
                    """, auditCash);
                amendRequest.Parameters.AddWithValue("request", financeMoveOut);
                var requestDenied = await Assert.ThrowsAsync<PostgresException>(
                    () => amendRequest.ExecuteNonQueryAsync());
                Assert.Equal("23514", requestDenied.SqlState);

                // The completed financial checklist is also part of the
                // immutable signed handover, not editable task metadata.
                await using var reopenChecklist = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementMoveOutChecklistTasks"
                       SET "IsCompleted" = FALSE, "CompletedAt" = NULL
                     WHERE "MoveOutRequestId" = @request AND "Type" = 4
                    """, auditCash);
                reopenChecklist.Parameters.AddWithValue("request", financeMoveOut);
                var taskDenied = await Assert.ThrowsAsync<PostgresException>(
                    () => reopenChecklist.ExecuteNonQueryAsync());
                Assert.Equal("23514", taskDenied.SqlState);

                // A signed financial closeout also seals the identities
                // and roles of its contract parties; raw SQL must not
                // silently replace historical tenant provenance.
                await using var changeParty = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementContractTenants"
                       SET "IsDeleted" = TRUE
                     WHERE "ContractId" = @contract
                    """, auditCash);
                changeParty.Parameters.AddWithValue("contract", financeContract);
                var partyDenied = await Assert.ThrowsAsync<PostgresException>(
                    () => changeParty.ExecuteNonQueryAsync());
                Assert.Equal("23514", partyDenied.SqlState);

                await using var rewriteExtras = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementContracts"
                       SET "ExtraProperties" = '{"deposit":"spoofed"}'
                     WHERE "Id" = @contract
                    """, auditCash);
                rewriteExtras.Parameters.AddWithValue("contract", financeContract);
                var extrasDenied = await Assert.ThrowsAsync<PostgresException>(
                    () => rewriteExtras.ExecuteNonQueryAsync());
                Assert.Equal("23514", extrasDenied.SqlState);

                // A completed contract-deposit receipt supplied the held
                // 3,000,000 VND; the signed refund must not outlive a
                // direct SQL rewrite of that ORIGINAL cash evidence.
                await using var rewriteDeposit = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementPayments"
                       SET "Amount" = 1 WHERE "Id" = @id
                    """, auditCash);
                rewriteDeposit.Parameters.AddWithValue("id", financeDepositPaymentId);
                var depositDenied = await Assert.ThrowsAsync<PostgresException>(
                    () => rewriteDeposit.ExecuteNonQueryAsync());
                Assert.Equal("23514", depositDenied.SqlState);
            }

            // Historical closeout remains readable even after the room
            // enters a NEW, legitimate lifecycle state. Live RoomStatus is
            // NOT the original signed handover state.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var oldRoom = await db.Rooms.SingleAsync(x => x.Id == financeRoom);
                oldRoom.ChangeStatus(BuildingManagement.Rooms.RoomStatus.Maintenance);
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }
            using (var later = await clientB.GetAsync(signedCloseoutRoute))
            {
                Assert.Equal(HttpStatusCode.OK, later.StatusCode);
                using var json = JsonDocument.Parse(
                    await later.Content.ReadAsStringAsync());
                Assert.Equal((int)BuildingManagement.Rooms.RoomStatus.Available,
                    json.RootElement.GetProperty("roomStatusAtCloseout").GetInt32());
                Assert.Equal((int)BuildingManagement.Rooms.RoomStatus.Maintenance,
                    json.RootElement.GetProperty("currentRoomStatus").GetInt32());
            }

            string Members(Guid contractId) => scopedContracts + "/" +
                contractId.ToString("D") + "/tenants";
            using (var response = await clientB.GetAsync(Members(contractB)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Single(rows);
                Assert.Equal(tenantB, rows[0].GetProperty("tenantId").GetGuid());
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Tenant B scratch", body);
                Assert.DoesNotContain("CID-B-", body);
            }
            using (var response = await clientB.GetAsync(Members(contractA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.GetAsync(Members(contractA)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string AddMember(Guid tenant, int role) =>
                JsonSerializer.Serialize(new { tenantId = tenant, role });
            async Task<HttpStatusCode> PostMemberAsync(
                Guid contract, Guid tenant, int role)
            {
                using var content = new StringContent(AddMember(tenant, role),
                    Encoding.UTF8, "application/json");
                using var response = await clientB.PostAsync(Members(contract), content);
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.Forbidden,
                await PostMemberAsync(contractC, tenantA, 2));
            Assert.Equal(HttpStatusCode.Forbidden,
                await PostMemberAsync(contractC, Guid.NewGuid(), 2));
            Assert.Equal(HttpStatusCode.Forbidden,
                await PostMemberAsync(contractA, tenantA, 2));
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(contractC, tenantB, 2));
            using (var response = await clientB.GetAsync(Members(contractC)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Single(rows);
                Assert.Equal(tenantB, rows[0].GetProperty("tenantId").GetGuid());
            }
            using (var payload = new StringContent("""{"role":1}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(
                Members(contractC) + "/" + tenantB.ToString("D"), payload))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(Members(contractC)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Single(rows);
                Assert.Equal(1, rows[0].GetProperty("role").GetInt32());
            }
            using (var payload = new StringContent("""{"role":2}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(
                Members(contractA) + "/" + tenantA.ToString("D"), payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.DeleteAsync(
                Members(contractA) + "/" + tenantA.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.DeleteAsync(
                Members(contractC) + "/" + tenantB.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(Members(contractC)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Empty(json.RootElement.EnumerateArray());
            }
            // Legacy service must restore a soft-deleted relation, never
            // create a duplicate despite the contract/tenant unique index.
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(contractC, tenantB, 2));
            using (var response = await clientB.DeleteAsync(
                Members(contractC) + "/" + tenantB.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // New tenant onboarding is the ONLY route that may create
            // global PII: it requires the same Building contract Draft row,
            // and the new Tenant + ContractTenant must commit atomically.
            string NewTenantJson(string phone, string citizenId, int role) =>
                JsonSerializer.Serialize(new
                {
                    tenant = new
                    {
                        fullName = "Slice B Onboarding",
                        phoneNumber = phone,
                        identityType = 1,
                        identityNumber = citizenId,
                        email = "scratch@example.test"
                    },
                    role
                });
            async Task<(HttpStatusCode Code, string Body)> RegisterAsync(
                HttpClient caller, Guid contract, string phone,
                string citizenId, int role)
            {
                using var payload = new StringContent(
                    NewTenantJson(phone, citizenId, role), Encoding.UTF8,
                    "application/json");
                using var response = await caller.PostAsync(
                    Members(contract) + "/new", payload);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }

            // A B-only operator cannot create a tenant by targeting A.
            var forbiddenRegistration = await RegisterAsync(clientB,
                contractA, "0981234570", "001123456780", 2);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRegistration.Code);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.False(await db.Tenants.AnyAsync(t =>
                    t.IdentityNumber == "001123456780"));
                await uow.CompleteAsync();
            }

            // A conflict AFTER tenant creation must roll back that newly
            // inserted profile, not strand global PII as an orphan.
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(contractC, tenantB, 1));
            var rollbackRegistration = await RegisterAsync(clientB,
                contractC, "0981234571", "001123456781", 1);
            Assert.NotEqual(HttpStatusCode.OK, rollbackRegistration.Code);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.False(await db.Tenants.AnyAsync(t =>
                    t.IdentityNumber == "001123456781"));
                await uow.CompleteAsync();
            }
            using (var response = await clientB.DeleteAsync(
                Members(contractC) + "/" + tenantB.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var registered = await RegisterAsync(clientB, contractC,
                "0981234572", "001123456782", 1);
            Assert.Equal(HttpStatusCode.OK, registered.Code);
            Assert.DoesNotContain("0981234572", registered.Body);
            Assert.DoesNotContain("001123456782", registered.Body);
            Assert.DoesNotContain("Slice B Onboarding", registered.Body);
            Guid registeredTenant;
            using (var json = JsonDocument.Parse(registered.Body))
            {
                registeredTenant = json.RootElement
                    .GetProperty("tenantId").GetGuid();
                Assert.NotEqual(Guid.Empty, registeredTenant);
                Assert.Equal(1, json.RootElement.GetProperty("role").GetInt32());
            }
            using (var response = await clientB.GetAsync(Members(contractC)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Contains(json.RootElement.EnumerateArray(),
                    item => item.GetProperty("tenantId").GetGuid() ==
                        registeredTenant);
            }
            using (var response = await clientB.DeleteAsync(
                Members(contractC) + "/" + registeredTenant.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // An independent room is used for sign/activate so existing
            // Slice A room-mutation and deletion probes stay unchanged.
            var signingRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "102",
                    name = "PG B102 signing", area = 25.0,
                    capacity = 2, status = 1 });
            async Task<Guid> CreateSigningDraftAsync(string suffix)
            {
                using var body = new StringContent(CreateJson(signingRoom,
                    "SIGN-B-" + suffix + "-" + Guid.NewGuid().ToString("N")),
                    Encoding.UTF8, "application/json");
                using var response = await clientB.PostAsync(scopedContracts, body);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                return json.RootElement.GetProperty("id").GetGuid();
            }
            var signCandidate1 = await CreateSigningDraftAsync("1");
            var signCandidate2 = await CreateSigningDraftAsync("2");
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(signCandidate1, tenantB, 1));
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(signCandidate2, tenantB, 1));

            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + contractA.ToString("D") + "/sign", null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + contractA.ToString("D") + "/activate", null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Hold the room EXCLUSIVELY using an independent DB connection.
            // Neither candidate may pass through signing while the room is
            // locked. After release only ONE can sign due to overlap checks
            // re-evaluated under the acquired room lock.
            Guid activeSigningContract = Guid.Empty;
            await using (var lockConnection = new NpgsqlConnection(buildingCs))
            {
                await lockConnection.OpenAsync();
                await using var lockTx = await lockConnection.BeginTransactionAsync();
                await using (var cmd = new NpgsqlCommand(
                    """
                    SELECT "Id" FROM "BuildingManagementRooms"
                     WHERE "Id" = @room FOR UPDATE
                    """, lockConnection, lockTx))
                {
                    cmd.Parameters.AddWithValue("room", signingRoom);
                    Assert.Equal(signingRoom, await cmd.ExecuteScalarAsync());
                }
                async Task<HttpStatusCode> SignWithClientAsync(Guid id)
                {
                    using var response = await clientB.PostAsync(
                        scopedContracts + "/" + id.ToString("D") + "/sign", null);
                    return response.StatusCode;
                }
                var signing1 = Task.Run(() => SignWithClientAsync(signCandidate1));
                var signing2 = Task.Run(() => SignWithClientAsync(signCandidate2));
                await Task.Delay(250);
                Assert.False(signing1.IsCompleted,
                    "First contract sign must wait for room row lock.");
                Assert.False(signing2.IsCompleted,
                    "Second contract sign must wait for room row lock.");
                await lockTx.CommitAsync();
                var results = await Task.WhenAll(signing1, signing2);
                Assert.Equal(1, results.Count(x => x == HttpStatusCode.OK));
                var signed = results[0] == HttpStatusCode.OK
                    ? signCandidate1 : signCandidate2;
                activeSigningContract = signed;
                var rejected = signed == signCandidate1
                    ? signCandidate2 : signCandidate1;
                using (var response = await clientB.GetAsync(
                    scopedContracts + "/" + signed.ToString("D")))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    Assert.Equal(5,
                        json.RootElement.GetProperty("status").GetInt32());
                }
                using (var response = await clientB.GetAsync(
                    scopedContracts + "/" + rejected.ToString("D")))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    Assert.Equal(1,
                        json.RootElement.GetProperty("status").GetInt32());
                }
                using (var response = await clientB.PostAsync(
                    scopedContracts + "/" + signed.ToString("D") + "/activate",
                    null))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    Assert.Equal(2,
                        json.RootElement.GetProperty("status").GetInt32());
                }
                using (var response = await clientB.GetAsync(
                    ChildId(roomGet, signingRoom)))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    Assert.Equal(2,
                        json.RootElement.GetProperty("status").GetInt32());
                }
                using (var response = await clientB.PostAsync(
                    scopedContracts + "/" + rejected.ToString("D") +
                        "/activate", null))
                    Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
                using (var response = await clientB.PostAsync(
                    scopedContracts + "/" + signed.ToString("D") + "/sign", null))
                    Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            }

            // A live Active contract can reserve renewal only for its own
            // building. One active hold must be consumed in the same UoW as
            // the new signed contract, copied tenants and date checks.
            string RenewalRoute(Guid contract) => scopedContracts + "/" +
                contract.ToString("D") + "/renew";
            string HoldRoute(Guid contract) => scopedContracts + "/" +
                contract.ToString("D") + "/renewal-holds";
            string RenewalJson(string suffix) => JsonSerializer.Serialize(new
            {
                contractNumber = "RENEW-" + suffix + "-" +
                    Guid.NewGuid().ToString("N"),
                startDate = "2027-11-01T00:00:00Z",
                endDate = "2028-10-31T00:00:00Z",
                monthlyRent = 2850000m,
                notes = "Isolated renewal audit"
            });

            using (var payload = new StringContent("""{"notes":"denied"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                HoldRoute(contractA), payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var payload = new StringContent(RenewalJson("A"),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                RenewalRoute(contractA), payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using (var payload = new StringContent(RenewalJson("NO-HOLD"),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                RenewalRoute(activeSigningContract), payload))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            Guid renewalHold;
            using (var payload = new StringContent("""{"notes":"renewal scope"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                HoldRoute(activeSigningContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                renewalHold = json.RootElement.GetProperty("id").GetGuid();
                Assert.NotEqual(Guid.Empty, renewalHold);
                Assert.Equal(1, json.RootElement.GetProperty("status").GetInt32());
            }
            // The global hold primary key cannot be cancelled by someone
            // outside the contract's building, nor used by a stranger.
            // A second hold is rejected by unique history / active gate.
            using (var payload = new StringContent("""{"notes":"duplicate"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                HoldRoute(activeSigningContract), payload))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            Guid renewedContract;
            using (var payload = new StringContent(RenewalJson("SUCCESS"),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                RenewalRoute(activeSigningContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                renewedContract = json.RootElement.GetProperty("id").GetGuid();
                Assert.NotEqual(activeSigningContract, renewedContract);
            }
            using (var response = await clientB.GetAsync(
                scopedContracts + "/" + renewedContract.ToString("D")))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal(5, json.RootElement.GetProperty("status").GetInt32());
                Assert.Equal(signingRoom,
                    json.RootElement.GetProperty("roomId").GetGuid());
            }
            using (var response = await clientB.GetAsync(Members(renewedContract)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Contains(json.RootElement.EnumerateArray(),
                    member => member.GetProperty("tenantId").GetGuid() == tenantB &&
                        member.GetProperty("role").GetInt32() == 1);
            }
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var hold = await db.ContractRenewalHolds
                    .SingleAsync(h => h.Id == renewalHold);
                Assert.Equal(BuildingManagement.Contracts.ContractRenewalHoldStatus.Completed,
                    hold.Status);
                Assert.Equal(renewedContract, hold.CompletedContractId);
                await uow.CompleteAsync();
            }
            using (var payload = new StringContent(RenewalJson("AGAIN"),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                RenewalRoute(activeSigningContract), payload))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                scopedContracts + "/renewal-holds/" +
                renewalHold.ToString("D") + "/cancel", null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            // Standalone cancel path: new room, new Active contract, hold
            // cancelled before renewal. It must block renewal and leave
            // contract/room occupancy unchanged.
            var cancelRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "103",
                    name = "PG B103 cancel hold", area = 25.0,
                    capacity = 2, status = 1 });
            Guid cancelContract;
            using (var payload = new StringContent(CreateJson(cancelRoom,
                "CANCEL-HOLD-" + Guid.NewGuid().ToString("N")),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(scopedContracts, payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                cancelContract = json.RootElement.GetProperty("id").GetGuid();
            }
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(cancelContract, tenantB, 1));
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + cancelContract.ToString("D") + "/sign", null))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + cancelContract.ToString("D") + "/activate", null))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Guid cancelledHold;
            using (var payload = new StringContent("""{"notes":"cancel it"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                HoldRoute(cancelContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                cancelledHold = json.RootElement.GetProperty("id").GetGuid();
            }
            using (var response = await clientB.PostAsync(
                scopedContracts + "/renewal-holds/" +
                cancelledHold.ToString("D") + "/cancel", null))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal(4, json.RootElement.GetProperty("status").GetInt32());
            }
            using (var payload = new StringContent(RenewalJson("CANCELLED"),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                RenewalRoute(cancelContract), payload))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            // Direct ContractDeposit payment has its own contract-bound
            // route. A cross-building ID and route/body substitution are
            // forbidden, and duplicate cash confirmation cannot create
            // a second allocation.
            string DirectDepositRoute(Guid contractId) =>
                scopedContracts + "/" + contractId.ToString("D") +
                "/deposit-payments";
            string DirectDepositJson(Guid contractId) => JsonSerializer.Serialize(new
            {
                contractId,
                paymentNumber = "DIRECT-DEP-" + Guid.NewGuid().ToString("N"),
                amount = 500000m,
                method = 1,
                channel = 1
            });
            using (var body = new StringContent(DirectDepositJson(contractA),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                DirectDepositRoute(contractA), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent(DirectDepositJson(contractA),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                DirectDepositRoute(cancelContract), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            Guid directPayment;
            using (var body = new StringContent(DirectDepositJson(cancelContract),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                DirectDepositRoute(cancelContract), body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                directPayment = json.RootElement.GetProperty("id").GetGuid();
                Assert.Equal(1, json.RootElement.GetProperty("status").GetInt32());
            }
            string CompleteDirectRoute(Guid contractId) =>
                DirectDepositRoute(contractId) + "/" +
                directPayment.ToString("D") + "/complete";
            using (var body = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                CompleteDirectRoute(contractA), body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var body = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                CompleteDirectRoute(cancelContract), body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal(2, json.RootElement.GetProperty("status").GetInt32());
            }
            using (var body = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                CompleteDirectRoute(cancelContract), body))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var postings = await db.DepositSettlements
                    .Where(s => s.PaymentId == directPayment).ToListAsync();
                Assert.Single(postings);
                Assert.Equal(500000m, postings[0].Amount);
                Assert.Equal(cancelContract, postings[0].TargetContractId);
                Assert.Equal(
                    BuildingManagement.DepositSettlements.DepositSettlementType.AllocateToContractDeposit,
                    postings[0].Type);
                await uow.CompleteAsync();
            }

            // A second Pending direct deposit can be cancelled only
            // through this contract. The completed receipt is immutable.
            Guid cancellableContractPayment;
            using (var body = new StringContent(DirectDepositJson(cancelContract),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                DirectDepositRoute(cancelContract), body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                cancellableContractPayment = json.RootElement
                    .GetProperty("id").GetGuid();
            }
            string CancelContractPayment(Guid id, Guid paymentId) =>
                DirectDepositRoute(id) + "/" + paymentId.ToString("D") +
                "/cancel";
            using (var response = await clientB.PostAsync(
                CancelContractPayment(contractA, cancellableContractPayment), null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                CancelContractPayment(cancelContract, cancellableContractPayment), null))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                CancelContractPayment(cancelContract, cancellableContractPayment), null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                CancelContractPayment(cancelContract, directPayment), null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            // Read only the authorized contract's deposit receipt status,
            // with no global tenant, payment reference or private notes.
            using (var response = await clientB.GetAsync(
                DirectDepositRoute(contractA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                DirectDepositRoute(cancelContract)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var content = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(content);
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Contains(rows, p =>
                    p.GetProperty("id").GetGuid() == directPayment &&
                    p.GetProperty("amount").GetDecimal() == 500000m &&
                    p.GetProperty("status").GetInt32() == 2);
                Assert.DoesNotContain("transactionReference", content,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("tenantId", content,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("notes", content,
                    StringComparison.OrdinalIgnoreCase);
            }

            // Reservation creation is scoped to existing tenant provenance:
            // operator B may reserve only a B room for a B-linked tenant.
            var reservationRoute = scopedContracts + "/reservations";
            // Reservation dates follow the domain's Vietnam-local calendar.
            // A hardcoded 2026-10-29 becomes invalid the next day, making
            // otherwise unrelated PostgreSQL finance regression fail.
            // First day of next month is inside the supported window even
            // when CI runs at month-end or just after midnight in Vietnam.
            var localToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            var firstNextMonth = new DateOnly(
                localToday.Year, localToday.Month, 1).AddMonths(1);
            var reservationMoveIn = firstNextMonth.ToString("yyyy-MM-dd") +
                "T00:00:00Z";
            string ReservationJson(Guid roomId, Guid tenantId) =>
                JsonSerializer.Serialize(new {
                    reservationNumber = "RES-STAGE-" + Guid.NewGuid().ToString("N"),
                    roomId, tenantId, expectedMoveInDate = reservationMoveIn,
                    quotedMonthlyRent = 2800000m,
                    requiredDepositAmount = 3000000m,
                    minimumReservationDepositAmount = 500000m
                });
            var reservationB = Guid.Empty;
            async Task<HttpStatusCode> TryReserveAsync(Guid roomId, Guid tenantId)
            {
                using var payload = new StringContent(
                    ReservationJson(roomId, tenantId), Encoding.UTF8,
                    "application/json");
                using var response = await clientB.PostAsync(reservationRoute, payload);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    reservationB = json.RootElement.GetProperty("id").GetGuid();
                }
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.Forbidden,
                await TryReserveAsync(roomA, tenantB));
            Assert.Equal(HttpStatusCode.Forbidden,
                await TryReserveAsync(roomB, tenantA));
            Assert.Equal(HttpStatusCode.Forbidden,
                await TryReserveAsync(roomB, Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK,
                await TryReserveAsync(roomB, tenantB));
            Assert.NotEqual(Guid.Empty, reservationB);

            // A matching room in a different building never becomes visible
            // through the reservation list, direct ID, or tenant GUID.
            var reservationA = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                db.RoomReservations.Add(new BuildingManagement.RoomReservations.RoomReservation(
                    reservationA, "RES-STAGE-A-" + reservationA.ToString("N"),
                    roomA, tenantA,
                    new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                    2700000m, 3000000m, 500000m));
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }

            using (var response = await clientB.GetAsync(reservationRoute))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == reservationB);
                Assert.DoesNotContain(rows, row => row.GetProperty("id").GetGuid() == reservationA);
                Assert.DoesNotContain("phoneNumber", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("identityNumber", body, StringComparison.OrdinalIgnoreCase);
            }
            using (var response = await clientRoot.GetAsync(reservationRoute))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == reservationA);
                Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == reservationB);
            }
            using (var response = await clientB.GetAsync(
                reservationRoute + "/" + reservationB.ToString("D")))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(
                reservationRoute + "/" + reservationA.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                reservationRoute + "/" + Guid.NewGuid().ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                reservationRoute + "?maxResultCount=101"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Scoped reservation cancellation must not permit foreign-room
            // enumeration or cross-building mutations. The pending B hold
            // has no completed payment, so no settlement may be created.
            async Task<HttpStatusCode> TryCancelReservationAsync(Guid id)
            {
                using var payload = new StringContent("""{"reason":1}""",
                    Encoding.UTF8, "application/json");
                using var response = await clientB.PostAsync(
                    reservationRoute + "/" + id.ToString("D") + "/cancel", payload);
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.Forbidden,
                await TryCancelReservationAsync(reservationA));
            Assert.Equal(HttpStatusCode.Forbidden,
                await TryCancelReservationAsync(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.OK,
                await TryCancelReservationAsync(reservationB));
            Assert.NotEqual(HttpStatusCode.OK,
                await TryCancelReservationAsync(reservationB));
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.RoomReservations.RoomReservationStatus.Cancelled,
                    (await db.RoomReservations.SingleAsync(r =>
                        r.Id == reservationB)).Status);
                Assert.False(await db.DepositSettlements.AnyAsync(s =>
                    s.SourceRoomReservationId == reservationB));
                Assert.Equal(BuildingManagement.RoomReservations.RoomReservationStatus.PendingPayment,
                    (await db.RoomReservations.SingleAsync(r =>
                        r.Id == reservationA)).Status);
                await uow.CompleteAsync();
            }

            // Only a fresh reservation in building B can accept a pending
            // deposit payment. A foreign reservation ID and mismatched route
            // identifier must fail before any globally addressed payment.
            Assert.Equal(HttpStatusCode.OK,
                await TryReserveAsync(roomB, tenantB));
            var paidCandidate = reservationB;
            string PaymentJson(Guid roomReservationId) => JsonSerializer.Serialize(new
            {
                paymentNumber = "STAGE-PAY-" + Guid.NewGuid().ToString("N"),
                roomReservationId,
                amount = 500000m,
                method = 1,
                channel = 1
            });
            async Task<(HttpStatusCode Status, string Body)> TryCreateDepositAsync(
                Guid routeId, Guid bodyId)
            {
                using var payload = new StringContent(
                    PaymentJson(bodyId), Encoding.UTF8, "application/json");
                using var response = await clientB.PostAsync(
                    reservationRoute + "/" + routeId.ToString("D") +
                    "/deposit-payments", payload);
                return (response.StatusCode,
                    await response.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Forbidden,
                (await TryCreateDepositAsync(reservationA, reservationA)).Status);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await TryCreateDepositAsync(paidCandidate, reservationA)).Status);
            var pendingDeposit = await TryCreateDepositAsync(
                paidCandidate, paidCandidate);
            Assert.Equal(HttpStatusCode.OK, pendingDeposit.Status);
            Guid pendingPaymentId;
            using (var json = JsonDocument.Parse(pendingDeposit.Body))
            {
                pendingPaymentId = json.RootElement.GetProperty("id").GetGuid();
                Assert.NotEqual(Guid.Empty, pendingPaymentId);
                Assert.Equal(1, json.RootElement.GetProperty("status").GetInt32());
            }
            // Two Pending receipts may exist, but cancelling a pending
            // alternate must not cancel the reservation or completed money.
            var alternate = await TryCreateDepositAsync(
                paidCandidate, paidCandidate);
            Assert.Equal(HttpStatusCode.OK, alternate.Status);
            Guid alternatePayment;
            using (var json = JsonDocument.Parse(alternate.Body))
                alternatePayment = json.RootElement.GetProperty("id").GetGuid();
            string CancelReservationPayment(Guid reservationId, Guid paymentId) =>
                reservationRoute + "/" + reservationId.ToString("D") +
                "/deposit-payments/" + paymentId.ToString("D") + "/cancel";
            using (var response = await clientB.PostAsync(
                CancelReservationPayment(reservationA, alternatePayment), null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                CancelReservationPayment(paidCandidate, alternatePayment), null))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                CancelReservationPayment(paidCandidate, alternatePayment), null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            async Task<HttpStatusCode> TryCompleteDepositAsync(Guid routeReservation)
            {
                using var payload = new StringContent("{}",
                    Encoding.UTF8, "application/json");
                using var response = await clientB.PostAsync(
                    reservationRoute + "/" + routeReservation.ToString("D") +
                    "/deposit-payments/" + pendingPaymentId.ToString("D") +
                    "/complete", payload);
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.Forbidden,
                await TryCompleteDepositAsync(reservationA));
            Assert.Equal(HttpStatusCode.OK,
                await TryCompleteDepositAsync(paidCandidate));
            Assert.NotEqual(HttpStatusCode.OK,
                await TryCompleteDepositAsync(paidCandidate));
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.Payments.PaymentStatus.Completed,
                    (await db.Payments.SingleAsync(p => p.Id == pendingPaymentId)).Status);
                Assert.Equal(BuildingManagement.RoomReservations.RoomReservationStatus.Reserved,
                    (await db.RoomReservations.SingleAsync(r => r.Id == paidCandidate)).Status);
                await uow.CompleteAsync();
            }

            using (var response = await clientB.PostAsync(
                CancelReservationPayment(paidCandidate, pendingPaymentId), null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                using var uow = scope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await scope.ServiceProvider.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.Payments.PaymentStatus.Cancelled,
                    (await db.Payments.SingleAsync(p => p.Id == alternatePayment)).Status);
                Assert.Equal(BuildingManagement.Payments.PaymentStatus.Completed,
                    (await db.Payments.SingleAsync(p => p.Id == pendingPaymentId)).Status);
                Assert.False(await db.DepositSettlements.AnyAsync(s =>
                    s.PaymentId == alternatePayment));
                await uow.CompleteAsync();
            }

            // Payment status read cannot disclose receipts from another
            // building or the private bank/tenant evidence.
            using (var response = await clientB.GetAsync(
                reservationRoute + "/" + reservationA.ToString("D") +
                "/deposit-payments"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                reservationRoute + "/" + paidCandidate.ToString("D") +
                "/deposit-payments"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var content = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(content);
                var rows = json.RootElement.EnumerateArray().ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Contains(rows, p =>
                    p.GetProperty("id").GetGuid() == pendingPaymentId &&
                    p.GetProperty("amount").GetDecimal() == 500000m &&
                    p.GetProperty("status").GetInt32() == 2);
                Assert.Contains(rows, p =>
                    p.GetProperty("id").GetGuid() == alternatePayment &&
                    p.GetProperty("status").GetInt32() == 4);
                Assert.DoesNotContain("tenantId", content,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("transactionReference", content,
                    StringComparison.OrdinalIgnoreCase);
            }

            // This is a real completed 500k cash hold: cancellation must
            // atomically forfeit exactly 500k once, not refund it twice
            // or silently mark only the reservation cancelled.
            Assert.Equal(HttpStatusCode.OK,
                await TryCancelReservationAsync(paidCandidate));
            Assert.NotEqual(HttpStatusCode.OK,
                await TryCancelReservationAsync(paidCandidate));
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.RoomReservations.RoomReservationStatus.Cancelled,
                    (await db.RoomReservations.SingleAsync(r => r.Id == paidCandidate)).Status);
                var dispositions = await db.DepositSettlements
                    .Where(s => s.SourceRoomReservationId == paidCandidate)
                    .ToListAsync();
                Assert.Single(dispositions);
                Assert.Equal(BuildingManagement.DepositSettlements.DepositSettlementType.Forfeit,
                    dispositions[0].Type);
                Assert.Equal(500000m, dispositions[0].Amount);
                await uow.CompleteAsync();
            }

            // An actual completed hold must be allocated to the matching
            // Active contract once, not to an unrelated room or building.
            var allocationRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "104",
                    name = "PG B104 allocation", area = 25.0,
                    capacity = 2, status = 1 });
            Guid allocationReservation;
            using (var body = new StringContent(ReservationJson(
                allocationRoom, tenantB), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(reservationRoute, body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                allocationReservation = json.RootElement.GetProperty("id").GetGuid();
            }
            var allocationPending = await TryCreateDepositAsync(
                allocationReservation, allocationReservation);
            Assert.Equal(HttpStatusCode.OK, allocationPending.Status);
            Guid allocationPayment;
            using (var json = JsonDocument.Parse(allocationPending.Body))
                allocationPayment = json.RootElement.GetProperty("id").GetGuid();
            using (var body = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                reservationRoute + "/" + allocationReservation.ToString("D") +
                "/deposit-payments/" + allocationPayment.ToString("D") +
                "/complete", body))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Guid allocationContract;
            using (var body = new StringContent(CreateJson(allocationRoom,
                "STAGE-ALLOC-" + Guid.NewGuid().ToString("N")),
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(scopedContracts, body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                allocationContract = json.RootElement.GetProperty("id").GetGuid();
            }
            Assert.Equal(HttpStatusCode.OK,
                await PostMemberAsync(allocationContract, tenantB, 1));
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + allocationContract.ToString("D") +
                "/sign", null))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + allocationContract.ToString("D") +
                "/activate", null))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string AllocationRoute(Guid contract, Guid reservation) =>
                scopedContracts + "/" + contract.ToString("D") +
                "/allocate-reservation/" + reservation.ToString("D");
            using (var response = await clientB.PostAsync(
                AllocationRoute(contractA, allocationReservation), null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                AllocationRoute(allocationContract, reservationA), null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                AllocationRoute(cancelContract, allocationReservation), null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                AllocationRoute(allocationContract, allocationReservation), null))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal(500000m,
                    json.RootElement.GetProperty("allocatedAmount").GetDecimal());
                Assert.Equal(500000m,
                    json.RootElement.GetProperty("coveredDeposit").GetDecimal());
            }
            using (var response = await clientB.PostAsync(
                AllocationRoute(allocationContract, allocationReservation), null))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal(0m,
                    json.RootElement.GetProperty("allocatedAmount").GetDecimal());
                Assert.Equal(500000m,
                    json.RootElement.GetProperty("coveredDeposit").GetDecimal());
            }
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var settlements = await db.DepositSettlements
                    .Where(s => s.SourceRoomReservationId == allocationReservation)
                    .ToListAsync();
                Assert.Single(settlements);
                Assert.Equal(
                    BuildingManagement.DepositSettlements.DepositSettlementType.AllocateToContractDeposit,
                    settlements[0].Type);
                Assert.Equal(500000m, settlements[0].Amount);
                Assert.Equal(allocationContract, settlements[0].TargetContractId);
                var converted = await db.RoomReservations
                    .SingleAsync(r => r.Id == allocationReservation);
                Assert.Equal(
                    BuildingManagement.RoomReservations.RoomReservationStatus.ConvertedToContract,
                    converted.Status);
                Assert.Equal(allocationContract, converted.ConvertedContractId);
                await uow.CompleteAsync();
            }

            // Full 3m completed hold must refund (not forfeit) on customer
            // cancellation; no duplicate payout journal on retry.
            var refundRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "105",
                    name = "PG B105 full deposit refund", area = 25.0,
                    capacity = 2, status = 1 });
            Guid refundReservation;
            using (var body = new StringContent(ReservationJson(
                refundRoom, tenantB), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(reservationRoute, body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                refundReservation = json.RootElement.GetProperty("id").GetGuid();
            }
            Guid refundPayment;
            using (var body = new StringContent(JsonSerializer.Serialize(new
            {
                paymentNumber = "STAGE-FULL-" + Guid.NewGuid().ToString("N"),
                roomReservationId = refundReservation,
                amount = 3000000m,
                method = 1,
                channel = 1
            }), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                reservationRoute + "/" + refundReservation.ToString("D") +
                "/deposit-payments", body))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                refundPayment = json.RootElement.GetProperty("id").GetGuid();
            }
            using (var body = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                reservationRoute + "/" + refundReservation.ToString("D") +
                "/deposit-payments/" + refundPayment.ToString("D") +
                "/complete", body))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                await TryCancelReservationAsync(refundReservation));
            Assert.NotEqual(HttpStatusCode.OK,
                await TryCancelReservationAsync(refundReservation));
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var refundJournal = await db.DepositSettlements
                    .Where(s => s.SourceRoomReservationId == refundReservation)
                    .ToListAsync();
                Assert.Single(refundJournal);
                Assert.Equal(
                    BuildingManagement.DepositSettlements.DepositSettlementType.Refund,
                    refundJournal[0].Type);
                Assert.Equal(3000000m, refundJournal[0].Amount);
                Assert.Equal(
                    BuildingManagement.RoomReservations.RoomReservationStatus.Cancelled,
                    (await db.RoomReservations.SingleAsync(r =>
                        r.Id == refundReservation)).Status);
                await uow.CompleteAsync();
            }

            // Phase 2H end-to-end isolated HTTP: actual authenticated ABP,
            // scoped contract, identity commit lease and PostgreSQL triggers.
            // No financial transfer or room reassignment can occur via these
            // request/review-only routes.
            Assert.Contains(actions, action =>
                action.ControllerTypeInfo.AsType() ==
                    typeof(SliceBStagingRoomChangesController) &&
                action.MethodInfo.Name == "SubmitAsync");
            using (var scope = app.Services.CreateScope())
                Assert.NotNull(scope.ServiceProvider
                    .GetRequiredService<SliceBStagingRoomChangeRequests>());

            var changeTarget = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "116",
                    name = "Phase2H review-only target",
                    area = 25m, capacity = 2, status = 1 });
            string ChangeRoute(Guid id) =>
                "/api/slice-b-staging/contracts/" + id.ToString("D") +
                "/room-changes";
            var changeInput = JsonSerializer.Serialize(new {
                targetRoomId = changeTarget,
                requestedDate = "2027-06-30",
                reason = "Tenant requested a quieter room for next lease"
            });
            var crossTargetInput = JsonSerializer.Serialize(new {
                targetRoomId = roomA,
                requestedDate = "2027-06-30",
                reason = "Attempt to move outside own building"
            });
            using (var foreignPayload = new StringContent(changeInput,
                Encoding.UTF8, "application/json"))
            using (var foreign = await clientB.PostAsync(
                ChangeRoute(contractA), foreignPayload))
                Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            using (var foreignTargetPayload = new StringContent(
                crossTargetInput, Encoding.UTF8, "application/json"))
            using (var foreign = await clientB.PostAsync(
                ChangeRoute(cancelContract), foreignTargetPayload))
                Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

            Guid roomChangeRequest;
            using (var payload = new StringContent(changeInput, Encoding.UTF8,
                "application/json"))
            using (var created = await clientB.PostAsync(
                ChangeRoute(cancelContract), payload))
            {
                var body = await created.Content.ReadAsStringAsync();
                Assert.True(created.StatusCode is HttpStatusCode.OK or
                    HttpStatusCode.Created, "RoomChange submit: " + body);
                using var doc = JsonDocument.Parse(body);
                Assert.Equal(0, doc.RootElement.GetProperty("status").GetInt32());
                Assert.Equal(cancelRoom,
                    doc.RootElement.GetProperty("sourceRoomId").GetGuid());
                Assert.Equal(changeTarget,
                    doc.RootElement.GetProperty("targetRoomId").GetGuid());
                roomChangeRequest = doc.RootElement.GetProperty("id").GetGuid();
            }
            using (var payload = new StringContent(changeInput, Encoding.UTF8,
                "application/json"))
            using (var repeat = await clientB.PostAsync(
                ChangeRoute(cancelContract), payload))
                Assert.False(repeat.IsSuccessStatusCode);

            string ChangeAt(Guid id) =>
                ChangeRoute(cancelContract) + "/" + id.ToString("D");
            using (var crossRead = await clientB.GetAsync(
                ChangeRoute(contractA) + "/" + roomChangeRequest.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, crossRead.StatusCode);
            using (var wrongId = await clientB.GetAsync(
                ChangeAt(Guid.NewGuid()) + "/live-readiness"))
                Assert.Equal(HttpStatusCode.Forbidden, wrongId.StatusCode);
            // New immutable roster history is captured from persisted
            // ContractTenant links, never from a client-provided list.
            using (var noRoster = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/live-readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, noRoster.StatusCode);
                using var json = JsonDocument.Parse(
                    await noRoster.Content.ReadAsStringAsync());
                Assert.False(json.RootElement.GetProperty(
                    "canContinuePolicyReview").GetBoolean());
                Assert.Contains(json.RootElement.GetProperty("blockers")
                    .EnumerateArray(), x => x.GetString() ==
                    "TenantRosterSnapshotMissingOrChanged");
            }
            using (var forged = await clientB.PostAsync(
                ChangeRoute(contractA) + "/" + roomChangeRequest.ToString("D") +
                "/tenant-roster", null))
                Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
            Guid rosterId;
            using (var roster = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/tenant-roster", null))
            {
                var body = await roster.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var doc = JsonDocument.Parse(body);
                Assert.True(doc.RootElement.GetProperty("rosterCount").GetInt32() >= 1);
                Assert.False(doc.RootElement.GetProperty(
                    "isTransferConsentVerified").GetBoolean());
                rosterId = doc.RootElement.GetProperty("id").GetGuid();
                Assert.Equal(64, doc.RootElement.GetProperty(
                    "rosterSha256").GetString()!.Length);
            }
            // Explicit OI-07 choices are persisted as PROPOSED only,
            // separate from any independently authorized transfer.
            var policyProposal = JsonSerializer.Serialize(new {
                contractTreatment = 1, pricingTreatment = 1,
                depositTreatment = 1, tenantTreatment = 1,
                contractPolicyReference = "SIGNED-CONTRACT-DECISION-2026-001",
                pricingPolicyReference = "SIGNED-PRICING-DECISION-2026-001",
                depositPolicyReference = "SIGNED-DEPOSIT-DECISION-2026-001",
                tenantConsentReference = "SIGNED-TENANT-CONSENT-2026-001"
            });
            Guid proposedPolicyId;
            using (var body = new StringContent(policyProposal, Encoding.UTF8,
                "application/json"))
            using (var oi07Reply = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-proposal", body))
            {
                var value = await oi07Reply.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, oi07Reply.StatusCode);
                using var json = JsonDocument.Parse(value);
                proposedPolicyId = json.RootElement.GetProperty("id").GetGuid();
                Assert.False(json.RootElement.GetProperty("isApproved").GetBoolean());
                Assert.False(json.RootElement.GetProperty("isExecuted").GetBoolean());
                Assert.Equal(rosterId,
                    json.RootElement.GetProperty("rosterSnapshotId").GetGuid());
            }
            using (var body = new StringContent(policyProposal, Encoding.UTF8,
                "application/json"))
            using (var duplicatePolicy = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-proposal", body))
                Assert.False(duplicatePolicy.IsSuccessStatusCode);
            using (var readPolicy = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-proposal"))
            {
                Assert.Equal(HttpStatusCode.OK, readPolicy.StatusCode);
                using var json = JsonDocument.Parse(
                    await readPolicy.Content.ReadAsStringAsync());
                Assert.Equal(proposedPolicyId,
                    json.RootElement.GetProperty("id").GetGuid());
                Assert.False(json.RootElement.GetProperty("isExecuted").GetBoolean());
            }

            // Independent root, not the requesting operator, must decide
            // the proposed OI-07 policy. This still performs NO transfer.
            var signedRootReview = JsonSerializer.Serialize(new {
                accepted = true,
                signedReviewReference = "SIGNED-ROOT-REVIEW-OI07-2026-001",
                reason = "Approved policy selection only; finance and handover pending"
            });
            using (var body = new StringContent(signedRootReview, Encoding.UTF8,
                "application/json"))
            using (var oi07SelfApprove = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-review", body))
                Assert.Equal(HttpStatusCode.Forbidden, oi07SelfApprove.StatusCode);
            using (var body = new StringContent(signedRootReview, Encoding.UTF8,
                "application/json"))
            using (var oi07RootApproval = await clientRoot.PostAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-review", body))
            {
                var raw = await oi07RootApproval.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, oi07RootApproval.StatusCode);
                using var doc = JsonDocument.Parse(raw);
                Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
                Assert.False(doc.RootElement.GetProperty(
                    "isTransferExecuted").GetBoolean());
                Assert.Equal(proposedPolicyId,
                    doc.RootElement.GetProperty("policyId").GetGuid());
            }
            using (var body = new StringContent(signedRootReview, Encoding.UTF8,
                "application/json"))
            using (var oi07Replay = await clientRoot.PostAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-review", body))
                Assert.False(oi07Replay.IsSuccessStatusCode);
            using (var oi07ReviewRead = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/business-policy-review"))
            {
                Assert.Equal(HttpStatusCode.OK, oi07ReviewRead.StatusCode);
                using var doc = JsonDocument.Parse(
                    await oi07ReviewRead.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
            }

            using (var executionPreflight = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/execution-readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, executionPreflight.StatusCode);
                using var doc = JsonDocument.Parse(
                    await executionPreflight.Content.ReadAsStringAsync());
                Assert.False(doc.RootElement.GetProperty(
                    "canExecuteTransfer").GetBoolean());
                Assert.False(doc.RootElement.GetProperty(
                    "isExternalBankVerification").GetBoolean());
                Assert.Contains(doc.RootElement.GetProperty("blockers")
                    .EnumerateArray(), x => x.GetString() ==
                    "SourcePhysicalHandoverNotVerified");
                Assert.Contains(doc.RootElement.GetProperty("blockers")
                    .EnumerateArray(), x => x.GetString() ==
                    "SourceTransferMonthNotOfficiallyPosted");
            }

            // The transfer writer exists but must refuse an unearned
            // future-dated move without physical/financial evidence.
            var unauthorizedComplete = JsonSerializer.Serialize(new {
                targetMonthlyRent = 2800000m, sourceRoomDisposition = 1,
                dispositionEvidenceReference = "SIGNED-CLEAN-INSPECTION-001",
                completionReference = "ROOM-CHANGE-COMPLETION-001"
            });
            using (var body = new StringContent(unauthorizedComplete,
                Encoding.UTF8, "application/json"))
            using (var prematureTransfer = await clientRoot.PostAsync(
                ChangeAt(roomChangeRequest) + "/complete-room-change", body))
                Assert.False(prematureTransfer.IsSuccessStatusCode);

            // Do not accept an unearned physical handover: request is
            // future-dated and no room-source final E/W readings exist.
            var handoverInput = JsonSerializer.Serialize(new {
                inspectionEvidenceReference = "SIGNED-INSPECTION-2026-001",
                signedHandoverReference = "SIGNED-HANDOVER-2026-001"
            });
            using (var body = new StringContent(handoverInput, Encoding.UTF8,
                "application/json"))
            using (var deniedHandover = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/physical-handover", body))
                Assert.Equal(HttpStatusCode.Forbidden, deniedHandover.StatusCode);
            using (var missingHandover = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/physical-handover"))
                Assert.Equal(HttpStatusCode.Forbidden, missingHandover.StatusCode);

            using (var duplicateRoster = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/tenant-roster", null))
                Assert.False(duplicateRoster.IsSuccessStatusCode);
            using (var rosterRead = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/tenant-roster"))
            {
                Assert.Equal(HttpStatusCode.OK, rosterRead.StatusCode);
                using var doc = JsonDocument.Parse(
                    await rosterRead.Content.ReadAsStringAsync());
                Assert.Equal(rosterId, doc.RootElement.GetProperty("id").GetGuid());
            }

            using (var live = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/live-readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                using var doc = JsonDocument.Parse(
                    await live.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty(
                    "canContinuePolicyReview").GetBoolean());
                Assert.True(doc.RootElement.GetProperty(
                    "transferExecutionBlocked").GetBoolean());
                Assert.Empty(doc.RootElement.GetProperty(
                    "blockers").EnumerateArray());
            }
            // A reservation/maintenance state may change after intake.
            // Readiness MUST re-read actual locked rows, not trust a
            // client-provided "available" flag from the original request.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var targetRoom = await db.Rooms.SingleAsync(r => r.Id == changeTarget);
                targetRoom.ChangeStatus(BuildingManagement.Rooms.RoomStatus.Maintenance);
                await uow.CompleteAsync();
            }
            using (var live = await clientB.GetAsync(
                ChangeAt(roomChangeRequest) + "/live-readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                using var doc = JsonDocument.Parse(
                    await live.Content.ReadAsStringAsync());
                Assert.False(doc.RootElement.GetProperty(
                    "canContinuePolicyReview").GetBoolean());
                Assert.Contains(doc.RootElement.GetProperty(
                    "blockers").EnumerateArray(),
                    x => x.GetString() == "TargetRoomNoLongerAvailable");
                Assert.True(doc.RootElement.GetProperty(
                    "transferExecutionBlocked").GetBoolean());
            }
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var targetRoom = await db.Rooms.SingleAsync(r => r.Id == changeTarget);
                targetRoom.ChangeStatus(BuildingManagement.Rooms.RoomStatus.Available);
                await uow.CompleteAsync();
            }

            using (var body = new StringContent(
                """{"reviewNotes":"Deposit policy OI-07 not yet approved"}""",
                Encoding.UTF8, "application/json"))
            using (var review = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/policy-hold", body))
            {
                Assert.Equal(HttpStatusCode.OK, review.StatusCode);
                using var doc = JsonDocument.Parse(
                    await review.Content.ReadAsStringAsync());
                Assert.Equal(1, doc.RootElement.GetProperty("status").GetInt32());
            }
            using (var body = new StringContent(
                """{"reviewNotes":"Reviewer rejected without deposit disposal"}""",
                Encoding.UTF8, "application/json"))
            using (var rejection = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/reject", body))
            {
                Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);
                using var doc = JsonDocument.Parse(
                    await rejection.Content.ReadAsStringAsync());
                Assert.Equal(2, doc.RootElement.GetProperty("status").GetInt32());
            }
            using (var revive = await clientB.PostAsync(
                ChangeAt(roomChangeRequest) + "/cancel", null))
                Assert.False(revive.IsSuccessStatusCode);
            using (var list = await clientB.GetAsync(ChangeRoute(cancelContract)))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                using var doc = JsonDocument.Parse(
                    await list.Content.ReadAsStringAsync());
                Assert.Contains(doc.RootElement.EnumerateArray(),
                    x => x.GetProperty("id").GetGuid() == roomChangeRequest);
            }

            Guid cancelChange;
            using (var payload = new StringContent(changeInput, Encoding.UTF8,
                "application/json"))
            using (var created = await clientB.PostAsync(
                ChangeRoute(cancelContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                using var doc = JsonDocument.Parse(
                    await created.Content.ReadAsStringAsync());
                cancelChange = doc.RootElement.GetProperty("id").GetGuid();
            }
            using (var cancelled = await clientB.PostAsync(
                ChangeAt(cancelChange) + "/cancel", null))
            {
                Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
                using var doc = JsonDocument.Parse(
                    await cancelled.Content.ReadAsStringAsync());
                Assert.Equal(3, doc.RootElement.GetProperty("status").GetInt32());
            }
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Active,
                    (await db.Contracts.SingleAsync(c => c.Id == cancelContract)).Status);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Occupied,
                    (await db.Rooms.SingleAsync(r => r.Id == cancelRoom)).Status);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Available,
                    (await db.Rooms.SingleAsync(r => r.Id == changeTarget)).Status);
                Assert.Equal(2, await db.RoomChangeRequests.CountAsync(x =>
                    x.SourceContractId == cancelContract));
                await uow.CompleteAsync();
            }


            // PHASE 2H POSITIVE HTTP ACCEPTANCE: the same isolated ABP Host,
            // real Identity lease, actual EF migrations and actual deferred
            // PostgreSQL commit trigger. Only submission time is simulated:
            // the request is created at month-end; financial review, handover
            // and commit happen on the real clock after that date. This never
            // changes the real production clock or permits backdated requests.
            var transferSourceRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "117",
                    name = "2H atomic HTTP source", area = 25m, capacity = 2,
                    status = (int)BuildingManagement.Rooms.RoomStatus.Occupied });
            var transferTargetRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "118",
                    name = "2H atomic HTTP target", area = 25m, capacity = 2,
                    status = (int)BuildingManagement.Rooms.RoomStatus.Available });
            var transferContract = Guid.NewGuid();
            var transferMeterE = Guid.NewGuid();
            var transferMeterW = Guid.NewGuid();
            var transferEndE = Guid.NewGuid();
            var transferEndW = Guid.NewGuid();
            var transferDepositPayment = Guid.NewGuid();
            const decimal transferRent = 3_000_000m;
            const decimal transferBill = 3_083_000m;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var contract = new BuildingManagement.Contracts.Contract(
                    transferContract, "TRF-PG-HTTP-" + transferContract.ToString("N"),
                    transferSourceRoom, new DateTime(2026, 9, 1), null, transferRent);
                contract.Activate();
                db.Contracts.Add(contract);
                db.ContractTenants.Add(new BuildingManagement.Contracts.ContractTenant(
                    Guid.NewGuid(), transferContract, tenantB,
                    BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant));
                db.UtilityMeters.AddRange(
                    new BuildingManagement.Utilities.UtilityMeter(
                        transferMeterE, transferSourceRoom,
                        BuildingManagement.Utilities.UtilityType.Electricity,
                        "TRF-E-" + transferMeterE.ToString("N"), 0m,
                        new DateTime(2026, 9, 1)),
                    new BuildingManagement.Utilities.UtilityMeter(
                        transferMeterW, transferSourceRoom,
                        BuildingManagement.Utilities.UtilityType.Water,
                        "TRF-W-" + transferMeterW.ToString("N"), 0m,
                        new DateTime(2026, 9, 1)));
                db.MeterReadings.AddRange(
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), transferMeterE, 2026, 9,
                        0m, 10m, new DateTime(2026, 9, 30)),
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), transferMeterW, 2026, 9,
                        0m, 15m, new DateTime(2026, 9, 30)),
                    new BuildingManagement.Utilities.MeterReading(
                        transferEndE, transferMeterE, 2026, 10,
                        10m, 20m, new DateTime(2026, 10, 31)),
                    new BuildingManagement.Utilities.MeterReading(
                        transferEndW, transferMeterW, 2026, 10,
                        15m, 19m, new DateTime(2026, 10, 31)));
                var payment = new BuildingManagement.Payments.Payment(
                    transferDepositPayment,
                    "TRF-DEPOSIT-" + transferDepositPayment.ToString("N"),
                    tenantB, BuildingManagement.Payments.PaymentPurpose.ContractDeposit,
                    BuildingManagement.Payments.BuildingManagement.Payments
                        .PaymentMethod.BankTransfer,
                    BuildingManagement.Payments.PaymentChannel.Manual,
                    transferRent, contractId: transferContract,
                    transactionReference: "TRF-PG-DEPOSIT-" +
                        transferDepositPayment.ToString("N"));
                payment.Complete(DateTime.UtcNow.AddDays(-5),
                    "TRF-PG-DEPOSIT-" + transferDepositPayment.ToString("N"));
                db.Payments.Add(payment);
                await db.SaveChangesAsync();
                var allocated = new BuildingManagement.DepositSettlements.DepositSettlement(
                    Guid.NewGuid(),
                    BuildingManagement.DepositSettlements.DepositSettlementType
                        .AllocateToContractDeposit, transferRent,
                    paymentId: transferDepositPayment,
                    targetContractId: transferContract,
                    notes: "Signed transfer source deposit");
                allocated.Complete(DateTime.UtcNow.AddDays(-4));
                db.DepositSettlements.Add(allocated);
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }
            async Task<JsonDocument> Positive2HPostAsync(
                HttpClient caller, string url, object payload)
            {
                var (status, data) = await PostFinalAsync(
                    caller, url, JsonSerializer.Serialize(payload));
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created,
                    "2H positive POST " + url + ": " + status + " " + data);
                return JsonDocument.Parse(data);
            }
            // H4 REAL AUTHENTICATED POSTGRESQL INTENT: source cash comes
            // only from completed, locked source allocations. The target
            // required deposit is a signed root *proposal*, not bank money.
            var h4SourceRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "119",
                    name = "2H H4 source proof", area = 24m, capacity = 2,
                    status = (int)BuildingManagement.Rooms.RoomStatus.Occupied });
            var h4TargetRoom = await CreateChildAsync(clientB, roomCreate,
                new { floorId = floorB, roomNumber = "120",
                    name = "2H H4 target proof", area = 24m, capacity = 2,
                    status = (int)BuildingManagement.Rooms.RoomStatus.Available });
            var h4SourceContract = Guid.NewGuid();
            var h4DepositPayment = Guid.NewGuid();
            var h4MeterE = Guid.NewGuid();
            var h4MeterW = Guid.NewGuid();
            var h4EndE = Guid.NewGuid();
            var h4EndW = Guid.NewGuid();
            using (var h4Scope = app.Services.CreateScope())
            {
                var sp = h4Scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var c = new BuildingManagement.Contracts.Contract(
                    h4SourceContract,
                    "TRF-H4-HTTP-" + h4SourceContract.ToString("N"),
                    h4SourceRoom, new DateTime(2026, 9, 1), null, 3_000_000m);
                c.Activate();
                db.Contracts.Add(c);
                db.ContractTenants.Add(new BuildingManagement.Contracts.ContractTenant(
                    Guid.NewGuid(), h4SourceContract, tenantB,
                    BuildingManagement.Contracts.ContractTenantRole.PrimaryTenant));
                // Original source readings have actual mid-month dates:
                // handover evidence, frozen invoice and E/W must refer to
                // these exact immutable original measurements.
                db.UtilityMeters.AddRange(
                    new BuildingManagement.Utilities.UtilityMeter(
                        h4MeterE, h4SourceRoom,
                        BuildingManagement.Utilities.UtilityType.Electricity,
                        "H4-E-" + h4MeterE.ToString("N"), 0m,
                        new DateTime(2026, 9, 1)),
                    new BuildingManagement.Utilities.UtilityMeter(
                        h4MeterW, h4SourceRoom,
                        BuildingManagement.Utilities.UtilityType.Water,
                        "H4-W-" + h4MeterW.ToString("N"), 0m,
                        new DateTime(2026, 9, 1)));
                db.MeterReadings.AddRange(
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), h4MeterE, 2026, 9, 0m, 10m,
                        new DateTime(2026, 9, 30)),
                    new BuildingManagement.Utilities.MeterReading(
                        Guid.NewGuid(), h4MeterW, 2026, 9, 0m, 15m,
                        new DateTime(2026, 9, 30)),
                    new BuildingManagement.Utilities.MeterReading(
                        h4EndE, h4MeterE, 2026, 10, 10m, 20m,
                        new DateTime(2026, 10, 15)),
                    new BuildingManagement.Utilities.MeterReading(
                        h4EndW, h4MeterW, 2026, 10, 15m, 19m,
                        new DateTime(2026, 10, 15)));
                var payment = new BuildingManagement.Payments.Payment(
                    h4DepositPayment,
                    "TRF-H4-DEPOSIT-" + h4DepositPayment.ToString("N"),
                    tenantB, BuildingManagement.Payments.PaymentPurpose.ContractDeposit,
                    BuildingManagement.Payments.BuildingManagement.Payments.PaymentMethod.BankTransfer,
                    BuildingManagement.Payments.PaymentChannel.Manual,
                    3_000_000m, contractId: h4SourceContract,
                    transactionReference: "TRF-H4-ACCOUNTED-" +
                        h4DepositPayment.ToString("N"));
                payment.Complete(new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc),
                    "TRF-H4-ACCOUNTED-" + h4DepositPayment.ToString("N"));
                db.Payments.Add(payment);
                await db.SaveChangesAsync();
                var allocated = new BuildingManagement.DepositSettlements.DepositSettlement(
                    Guid.NewGuid(),
                    BuildingManagement.DepositSettlements.DepositSettlementType
                        .AllocateToContractDeposit, 3_000_000m,
                    paymentId: h4DepositPayment,
                    targetContractId: h4SourceContract,
                    notes: "H4 source completed internal deposit allocation");
                allocated.Complete(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));
                db.DepositSettlements.Add(allocated);
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }
            var h4Route = ChangeRoute(h4SourceContract);
            Guid h4Request;
            // Backdate only this disposable HTTP test clock for submission.
            // The real PostgreSQL commit clock is never changed.
            transferClock.SetUtc(new DateTimeOffset(
                2026, 10, 15, 4, 0, 0, TimeSpan.Zero));
            try
            {
                using var request = await Positive2HPostAsync(clientB, h4Route,
                    new { targetRoomId = h4TargetRoom,
                        requestedDate = "2026-10-15",
                        reason = "Independently signed deposit delta proposal" });
                h4Request = request.RootElement.GetProperty("id").GetGuid();
            }
            finally { transferClock.Reset(); }
            var h4At = h4Route + "/" + h4Request.ToString("D");
            using (var roster = await Positive2HPostAsync(clientB,
                h4At + "/tenant-roster", new { }))
                Assert.Equal(1, roster.RootElement.GetProperty("rosterCount").GetInt32());
            using (var proposal = await Positive2HPostAsync(clientB,
                h4At + "/business-policy-proposal", new {
                    contractTreatment = 1, pricingTreatment = 2,
                    depositTreatment = 2, tenantTreatment = 1,
                    contractPolicyReference = "OWNER-SIGNED-H4-NEW-CONTRACT-2027",
                    pricingPolicyReference = "OWNER-SIGNED-H4-PRORATION-2027",
                    depositPolicyReference = "OWNER-SIGNED-H4-TARGET-DEPOSIT-2027",
                    tenantConsentReference = "TENANT-SIGNED-H4-ROSTER-2027"
                }))
                Assert.False(proposal.RootElement.GetProperty("isApproved").GetBoolean());
            using (var approved = await Positive2HPostAsync(clientRoot,
                h4At + "/business-policy-review", new {
                    accepted = true,
                    signedReviewReference = "OWNER-INDEPENDENT-H4-REVIEW-2027",
                    reason = "Approved exclusive H4 target deposit decision only"
                }))
                Assert.True(approved.RootElement.GetProperty("accepted").GetBoolean());
            using (var day = await Positive2HPostAsync(clientB,
                h4At + "/day-pricing-rule-candidate", new {
                    targetMonthlyRentVnd = 4_000_000m,
                    transferDayOwner = 2, rounding = 1
                }))
                Assert.False(day.RootElement.GetProperty("canExecuteRoomChange").GetBoolean());

            var h4IntentUrl = h4At + "/h4-disposition-intent";
            var h4IntentInput = JsonSerializer.Serialize(new {
                targetRequiredDepositVnd = 4_000_000m,
                proposedTopUpDueDateVietnam = "2026-11-15",
                surplusChoice = 0,
                ownerSignedDispositionReference = "ROOT-SIGNED-H4-DEFERRED-TOPUP-2027"
            });
            var invalidH4Input = JsonSerializer.Serialize(new {
                targetRequiredDepositVnd = 4_000_000m,
                proposedTopUpDueDateVietnam = "2026-11-16",
                surplusChoice = 0,
                ownerSignedDispositionReference = "ROOT-SIGNED-H4-INVALID-DUE-2027"
            });
            var deniedStaffH4 = await PostFinalAsync(
                clientB, h4IntentUrl, h4IntentInput);
            Assert.Equal(HttpStatusCode.Forbidden, deniedStaffH4.Status);
            var deniedCrossH4 = await PostFinalAsync(
                clientB, ChangeRoute(contractA) + "/" +
                    h4Request.ToString("D") + "/h4-disposition-intent",
                h4IntentInput);
            Assert.Equal(HttpStatusCode.Forbidden, deniedCrossH4.Status);
            var deniedLateH4 = await PostFinalAsync(
                clientRoot, h4IntentUrl, invalidH4Input);
            Assert.Equal(HttpStatusCode.Forbidden, deniedLateH4.Status);
            // A signed 4M target rent cannot sustain an invented 5M
            // target deposit: this must fail without reserving source money.
            var wrongTargetDeposit = await PostFinalAsync(
                clientRoot, h4IntentUrl, JsonSerializer.Serialize(new {
                    targetRequiredDepositVnd = 5_000_000m,
                    proposedTopUpDueDateVietnam = "2026-11-15",
                    surplusChoice = 0,
                    ownerSignedDispositionReference =
                        "ROOT-SIGNED-H4-IMPOSSIBLE-TARGET-DEPOSIT"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, wrongTargetDeposit.Status);
            var rootH4Intent = await PostFinalAsync(
                clientRoot, h4IntentUrl, h4IntentInput);
            Assert.Equal(HttpStatusCode.OK, rootH4Intent.Status);
            using (var intent = JsonDocument.Parse(rootH4Intent.Body))
            {
                Assert.Equal(3_000_000m, intent.RootElement.GetProperty(
                    "recordedSourceDepositVnd").GetDecimal());
                Assert.Equal(3_000_000m, intent.RootElement.GetProperty(
                    "proposedCarriedDepositVnd").GetDecimal());
                Assert.Equal(1_000_000m, intent.RootElement.GetProperty(
                    "unpaidTopUpReceivableVnd").GetDecimal());
                Assert.False(intent.RootElement.GetProperty(
                    "isBankCashVerified").GetBoolean());
                Assert.False(intent.RootElement.GetProperty(
                    "isFinancialPosting").GetBoolean());
                Assert.False(intent.RootElement.GetProperty(
                    "canExecuteTransfer").GetBoolean());
            }
            using (var deniedRead = await clientB.GetAsync(h4IntentUrl))
                Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            using (var rootRead = await clientRoot.GetAsync(h4IntentUrl))
            {
                Assert.Equal(HttpStatusCode.OK, rootRead.StatusCode);
                using var existing = JsonDocument.Parse(
                    await rootRead.Content.ReadAsStringAsync());
                Assert.Equal(64, existing.RootElement.GetProperty(
                    "evidenceSha256").GetString()!.Length);
            }
            // No H4 liability exists until one fully authorized mid-month
            // completion has atomically committed. Root, staff, and
            // cross-building probes must never invent an obligation.
            var h4ObligationUrl = h4At + "/h4-outstanding-obligation";
            using (var staffObligation = await clientB.GetAsync(h4ObligationUrl))
                Assert.Equal(HttpStatusCode.Forbidden, staffObligation.StatusCode);
            using (var rootBeforeCompletion = await clientRoot.GetAsync(h4ObligationUrl))
                Assert.Equal(HttpStatusCode.Forbidden, rootBeforeCompletion.StatusCode);
            using (var crossObligation = await clientRoot.GetAsync(
                ChangeRoute(contractA) + "/" + h4Request.ToString("D") +
                "/h4-outstanding-obligation"))
                Assert.Equal(HttpStatusCode.Forbidden, crossObligation.StatusCode);

            // The writer must deny premature execution and preserve every
            // existing source cash allocation and pending signed H4 intent.
            transferClock.SetUtc(new DateTimeOffset(
                2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
            try
            {
                var early = await PostFinalAsync(clientRoot,
                    h4At + "/complete-h4-mid-month-room-change",
                    JsonSerializer.Serialize(new {
                        targetMonthlyRent = 4_000_000m,
                        sourceRoomDisposition = 1,
                        dispositionEvidenceReference =
                            "ROOT-SIGNED-H4-SOURCE-ROOM-CLEARANCE-001",
                        completionReference =
                            "ROOT-SIGNED-H4-ATOMIC-TRANSFER-001"
                    }));
                Assert.Equal(HttpStatusCode.Forbidden, early.Status);
            }
            finally { transferClock.Reset(); }
            var h4Duplicate = await PostFinalAsync(
                clientRoot, h4IntentUrl, h4IntentInput);
            Assert.Equal(HttpStatusCode.Forbidden, h4Duplicate.Status);
            using (var h4Scope = app.Services.CreateScope())
            {
                using var uow = h4Scope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await h4Scope.ServiceProvider.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var saved = await db.RoomChangeDepositDispositionIntents.AsNoTracking()
                    .SingleAsync(x => x.RequestId == h4Request);
                saved.VerifyFingerprint();
                Assert.Equal(3_000_000m, saved.ClaimedOriginalSourceHeldVnd);
                Assert.Equal(1_000_000m, saved.ProposedUnpaidTopUpVnd);
                Assert.Empty(await db.RoomChangeMidMonthCompletions.AsNoTracking()
                    .Where(x => x.RequestId == h4Request).ToArrayAsync());
                Assert.Empty(await db.DepositSettlements.AsNoTracking()
                    .Where(x => x.SourceContractId == h4SourceContract).ToArrayAsync());
                // No H4 completion = no forged obligation or new contract.
                await using (var checkH4 = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM "BuildingManagementRoomChangeH4Obligations"
                     WHERE "RequestId"=@request
                    """,
                    (NpgsqlConnection)db.Database.GetDbConnection(),
                    (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction()))
                {
                    checkH4.Parameters.AddWithValue("request", h4Request);
                    Assert.Equal(0L, Convert.ToInt64(
                        await checkH4.ExecuteScalarAsync()));
                }
                await uow.CompleteAsync();
            }

            // H4 POSITIVE EXECUTION: independent roles + original D03 issued
            // correction + reviewed actual source receipt + signed E/W.
            // The accepted 1M target shortfall is a FUTURE obligation and is
            // never a made-up deposit payment in this isolated test.
            using (var attested = await Positive2HPostAsync(clientB,
                h4At + "/physical-handover", new {
                    inspectionEvidenceReference =
                        "INDEPENDENT-H4-SOURCE-E-W-INSPECTION-2026",
                    signedHandoverReference =
                        "STAFF-AND-TENANT-SIGNED-H4-READINGS-2026"
                }))
            {
                Assert.Equal(h4EndE, attested.RootElement.GetProperty(
                    "electricityReadingId").GetGuid());
                Assert.Equal(h4EndW, attested.RootElement.GetProperty(
                    "waterReadingId").GetGuid());
            }
            using (var signedPrice = await Positive2HPostAsync(clientRoot,
                h4At + "/signed-pricing-archive", new {
                    signedApprovalReference =
                        "ROOT-SIGNED-H4-EXACT-DAY-PRICING-2026"
                }))
                Assert.False(signedPrice.RootElement.GetProperty(
                    "canExecuteTransfer").GetBoolean());
            using (var signedPhysical = await Positive2HPostAsync(clientRoot,
                h4At + "/three-party-handover-archive", new {
                    tenantRepresentativeId = tenantB,
                    ownerSignedReference =
                        "OWNER-H4-PHYSICAL-HANDOVER-SIGNATURE-2026",
                    staffSignedReference =
                        "STAFF-H4-PHYSICAL-HANDOVER-SIGNATURE-2026",
                    tenantSignedReference =
                        "TENANT-H4-PHYSICAL-HANDOVER-SIGNATURE-2026",
                    assetDefectReviewReference =
                        "H4-INSPECTION-NO-APPROVED-UNPAID-DAMAGE-2026"
                }))
                Assert.False(signedPhysical.RootElement.GetProperty(
                    "canExecuteTransfer").GetBoolean());

            // Verify the minimum safe denial even AFTER price and handover
            // are signed: no original fully reviewed monthly receivable yet.
            var h4CompletionRoute =
                h4At + "/complete-h4-mid-month-room-change";
            var h4CompletePayload = new {
                targetMonthlyRent = 4_000_000m,
                sourceRoomDisposition = 1,
                dispositionEvidenceReference =
                    "SIGNED-H4-ORIGINAL-ROOM-AVAILABLE-2026",
                completionReference =
                    "SIGNED-H4-ORIGINAL-ATOMIC-COMPLETE-2026"
            };
            var beforeH4Receipts = await PostFinalAsync(
                clientRoot, h4CompletionRoute,
                JsonSerializer.Serialize(h4CompletePayload));
            Assert.Equal(HttpStatusCode.Forbidden, beforeH4Receipts.Status);

            // A third authenticated, building-B-scoped reviewer is required
            // to classify D03: the classifier may be neither the proposer
            // (Root) nor the independent approval reviewer (Staff B).
            var h4Classifier = Guid.NewGuid();
            using (var classifierScope = app.Services.CreateScope())
            {
                var sp = classifierScope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var identity = sp.GetRequiredService<
                    NgocTrangHouseManagementSystemDbContext>();
                identity.Users.Add(new IdentityUser(h4Classifier,
                    "classifier-" + h4Classifier.ToString("N"),
                    "classifier-" + h4Classifier.ToString("N") + "@example.test"));
                await identity.SaveChangesAsync();
                var permissions = sp.GetRequiredService<IPermissionManager>();
                await permissions.SetForUserAsync(h4Classifier,
                    BuildingManagementPermissions.Contracts.Manage, true);
                await permissions.SetForUserAsync(h4Classifier,
                    BuildingManagementPermissions.Contracts.Read, true);
                await permissions.SetForUserAsync(h4Classifier,
                    BuildingManagementPermissions.Rooms.Manage, true);
                await permissions.SetForUserAsync(h4Classifier,
                    BuildingManagementPermissions.Rooms.Read, true);
                await permissions.SetForUserAsync(h4Classifier,
                    BuildingManagementPermissions.Buildings.Read, true);
                await uow.CompleteAsync();
            }
            using var clientH4Classifier = app.GetTestClient();
            clientH4Classifier.DefaultRequestHeaders.Add(
                "X-Slice-A-Test-Actor", h4Classifier.ToString("D"));
            using (var scopedGrant = await Positive2HPostAsync(
                clientRoot, grants, new {
                    buildingId = b,
                    operatorUserId = h4Classifier
                }))
                Assert.NotEqual(Guid.Empty,
                    scopedGrant.RootElement.GetProperty("grantId").GetGuid());

            var h4Finance = FinalInvoices(h4SourceContract);
            var h4MonthlyDraftRoute = scopedContracts + "/" +
                h4SourceContract.ToString("D") +
                "/monthly-utility-drafts/2026/10";
            Guid h4Draft;
            using (var draft = await Positive2HPostAsync(
                clientB, h4MonthlyDraftRoute, new { }))
                h4Draft = draft.RootElement.GetProperty("id").GetGuid();

            Guid h4Candidate;
            decimal h4FrozenInvoiceTotal;
            using (var candidate = await Positive2HPostAsync(
                clientRoot, h4Finance + "/monthly-drafts/" +
                    h4Draft.ToString("D") + "/candidate", new {
                    contractId = h4SourceContract,
                    roomId = h4SourceRoom,
                    billingYear = 2026,
                    billingMonth = 10,
                    paymentDueDate = new DateOnly(2026, 11, 7),
                    dueDatePolicyReference =
                        "ROOT-SIGNED-H4-MONTHLY-DUE-2026",
                    auxiliaryFeeDecision = 1,
                    auxiliaryFeePolicyReference =
                        "ROOT-SIGNED-H4-NO-SERVICE-EXTRAS-2026",
                    auxiliaryFeeReviewerId = root,
                    auxiliaryCharges = Array.Empty<object>()
                }))
            {
                h4Candidate = candidate.RootElement.GetProperty(
                    "candidateId").GetGuid();
                h4FrozenInvoiceTotal = candidate.RootElement.GetProperty(
                    "frozenCandidateAmount").GetDecimal();
                Assert.True(h4FrozenInvoiceTotal >= 3_000_000m);
            }
            var h4CandidateRoute = h4Finance +
                "/monthly-candidates/" + h4Candidate.ToString("D");
            using (var checkedInvoice = await Positive2HPostAsync(
                clientB, h4CandidateRoute + "/review", new {
                    accept = true,
                    reason = "Independent original E/W invoice and rent review"
                }))
                Assert.True(checkedInvoice.RootElement.GetProperty(
                    "accepted").GetBoolean());
            Guid h4Issued;
            using (var issued = await Positive2HPostAsync(clientRoot,
                h4CandidateRoute + "/issue", new { }))
                h4Issued = issued.RootElement.GetProperty(
                    "issuedInvoiceId").GetGuid();

            // D01/D02 binding: the transfer day is charged to the TARGET;
            // 14 source days / 31 October days, rounded to whole VND.
            var h4OriginalRent = 3_000_000m;
            var h4ProratedSource = decimal.Round(
                h4OriginalRent * 14m / 31m, 0,
                MidpointRounding.AwayFromZero);
            var h4SignedDelta = h4ProratedSource - h4OriginalRent;
            var h4FinalDue = h4FrozenInvoiceTotal + h4SignedDelta;
            Assert.Equal(-1_645_161m, h4SignedDelta);
            Assert.True(h4FinalDue > 0m);
            Guid h4Adjustment;
            using (var proposal = await Positive2HPostAsync(
                clientRoot,
                h4Finance + "/monthly-issued/" + h4Issued.ToString("D") +
                "/adjustments",
                new {
                    signedDeltaAmount = h4SignedDelta,
                    reason =
                        "Binding signed D01 target-day 14/31 source rent credit",
                    sourceEvidenceReference =
                        "H4-ROOT-SIGNED-OCTOBER-SOURCE-RENT-CORRECTION"
                }))
                h4Adjustment = proposal.RootElement.GetProperty(
                    "adjustmentId").GetGuid();
            using (var review = await Positive2HPostAsync(
                clientB, h4Finance + "/monthly-adjustments/" +
                    h4Adjustment.ToString("D") + "/review", new {
                    accept = true,
                    reason = "Independent source rent proration accepted"
                }))
                Assert.True(review.RootElement.GetProperty(
                    "accepted").GetBoolean());
            using (var categoryArchive = await Positive2HPostAsync(
                clientH4Classifier, h4At + "/classified-d03-archive",
                new {
                    adjustmentId = h4Adjustment,
                    signedClassificationReference =
                        "THIRD-REVIEWER-H4-D03-RENT-CREDIT-SIGNATURE",
                    lines = new [] {
                        new {
                            category = 1,
                            signedDeltaVnd = h4SignedDelta,
                            signedCategoryEvidenceReference =
                                "THIRD-REVIEWER-H4-RENT-CREDIT-ONLY"
                        }
                    }
                }))
                Assert.False(categoryArchive.RootElement.GetProperty(
                    "canExecuteTransfer").GetBoolean());
            using (var postedCorrection = await Positive2HPostAsync(
                clientRoot, h4At + "/post-d03-adjustment",
                new { adjustmentId = h4Adjustment }))
            {
                Assert.Equal(h4SignedDelta,
                    postedCorrection.RootElement.GetProperty(
                        "signedAmountVnd").GetDecimal());
                Assert.False(postedCorrection.RootElement.GetProperty(
                    "isExternalBankPayment").GetBoolean());
            }

            Guid h4ReceiptAllocation;
            using (var money = await Positive2HPostAsync(
                clientRoot, h4Finance + "/monthly-issued/" +
                    h4Issued.ToString("D") + "/manual-receipts", new {
                    tenantId = tenantB,
                    amount = h4FinalDue,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "H4-OCT-SOURCE-BANK-RECEIPT-" +
                        h4SourceContract.ToString("N"),
                    bankEvidenceReference = "H4-OCT-SOURCE-BANK-PROOF-" +
                        h4SourceContract.ToString("N")
                }))
                h4ReceiptAllocation = money.RootElement.GetProperty(
                    "allocationJournalId").GetGuid();
            Guid h4Receivable;
            using (var posted = await Positive2HPostAsync(clientRoot,
                h4Finance + "/monthly-issued/" + h4Issued.ToString("D") +
                "/post-receivable", new { }))
                h4Receivable = posted.RootElement.GetProperty("postingId").GetGuid();
            using (var verifiedReceipt = await Positive2HPostAsync(
                clientB, h4Finance + "/receivables/" +
                    h4Receivable.ToString("D") + "/allocations/" +
                    h4ReceiptAllocation.ToString("D") + "/review",
                new {
                    accept = true,
                    bankStatementEvidenceReference =
                        "STAFF-SIGNED-H4-PAID-SOURCE-RECEIVABLE-2026",
                    reason = "Independently matched original source receipt"
                }))
                Assert.True(verifiedReceipt.RootElement.GetProperty(
                    "accepted").GetBoolean());

            // Complete must remain Root-only even with all financial proof.
            var staffH4Complete = await PostFinalAsync(clientB,
                h4CompletionRoute, JsonSerializer.Serialize(h4CompletePayload));
            Assert.Equal(HttpStatusCode.Forbidden, staffH4Complete.Status);
            var wrongH4Amount = await PostFinalAsync(clientRoot,
                h4CompletionRoute, JsonSerializer.Serialize(new {
                    targetMonthlyRent = 4_000_001m,
                    sourceRoomDisposition = 1,
                    dispositionEvidenceReference =
                        "SIGNED-H4-ORIGINAL-ROOM-AVAILABLE-2026",
                    completionReference =
                        "SIGNED-H4-WRONG-RENT-REPLAY-2026"
                }));
            Assert.Equal(HttpStatusCode.Forbidden, wrongH4Amount.Status);

            // Serializable competition: exactly one H4 contract/obligation
            // transaction can win; the other must fail closed.
            var h4Attempts = await Task.WhenAll(
                PostFinalAsync(clientRoot, h4CompletionRoute,
                    JsonSerializer.Serialize(h4CompletePayload)),
                PostFinalAsync(clientRoot, h4CompletionRoute,
                    JsonSerializer.Serialize(h4CompletePayload)));
            var h4Winners = h4Attempts.Where(x =>
                x.Status is HttpStatusCode.OK or HttpStatusCode.Created).ToArray();
            Assert.Single(h4Winners);
            Assert.Single(h4Attempts.Where(x =>
                x.Status is not (HttpStatusCode.OK or HttpStatusCode.Created)));
            Guid h4TargetContract;
            using (var completed = JsonDocument.Parse(h4Winners[0].Body))
            {
                Assert.Equal(h4SourceContract,
                    completed.RootElement.GetProperty("sourceContractId").GetGuid());
                Assert.Equal(3_000_000m,
                    completed.RootElement.GetProperty(
                        "carriedDepositAmount").GetDecimal());
                Assert.True(completed.RootElement.GetProperty(
                    "isCompleted").GetBoolean());
                h4TargetContract = completed.RootElement.GetProperty(
                    "targetContractId").GetGuid();
            }
            var h4Replay = await PostFinalAsync(clientRoot,
                h4CompletionRoute, JsonSerializer.Serialize(h4CompletePayload));
            Assert.Equal(HttpStatusCode.Forbidden, h4Replay.Status);

            using (var pendingObligation = await clientRoot.GetAsync(h4ObligationUrl))
            {
                var body = await pendingObligation.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, pendingObligation.StatusCode);
                using var doc = JsonDocument.Parse(body);
                Assert.Equal(1, doc.RootElement.GetProperty("kind").GetInt32());
                Assert.Equal(1_000_000m, doc.RootElement.GetProperty(
                    "amountVnd").GetDecimal());
                Assert.Equal(3_000_000m, doc.RootElement.GetProperty(
                    "carriedDepositVnd").GetDecimal());
                Assert.Equal("2026-11-15", doc.RootElement.GetProperty(
                    "dueDateVietnam").GetString());
                Assert.Equal(h4TargetContract, doc.RootElement.GetProperty(
                    "targetContractId").GetGuid());
                Assert.True(doc.RootElement.GetProperty(
                    "isOutstanding").GetBoolean());
                Assert.False(doc.RootElement.GetProperty(
                    "isExternallyCollected").GetBoolean());
                Assert.False(doc.RootElement.GetProperty(
                    "isRefundPaid").GetBoolean());
                Assert.False(doc.RootElement.GetProperty(
                    "isRentCreditApplied").GetBoolean());
                Assert.False(doc.RootElement.GetProperty(
                    "canSettleDeposit").GetBoolean());
            }
            using (var staffRead = await clientB.GetAsync(h4ObligationUrl))
                Assert.Equal(HttpStatusCode.Forbidden, staffRead.StatusCode);

            using (var h4Scope = app.Services.CreateScope())
            {
                using var uow = h4Scope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await h4Scope.ServiceProvider.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var oldContract = await db.Contracts.SingleAsync(x =>
                    x.Id == h4SourceContract);
                var newContract = await db.Contracts.SingleAsync(x =>
                    x.Id == h4TargetContract);
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Ended,
                    oldContract.Status);
                Assert.Equal(BuildingManagement.Contracts.ContractEndReason.RoomChanged,
                    oldContract.EndReason);
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Active,
                    newContract.Status);
                Assert.Equal(h4SourceContract, newContract.RenewedFromContractId);
                Assert.Equal(new DateTime(2026, 10, 15), newContract.StartDate);
                Assert.Equal(4_000_000m, newContract.DepositAmount);
                var carry = await db.DepositSettlements.SingleAsync(x =>
                    x.SourceContractId == h4SourceContract);
                Assert.Equal(3_000_000m, carry.Amount);
                Assert.Equal(h4TargetContract, carry.TargetContractId);
                Assert.Equal(2, await db.ContractTenants.AsNoTracking()
                    .Where(x => x.ContractId == h4SourceContract ||
                        x.ContractId == h4TargetContract).CountAsync());
                var complete = await db.RoomChangeMidMonthCompletions
                    .SingleAsync(x => x.RequestId == h4Request);
                complete.VerifyFingerprint();
                Assert.Equal(h4SignedDelta, complete.SourceSignedAdjustmentVnd);
                Assert.Equal(h4FinalDue, complete.SourceReviewedReceiptsVnd);
                Assert.Equal(1_000_000m, complete.H4UnpaidTopUpVnd);
                Assert.Equal(0m, complete.H4NextMonthRentCreditVnd);
                Assert.Equal(0m, complete.H4ManualRefundLiabilityVnd);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Available,
                    (await db.Rooms.SingleAsync(x => x.Id == h4SourceRoom)).Status);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Occupied,
                    (await db.Rooms.SingleAsync(x => x.Id == h4TargetRoom)).Status);
                Assert.Equal(1, await db.MonthlyInvoiceAdjustmentDecisions
                    .CountAsync(x => x.ContractId == h4SourceContract &&
                        x.Accepted));
                Assert.Equal(1, await db.OfficialInvoiceReceiptReviews
                    .CountAsync(x => x.ContractId == h4SourceContract &&
                        x.Accepted));
                await uow.CompleteAsync();
            }
            // Original H4 financial ledger is immutable and cannot be paid
            // simply by changing its old amount or inserting a fake refund.
            await using (var h4Connection = new NpgsqlConnection(buildingCs))
            {
                await h4Connection.OpenAsync();
                await using var h4Change = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementRoomChangeH4Obligations"
                       SET "AmountVnd"="AmountVnd"+1
                     WHERE "RequestId"=@request
                    """, h4Connection);
                h4Change.Parameters.AddWithValue("request", h4Request);
                var denied = await Assert.ThrowsAsync<PostgresException>(
                    async () => await h4Change.ExecuteNonQueryAsync());
                Assert.Equal("23514", denied.SqlState);
            }

            var positiveRoute = ChangeRoute(transferContract);
            Guid positiveRequest;
            transferClock.SetUtc(new DateTimeOffset(
                2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
            try
            {
                using var submitted = await Positive2HPostAsync(clientB, positiveRoute,
                    new { targetRoomId = transferTargetRoom,
                        requestedDate = "2026-10-31",
                        reason = "Signed full-month move to the next room" });
                positiveRequest = submitted.RootElement.GetProperty("id").GetGuid();
            }
            finally { transferClock.Reset(); }
            var positiveAt = positiveRoute + "/" + positiveRequest.ToString("D");
            using (var roster = await Positive2HPostAsync(
                clientB, positiveAt + "/tenant-roster", new { }))
                Assert.Equal(1, roster.RootElement.GetProperty("rosterCount").GetInt32());
            using (var proposal = await Positive2HPostAsync(
                clientB, positiveAt + "/business-policy-proposal",
                new {
                    contractTreatment = 1, pricingTreatment = 1,
                    depositTreatment = 1, tenantTreatment = 1,
                    contractPolicyReference = "SIGNED-TRF-CONTRACT-2026-001",
                    pricingPolicyReference = "SIGNED-TRF-PRICING-2026-001",
                    depositPolicyReference = "SIGNED-TRF-DEPOSIT-2026-001",
                    tenantConsentReference = "SIGNED-TRF-TENANT-2026-001"
                }))
                Assert.False(proposal.RootElement.GetProperty("isApproved").GetBoolean());
            using (var review = await Positive2HPostAsync(
                clientRoot, positiveAt + "/business-policy-review", new {
                    accepted = true,
                    signedReviewReference = "SIGNED-TRF-ROOT-REVIEW-2026-001",
                    reason = "Independently verified whole-month same-rent and deposit"
                }))
                Assert.True(review.RootElement.GetProperty("accepted").GetBoolean());
            using (var handover = await Positive2HPostAsync(
                clientB, positiveAt + "/physical-handover", new {
                    inspectionEvidenceReference = "SIGNED-TRF-INSPECTION-2026-001",
                    signedHandoverReference = "SIGNED-TRF-HANDOVER-2026-001"
                }))
            {
                Assert.Equal(transferEndE,
                    handover.RootElement.GetProperty("electricityReadingId").GetGuid());
                Assert.Equal(transferEndW,
                    handover.RootElement.GetProperty("waterReadingId").GetGuid());
            }
            using (var beforeInvoice = await clientB.GetAsync(
                positiveAt + "/execution-readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, beforeInvoice.StatusCode);
                using var doc = JsonDocument.Parse(
                    await beforeInvoice.Content.ReadAsStringAsync());
                Assert.False(doc.RootElement.GetProperty("canExecuteTransfer").GetBoolean());
                Assert.Contains(doc.RootElement.GetProperty("blockers").EnumerateArray(),
                    x => x.GetString() == "SourceTransferMonthNotOfficiallyPosted");
            }

            // Issue the transfer-month invoice using the existing 2G.3
            // root/independent-review HTTP routes and real monthly meter
            // history, not a fake settled invoice inserted by the test.
            var transferDraftRoute = scopedContracts + "/" +
                transferContract.ToString("D") + "/monthly-utility-drafts/2026/10";
            Guid transferDraft;
            using (var draft = await Positive2HPostAsync(
                clientB, transferDraftRoute, new { }))
                transferDraft = draft.RootElement.GetProperty("id").GetGuid();
            var transferFinance = FinalInvoices(transferContract);
            Guid transferCandidate;
            using (var candidate = await Positive2HPostAsync(
                clientRoot, transferFinance + "/monthly-drafts/" +
                    transferDraft.ToString("D") + "/candidate",
                new {
                    contractId = transferContract, roomId = transferSourceRoom,
                    billingYear = 2026, billingMonth = 10,
                    paymentDueDate = new DateOnly(2026, 11, 7),
                    dueDatePolicyReference = "SIGNED-TRF-MONTH-DUE-001",
                    auxiliaryFeeDecision = 1,
                    auxiliaryFeePolicyReference = "SIGNED-TRF-NO-EXTRAS-001",
                    auxiliaryFeeReviewerId = root,
                    auxiliaryCharges = Array.Empty<object>()
                }))
            {
                transferCandidate = candidate.RootElement
                    .GetProperty("candidateId").GetGuid();
                Assert.Equal(transferBill, candidate.RootElement
                    .GetProperty("frozenCandidateAmount").GetDecimal());
            }
            var transferCandidateRoute = transferFinance +
                "/monthly-candidates/" + transferCandidate.ToString("D");
            using (var reviewed = await Positive2HPostAsync(clientB,
                transferCandidateRoute + "/review", new {
                    accept = true,
                    reason = "Independently checked transfer-month source E and W"
                }))
                Assert.True(reviewed.RootElement.GetProperty("accepted").GetBoolean());
            Guid transferIssued;
            using (var transferIssueResponse = await Positive2HPostAsync(clientRoot,
                transferCandidateRoute + "/issue", new { }))
                transferIssued = transferIssueResponse.RootElement.GetProperty("issuedInvoiceId").GetGuid();
            Guid transferPaymentJournal;
            using (var receipt = await Positive2HPostAsync(clientRoot,
                transferFinance + "/monthly-issued/" +
                    transferIssued.ToString("D") + "/manual-receipts",
                new {
                    tenantId = tenantB, amount = transferBill,
                    paidAtUtc = DateTime.UtcNow,
                    transactionReference = "TRF-MONTH-RECEIPT-" +
                        transferContract.ToString("N"),
                    bankEvidenceReference = "SIGNED-TRF-BANK-" +
                        transferContract.ToString("N")
                }))
                transferPaymentJournal = receipt.RootElement
                    .GetProperty("allocationJournalId").GetGuid();
            Guid transferPosting;
            using (var posted = await Positive2HPostAsync(clientRoot,
                transferFinance + "/monthly-issued/" +
                    transferIssued.ToString("D") + "/post-receivable", new { }))
                transferPosting = posted.RootElement.GetProperty("postingId").GetGuid();
            Guid transferReceiptReview;
            using (var accepted = await Positive2HPostAsync(clientB,
                transferFinance + "/receivables/" +
                    transferPosting.ToString("D") + "/allocations/" +
                    transferPaymentJournal.ToString("D") + "/review",
                new {
                    accept = true,
                    bankStatementEvidenceReference = "SIGNED-TRF-BANK-REVIEW-" +
                        transferContract.ToString("N"),
                    reason = "Independent reconciliation of transfer-month receipt"
                }))
            {
                Assert.True(accepted.RootElement.GetProperty("accepted").GetBoolean());
                transferReceiptReview = accepted.RootElement.GetProperty("reviewId").GetGuid();
            }
            // True OI-04 HTTP + PostgreSQL positive: independent staff
            // bank approval, then owner signs a complete exact fee split.
            decimal[] feeAmounts;
            using (var splitScope = app.Services.CreateScope())
            {
                using var uow = splitScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await splitScope.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var frozen = await db.MonthlyInvoiceCandidates.AsNoTracking()
                    .SingleAsync(x => x.Id == transferCandidate);
                feeAmounts = new[] { frozen.RentAmount, frozen.ElectricityAmount,
                    frozen.WaterAmount, frozen.AuxiliaryAmount };
                Assert.Equal(transferBill, feeAmounts.Sum());
                await uow.CompleteAsync();
            }
            var splitLines = feeAmounts.Select((amount, index) =>
                new { category = index + 1, amountVnd = amount })
                .Where(x => x.amountVnd > 0m).ToArray();
            var splitRoute = transferFinance + "/receivables/" +
                transferPosting.ToString("D") + "/receipt-reviews/" +
                transferReceiptReview.ToString("D") + "/line-split";
            var splitPayload = JsonSerializer.Serialize(new {
                signedAllocationReference = "SIGNED-OWNER-TRF-RECEIPT-SPLIT-001",
                lines = splitLines
            });
            var selfBankReviewer = await PostFinalAsync(
                clientB, splitRoute, splitPayload);
            Assert.Equal(HttpStatusCode.Forbidden, selfBankReviewer.Status);
            var ownerReceiptSplitResponse = await PostFinalAsync(clientRoot, splitRoute, splitPayload);
            Assert.True(ownerReceiptSplitResponse.Status is HttpStatusCode.OK or HttpStatusCode.Created,
                "Signed receipt line split: " + ownerReceiptSplitResponse.Status + " " + ownerReceiptSplitResponse.Body);
            using (var document = JsonDocument.Parse(ownerReceiptSplitResponse.Body))
            {
                Assert.Equal(transferBill, document.RootElement
                    .GetProperty("allocatedAmountVnd").GetDecimal());
                Assert.False(document.RootElement
                    .GetProperty("canPostDepositOffset").GetBoolean());
                Assert.False(document.RootElement
                    .GetProperty("isBankExternallyVerified").GetBoolean());
            }
            var replayReceiptSplitResponse = await PostFinalAsync(clientRoot, splitRoute, splitPayload);
            Assert.Equal(HttpStatusCode.OK, replayReceiptSplitResponse.Status);
            var altered = await PostFinalAsync(clientRoot, splitRoute,
                JsonSerializer.Serialize(new {
                    signedAllocationReference = "SIGNED-ALTERED-TRF-SPLIT-001",
                    lines = splitLines
                }));
            Assert.Equal(HttpStatusCode.Forbidden, altered.Status);
            using (var splitScope = app.Services.CreateScope())
            {
                using var uow = splitScope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await splitScope.ServiceProvider
                    .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var saved = await db.OfficialMonthlyReceiptLineAllocations
                    .AsNoTracking().Where(x => x.ReceiptReviewId == transferReceiptReview)
                    .ToArrayAsync();
                Assert.Equal(splitLines.Length, saved.Length);
                Assert.Equal(transferBill, saved.Sum(x => x.Amount));
                Assert.All(saved, x => Assert.Equal(root, x.AllocatedByUserId));
                await uow.CompleteAsync();
            }

            using (var readiness = await clientB.GetAsync(
                positiveAt + "/execution-readiness"))
            {
                var data = await readiness.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
                using var doc = JsonDocument.Parse(data);
                Assert.True(doc.RootElement.GetProperty("canExecuteTransfer").GetBoolean(),
                    "2H eligible execution after proven settlement: " + data);
                Assert.Empty(doc.RootElement.GetProperty("blockers").EnumerateArray());
            }

            // The 2G.5 guard's new RoomChanged exception is NOT a SQL
            // backdoor. A standalone active->ended change without a matching
            // completion ledger must fail at the deferred commit boundary.
            await using (var direct = new NpgsqlConnection(buildingCs))
            {
                await direct.OpenAsync();
                await using var tx = await direct.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable);
                await using (var end = new NpgsqlCommand(
                    """
                    UPDATE "BuildingManagementContracts"
                       SET "Status"=3, "EndReason"=4,
                           "ActualEndedAt"=@at
                     WHERE "Id"=@source
                    """, direct, tx))
                {
                    end.Parameters.AddWithValue("at", DateTime.UtcNow);
                    end.Parameters.AddWithValue("source", transferContract);
                    Assert.Equal(1, await end.ExecuteNonQueryAsync());
                }
                var mismatch = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await using var flush = new NpgsqlCommand(
                        "SET CONSTRAINTS ALL IMMEDIATE", direct, tx);
                    await flush.ExecuteNonQueryAsync();
                });
                Assert.Equal("23514", mismatch.SqlState);
                await tx.RollbackAsync();
            }
            var completePayload = new {
                targetMonthlyRent = transferRent, sourceRoomDisposition = 1,
                dispositionEvidenceReference = "SIGNED-TRF-CLEAN-ROOM-2026-001",
                completionReference = "SIGNED-TRF-ATOMIC-COMPLETE-2026-001"
            };
            var staffComplete = await PostFinalAsync(clientB,
                positiveAt + "/complete-room-change",
                JsonSerializer.Serialize(completePayload));
            Assert.Equal(HttpStatusCode.Forbidden, staffComplete.Status);
            // Two simultaneous legitimate root requests must not create two
            // contracts or transfer the same deposit twice.
            var rootCalls = await Task.WhenAll(
                PostFinalAsync(clientRoot, positiveAt + "/complete-room-change",
                    JsonSerializer.Serialize(completePayload)),
                PostFinalAsync(clientRoot, positiveAt + "/complete-room-change",
                    JsonSerializer.Serialize(completePayload)));
            var winners = rootCalls.Where(x =>
                x.Status is HttpStatusCode.OK or HttpStatusCode.Created).ToArray();
            Assert.Single(winners);
            Assert.False(rootCalls.Single(x =>
                x.Status != winners[0].Status || x.Body != winners[0].Body)
                .Status is HttpStatusCode.OK or HttpStatusCode.Created);
            Guid newTransferContract;
            using (var done = JsonDocument.Parse(winners[0].Body))
            {
                newTransferContract = done.RootElement
                    .GetProperty("targetContractId").GetGuid();
                Assert.Equal(transferContract, done.RootElement
                    .GetProperty("sourceContractId").GetGuid());
                Assert.Equal(transferRent, done.RootElement
                    .GetProperty("carriedDepositAmount").GetDecimal());
            }
            using (var replay = await clientRoot.PostAsync(
                positiveAt + "/complete-room-change",
                new StringContent(JsonSerializer.Serialize(completePayload),
                    Encoding.UTF8, "application/json")))
                Assert.False(replay.IsSuccessStatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>().GetDbContextAsync();
                var from = await db.Contracts.SingleAsync(x => x.Id == transferContract);
                var to = await db.Contracts.SingleAsync(x => x.Id == newTransferContract);
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Ended, from.Status);
                Assert.Equal(BuildingManagement.Contracts.ContractEndReason.RoomChanged,
                    from.EndReason);
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Active, to.Status);
                Assert.Equal(transferTargetRoom, to.RoomId);
                Assert.Equal(transferContract, to.RenewedFromContractId);
                Assert.Equal(new DateTime(2026, 11, 1), to.StartDate);
                Assert.Equal(transferRent, to.DepositAmount);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Available,
                    (await db.Rooms.SingleAsync(x => x.Id == transferSourceRoom)).Status);
                Assert.Equal(BuildingManagement.Rooms.RoomStatus.Occupied,
                    (await db.Rooms.SingleAsync(x => x.Id == transferTargetRoom)).Status);
                var originalTenants = await db.ContractTenants.AsNoTracking()
                    .Where(x => x.ContractId == transferContract).ToArrayAsync();
                var movedTenants = await db.ContractTenants.AsNoTracking()
                    .Where(x => x.ContractId == newTransferContract).ToArrayAsync();
                Assert.Single(originalTenants);
                Assert.Single(movedTenants);
                Assert.Equal(originalTenants[0].TenantId, movedTenants[0].TenantId);
                Assert.Equal(originalTenants[0].Role, movedTenants[0].Role);
                var carried = await db.DepositSettlements.SingleAsync(x =>
                    x.SourceContractId == transferContract);
                Assert.Equal(newTransferContract, carried.TargetContractId);
                Assert.Equal(transferRent, carried.Amount);
                Assert.Equal(BuildingManagement.DepositSettlements.DepositSettlementType
                    .RoomChangeCarryForward, carried.Type);
                Assert.Equal(1, await db.RoomChangeCompletions.CountAsync(x =>
                    x.SourceContractId == transferContract));
                var transferCompletionEvidence = await db.RoomChangeCompletions.SingleAsync(x =>
                    x.SourceContractId == transferContract);
                transferCompletionEvidence.VerifyFingerprint();
                Assert.Equal(1, await db.MonthlyIssuedInvoices.CountAsync(x =>
                    x.ContractId == transferContract));
                Assert.Equal(1, await db.OfficialInvoiceReceivables.CountAsync(x =>
                    x.ContractId == transferContract));
                Assert.Equal(1, await db.OfficialInvoiceReceiptReviews.CountAsync(x =>
                    x.ContractId == transferContract && x.Accepted));
                await uow.CompleteAsync();
            }

            // Completed Phase 2H history is an audit boundary, not just a
            // one-time consistency check at INSERT. Direct SQL attempts to
            // rewrite old rent, evict old tenant evidence, spend the already
            // carried deposit again or erase the completion must all fail.
            // These are real disposable PostgreSQL writes, not mocks.
            async Task AssertTransferredHistoryFrozenAsync(string statement)
            {
                await using var connection = new NpgsqlConnection(buildingCs);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(statement, connection);
                command.Parameters.AddWithValue("source", transferContract);
                var rejection = await Assert.ThrowsAsync<PostgresException>(
                    async () => await command.ExecuteNonQueryAsync());
                Assert.Equal("23514", rejection.SqlState);
            }
            await AssertTransferredHistoryFrozenAsync(
                """
                UPDATE "BuildingManagementContracts"
                   SET "MonthlyRent" = "MonthlyRent" + 1
                 WHERE "Id" = @source
                """);
            await AssertTransferredHistoryFrozenAsync(
                """
                DELETE FROM "BuildingManagementContractTenants"
                 WHERE "ContractId" = @source
                """);
            await AssertTransferredHistoryFrozenAsync(
                """
                UPDATE "BuildingManagementDepositSettlements"
                   SET "Amount" = "Amount" - 1
                 WHERE "SourceContractId" = @source
                """);
            await AssertTransferredHistoryFrozenAsync(
                """
                UPDATE "BuildingManagementPayments"
                   SET "Amount" = "Amount" + 1
                 WHERE "ContractId" = @source AND "Purpose" = 2
                """);
            await AssertTransferredHistoryFrozenAsync(
                """
                DELETE FROM "BuildingManagementRoomChangeCompletions"
                 WHERE "SourceContractId" = @source
                """);

            // Record the real room state rather than assuming occupancy:
            // other independent contract tests may have changed it earlier.
            BuildingManagement.Rooms.RoomStatus originalRoomStatus;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                originalRoomStatus = (await db.Rooms.SingleAsync(
                    r => r.Id == roomB)).Status;
                await uow.CompleteAsync();
            }

            // Scoped move-out scheduling and approval reuse the Contract
            // row UPDATE gate, identity permission lease and live membership.
            // They must NEVER settle a deposit or release the Room here.
            string MoveOutRoute(Guid id) => scopedContracts + "/" +
                id.ToString("D") + "/move-out-requests";
            string MoveOutBody(Guid contractId) => JsonSerializer.Serialize(new
            {
                contractId,
                requestedMoveOutDate = "2027-01-31",
                reason = "Scheduled departure"
            });
            using (var payload = new StringContent(
                MoveOutBody(contractA), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(contractA), payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var payload = new StringContent(
                MoveOutBody(contractA), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract), payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            Guid moveOutRequest;
            using (var payload = new StringContent(
                MoveOutBody(cancelContract), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                moveOutRequest = json.RootElement.GetProperty("id").GetGuid();
                Assert.NotEqual(Guid.Empty, moveOutRequest);
            }
            using (var response = await clientB.GetAsync(
                MoveOutRoute(contractA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // The original request is pending: reject it with a reason, then
            // create another and cancel it. Both must release the open-request
            // predicate without ending the contract or releasing the room.
            using (var payload = new StringContent(
                """{"reason":"Rejected after review"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(contractA) + "/" +
                moveOutRequest.ToString("D") + "/reject", payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var payload = new StringContent(
                """{"reason":"Rejected after review"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract) + "/" +
                moveOutRequest.ToString("D") + "/reject", payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal((int)BuildingManagement.MoveOuts.MoveOutRequestStatus.Rejected,
                    json.RootElement.GetProperty("status").GetInt32());
            }
            // Source-controlled read-only closeout readiness is NEVER a
            // backdoor to CompleteMoveOut or an invented final payout.
            using (var response = await clientB.GetAsync(
                MoveOutRoute(contractA) + "/" +
                moveOutRequest.ToString("D") + "/readiness"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract) + "/" +
                moveOutRequest.ToString("D") + "/readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.False(json.RootElement
                    .GetProperty("canCompleteMoveOut").GetBoolean());
                var blockers = json.RootElement.GetProperty("blockers")
                    .EnumerateArray().Select(x => x.GetString()).ToArray();
                Assert.Contains("MoveOutNotApproved", blockers);
                Assert.Contains("FinalInvoiceIssuanceNotImplemented", blockers);
            }

            // A terminal rejected request cannot be approved retroactively.
            using (var payload = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract) + "/" +
                moveOutRequest.ToString("D") + "/approve", payload))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            using (var payload = new StringContent(
                MoveOutBody(cancelContract), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                moveOutRequest = json.RootElement.GetProperty("id").GetGuid();
            }
            using (var response = await clientB.PostAsync(
                MoveOutRoute(contractA) + "/" +
                moveOutRequest.ToString("D") + "/cancel", null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract) + "/" +
                moveOutRequest.ToString("D") + "/cancel", null))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal((int)BuildingManagement.MoveOuts.MoveOutRequestStatus.Cancelled,
                    json.RootElement.GetProperty("status").GetInt32());
            }
            using (var payload = new StringContent(
                MoveOutBody(cancelContract), Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract), payload))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                moveOutRequest = json.RootElement.GetProperty("id").GetGuid();
            }
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains(moveOutRequest.ToString("D"), body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Scheduled departure", body,
                    StringComparison.OrdinalIgnoreCase);
            }
            using (var payload = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(contractA) + "/" +
                moveOutRequest.ToString("D") + "/approve", payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var payload = new StringContent("{}",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract) + "/" +
                moveOutRequest.ToString("D") + "/approve", payload))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(contractA) + "/" + moveOutRequest.ToString("D") +
                "/checklist"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract) + "/" + Guid.NewGuid().ToString("D") +
                "/checklist"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract) + "/" + moveOutRequest.ToString("D") +
                "/checklist"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                Assert.NotEmpty(json.RootElement.EnumerateArray());
                Assert.DoesNotContain("phoneNumber", body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("identityNumber", body,
                    StringComparison.OrdinalIgnoreCase);
            }
            // Final meter signoff requires BOTH verified electricity
            // and water evidence; a merely approved request has neither.
            // Foreign IDs cannot complete checklist tasks.
            using (var response = await clientB.PostAsync(
                MoveOutRoute(contractA) + "/" + moveOutRequest.ToString("D") +
                "/verify-final-meters", null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract) + "/" + Guid.NewGuid().ToString("D") +
                "/verify-final-meters", null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                MoveOutRoute(cancelContract) + "/" + moveOutRequest.ToString("D") +
                "/verify-final-meters", null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract) + "/" + moveOutRequest.ToString("D") +
                "/checklist"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.All(json.RootElement.EnumerateArray(),
                    task => Assert.False(
                        task.GetProperty("isCompleted").GetBoolean()));
            }

            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                Assert.True(await db.MoveOutChecklistTasks.AnyAsync(task =>
                    task.MoveOutRequestId == moveOutRequest));
                Assert.Equal(originalRoomStatus,
                    (await db.Rooms.SingleAsync(r => r.Id == roomB)).Status);
                Assert.Equal(BuildingManagement.Contracts.ContractStatus.Active,
                    (await db.Contracts.SingleAsync(c => c.Id == cancelContract)).Status);
                await uow.CompleteAsync();
            }

            // Cross-module invoice/deposit readiness is read-only and
            // cannot use another building's request/contract. It reports
            // known evidence gaps, not a refundable balance or payable bill.
            using (var response = await clientB.GetAsync(
                MoveOutRoute(contractA) + "/" +
                moveOutRequest.ToString("D") + "/financial-readiness"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract) + "/" +
                Guid.NewGuid().ToString("D") + "/financial-readiness"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(
                MoveOutRoute(cancelContract) + "/" +
                moveOutRequest.ToString("D") + "/financial-readiness"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                var view = json.RootElement;
                Assert.False(view.GetProperty("canIssueInvoice").GetBoolean());
                Assert.False(view.GetProperty("canRecognizeDebt").GetBoolean());
                Assert.False(view.GetProperty("canSettleDeposit").GetBoolean());
                Assert.False(view.GetProperty("canCompleteMoveOut").GetBoolean());
                Assert.NotEmpty(view.GetProperty("invoiceBlockers").EnumerateArray());
                Assert.Contains(view.GetProperty("invoiceBlockers")
                    .EnumerateArray().Select(x => x.GetString()),
                    blocker => blocker == "StagedInvoiceEvidencePersistenceNotMigrated");
                Assert.Contains(view.GetProperty("invoiceBlockers")
                    .EnumerateArray().Select(x => x.GetString()),
                    blocker => blocker == "FinalInvoiceIssuanceNotImplemented");
                Assert.NotEmpty(view.GetProperty("depositBlockers").EnumerateArray());
                Assert.DoesNotContain("phoneNumber", body,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("identityNumber", body,
                    StringComparison.OrdinalIgnoreCase);
            }

            // Identity Contracts.Manage DELETE cannot pass a Building write
            // transaction's permission lease. After revocation commits,
            // a fresh POST fails even if ABP's cache still says granted.
            Task<int>? revokeContractManage = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var principal = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (principal.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D"))
                    }, "CommitBoundContractWrite"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var draft = await sp.GetRequiredService<SliceBStagingContractMutation>()
                        .CreateAsync(new BuildingManagement.Contracts.CreateContractDto
                        {
                            ContractNumber = "STAGE-RACE-" + Guid.NewGuid().ToString("N"),
                            RoomId = roomB,
                            StartDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                            EndDate = new DateTime(2027, 10, 31, 0, 0, 0, DateTimeKind.Utc),
                            MonthlyRent = 2700000m
                        });
                    contractRace = draft.Id;
                    Assert.NotEqual(Guid.Empty, contractRace);
                    revokeContractManage = Task.Run(async () =>
                    {
                        await using var connection = new NpgsqlConnection(idCs);
                        await connection.OpenAsync();
                        await using var cmd = new NpgsqlCommand(
                            """
                            DELETE FROM "AbpPermissionGrants"
                             WHERE "ProviderName" = 'U' AND "ProviderKey" = @actor
                               AND "Name" = @permission
                            """, connection);
                        cmd.Parameters.AddWithValue("actor", operatorB.ToString("D"));
                        cmd.Parameters.AddWithValue("permission",
                            BuildingManagementPermissions.Contracts.Manage);
                        return await cmd.ExecuteNonQueryAsync();
                    });
                    await Task.Delay(250);
                    Assert.False(revokeContractManage.IsCompleted,
                        "Contracts.Manage revoke must await the Building contract commit.");
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(revokeContractManage);
            Assert.Equal(1, await revokeContractManage!
                .WaitAsync(TimeSpan.FromSeconds(15)));
            using (var body = new StringContent(CreateJson(roomB,
                "REVOKED-" + Guid.NewGuid().ToString("N")), Encoding.UTF8,
                "application/json"))
            using (var response = await clientB.PostAsync(scopedContracts, body))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Contracts.Manage, true);
                await uow.CompleteAsync();
            }

            // Cancel Draft contract, reject cross-building ID and Active
            // contract; this API is NOT a backdoor to ending occupancy.
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + contractA.ToString("D") + "/cancel", null))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + cancelContract.ToString("D") + "/cancel", null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + contractC.ToString("D") + "/cancel", null))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Equal((int)BuildingManagement.Contracts.ContractStatus.Cancelled,
                    json.RootElement.GetProperty("status").GetInt32());
            }
            using (var response = await clientB.PostAsync(
                scopedContracts + "/" + contractC.ToString("D") + "/cancel", null))
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            // Remove ONLY newly inserted disposable contract fixtures before
            // the pre-existing Room/Floor tests exercise deletion semantics.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var db = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                var disposableContracts = await db.Contracts.Where(x =>
                    x.Id == contractC || x.Id == contractRace).ToListAsync();
                Assert.Equal(2, disposableContracts.Count);
                db.Contracts.RemoveRange(disposableContracts);
                await db.SaveChangesAsync();
                await uow.CompleteAsync();
            }

            // Contract A was created solely to prove cross-building
            // isolation. Remove that SCRATCH contract before existing
            // Slice A room/floor deletion checks. Otherwise the room
            // correctly retains a contract history and cannot be deleted,
            // changing the pre-existing acceptance scenario.
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var ctx = await sp.GetRequiredService<
                    IDbContextProvider<BuildingManagementDbContext>>()
                    .GetDbContextAsync();
                // Restore the pre-existing Slice A room deletion scenario:
                // the cross-building read fixture must not leave a reservation
                // linked to Room A after its assertions complete.
                var scratchReservation = await ctx.RoomReservations.FindAsync(reservationA)
                    ?? throw new InvalidOperationException("Missing scratch reservation A.");
                ctx.RoomReservations.Remove(scratchReservation);
                var scratchContract = await ctx.Contracts.FindAsync(contractA)
                    ?? throw new InvalidOperationException("Missing scratch contract A.");
                ctx.Contracts.Remove(scratchContract);
                await ctx.SaveChangesAsync();
                await uow.CompleteAsync();
            }

            async Task PutChildAsync(HttpClient client, string route, Guid id,
                string json, HttpStatusCode expected)
            {
                using var payload = new StringContent(json, Encoding.UTF8,
                    "application/json");
                using var response = await client.PutAsync(ChildId(route, id), payload);
                Assert.Equal(expected, response.StatusCode);
            }
            await PutChildAsync(clientRoot, floorUpdate, floorA,
                """{"floorNumber":1,"name":"PG Floor A1 updated"}""",
                HttpStatusCode.OK);
            await PutChildAsync(clientB, floorUpdate, floorA,
                """{"floorNumber":1,"name":"Forbidden"}""",
                HttpStatusCode.Forbidden);
            await PutChildAsync(clientRoot, roomUpdate, roomA,
                """{"roomNumber":"101","name":"PG A101 updated","area":26,"capacity":2,"status":0}""",
                HttpStatusCode.OK);
            await PutChildAsync(clientB, roomUpdate, roomA,
                """{"roomNumber":"101","name":"Forbidden","area":26,"capacity":2,"status":0}""",
                HttpStatusCode.Forbidden);
            using (var response = await clientB.DeleteAsync(ChildId(roomDelete, roomA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.DeleteAsync(ChildId(floorDelete, floorA)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.DeleteAsync(ChildId(roomDelete, roomA)))
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
            using (var response = await clientRoot.DeleteAsync(ChildId(floorDelete, floorA)))
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

            // Two-database COMMIT-bound race: native Identity UPDATE must
            // wait until the ABP Building UoW has committed and released
            // its separate locked Identity transaction.
            Task<int>? disableResult = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D"))
                    }, "IsolatedPgRacePrincipal"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    var boundary = sp.GetRequiredService<IBuildingScopedWriteBoundary>();
                    await boundary.ExecuteBuildingAsync(b, async () =>
                    {
                        var pg = await sp.GetRequiredService<
                            IDbContextProvider<BuildingManagementDbContext>>()
                            .GetDbContextAsync();
                        await pg.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE \"BuildingManagementRooms\" SET \"Name\" = {"Committed before disable"} WHERE \"Id\" = {roomB}");
                        disableResult = Task.Run(async () =>
                        {
                            await using var identityConnection =
                                new NpgsqlConnection(idCs);
                            await identityConnection.OpenAsync();
                            await using var revoke = new NpgsqlCommand(
                                """UPDATE "AbpUsers" SET "IsActive" = FALSE WHERE "Id" = @id""",
                                identityConnection);
                            revoke.Parameters.AddWithValue("id", operatorB);
                            return await revoke.ExecuteNonQueryAsync();
                        });
                        await Task.Delay(250);
                        Assert.False(disableResult.IsCompleted,
                            "Identity disable must wait for Building UoW commit.");
                        return true;
                    }, BuildingScopedMutationKind.Room);
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(disableResult);
            Assert.Equal(1, await disableResult!.WaitAsync(TimeSpan.FromSeconds(15)));
            using (var response = await clientB.GetAsync(ChildId(roomGet, roomB)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.GetAsync(ChildId(roomGet, roomB)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("Committed before disable",
                    await response.Content.ReadAsStringAsync());
            }
            await using (var identityRestore = new NpgsqlConnection(idCs))
            {
                await identityRestore.OpenAsync();
                await using var restore = new NpgsqlCommand(
                    """UPDATE "AbpUsers" SET "IsActive" = TRUE WHERE "Id" = @id""",
                    identityRestore);
                restore.Parameters.AddWithValue("id", operatorB);
                Assert.Equal(1, await restore.ExecuteNonQueryAsync());
            }

            // Cross-database direct permission REVOKE must also wait for
            // a Room write to commit. Lease pins the specific 'U' grant row
            // against UPDATE/DELETE, even if ABP permission cache is warm.
            Task<int>? removedPermission = null;
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                        new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D"))
                    }, "IsolatedPgPermissionRace"))))
                {
                    using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                        .Begin(requiresNew: true, isTransactional: true);
                    await sp.GetRequiredService<IBuildingScopedWriteBoundary>()
                        .ExecuteBuildingAsync(b, async () =>
                        {
                            var pg = await sp.GetRequiredService<
                                IDbContextProvider<BuildingManagementDbContext>>()
                                .GetDbContextAsync();
                            await pg.Database.ExecuteSqlInterpolatedAsync(
                                $"UPDATE \"BuildingManagementRooms\" SET \"Name\" = {"Committed before permission revoke"} WHERE \"Id\" = {roomB}");
                            removedPermission = Task.Run(async () =>
                            {
                                await using var connection = new NpgsqlConnection(idCs);
                                await connection.OpenAsync();
                                await using var revoke = new NpgsqlCommand(
                                    """
                                    DELETE FROM "AbpPermissionGrants"
                                     WHERE "ProviderName" = 'U'
                                       AND "ProviderKey" = @actor
                                       AND "Name" = @permission
                                    """, connection);
                                revoke.Parameters.AddWithValue("actor",
                                    operatorB.ToString("D"));
                                revoke.Parameters.AddWithValue("permission",
                                    BuildingManagementPermissions.Rooms.Manage);
                                return await revoke.ExecuteNonQueryAsync();
                            });
                            await Task.Delay(250);
                            Assert.False(removedPermission.IsCompleted,
                                "Direct ABP permission revoke must await Building commit.");
                            return true;
                        }, BuildingScopedMutationKind.Room);
                    await uow.CompleteAsync();
                }
            }
            Assert.NotNull(removedPermission);
            Assert.Equal(1, await removedPermission!.WaitAsync(TimeSpan.FromSeconds(15)));
            using (var payload = new StringContent(
                """{"roomNumber":"101","name":"Must deny","area":26,"capacity":2,"status":0}""",
                Encoding.UTF8, "application/json"))
            using (var response = await clientB.PutAsync(
                ChildId(roomUpdate, roomB), payload))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Rooms.Manage, true);
                await uow.CompleteAsync();
            }

            // Role-only permission path: ABP Identity membership, role
            // and role permission are real persisted rows. A stale role
            // claim cannot bypass the commit-bound Identity lease after
            // either the role permission OR the user-role row is revoked.
            var delegatedRole = "slicea_role_" + Guid.NewGuid().ToString("N");
            var delegatedRoleId = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                var roleManager = sp.GetRequiredService<IdentityRoleManager>();
                var roleCreated = await roleManager.CreateAsync(
                    new IdentityRole(delegatedRoleId, delegatedRole));
                Assert.True(roleCreated.Succeeded,
                    string.Join("; ", roleCreated.Errors.Select(x => x.Description)));
                var users = sp.GetRequiredService<IdentityUserManager>();
                var operatorAccount = await users.GetByIdAsync(operatorB);
                var joined = await users.AddToRoleAsync(operatorAccount, delegatedRole);
                Assert.True(joined.Succeeded,
                    string.Join("; ", joined.Errors.Select(x => x.Description)));
                var permissions = sp.GetRequiredService<IPermissionManager>();
                await permissions.SetForRoleAsync(delegatedRole,
                    BuildingManagementPermissions.Rooms.Manage, true);
                await permissions.SetForUserAsync(operatorB,
                    BuildingManagementPermissions.Rooms.Manage, false);
                await uow.CompleteAsync();
            }

            async Task CheckRoleRevokeWaitsAsync(bool removeMembership)
            {
                Task<int>? revoke = null;
                using (var scope = app.Services.CreateScope())
                {
                    var sp = scope.ServiceProvider;
                    var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                    using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                            new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D")),
                            new Claim(AbpClaimTypes.Role, delegatedRole)
                        }, "PersistedRoleOnlyActor"))))
                    {
                        using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                            .Begin(requiresNew: true, isTransactional: true);
                        await sp.GetRequiredService<IBuildingScopedWriteBoundary>()
                            .ExecuteBuildingAsync(b, async () =>
                            {
                                var pg = await sp.GetRequiredService<
                                    IDbContextProvider<BuildingManagementDbContext>>()
                                    .GetDbContextAsync();
                                await pg.Database.ExecuteSqlInterpolatedAsync(
                                    $"UPDATE \"BuildingManagementRooms\" SET \"Name\" = {"Role-only authorized write"} WHERE \"Id\" = {roomB}");
                                revoke = Task.Run(async () =>
                                {
                                    await using var idConn = new NpgsqlConnection(idCs);
                                    await idConn.OpenAsync();
                                    await using var cmd = new NpgsqlCommand(
                                        removeMembership
                                            ? """DELETE FROM "AbpUserRoles" WHERE "UserId" = @actor AND "RoleId" = @role"""
                                            : """DELETE FROM "AbpPermissionGrants" WHERE "ProviderName" = 'R' AND "ProviderKey" = @roleName AND "Name" = @permission""",
                                        idConn);
                                    if (removeMembership)
                                    {
                                        cmd.Parameters.AddWithValue("actor", operatorB);
                                        cmd.Parameters.AddWithValue("role", delegatedRoleId);
                                    }
                                    else
                                    {
                                        cmd.Parameters.AddWithValue("roleName", delegatedRole);
                                        cmd.Parameters.AddWithValue("permission",
                                            BuildingManagementPermissions.Rooms.Manage);
                                    }
                                    return await cmd.ExecuteNonQueryAsync();
                                });
                                await Task.Delay(250);
                                Assert.False(revoke.IsCompleted,
                                    "ABP role revocation must await Building transaction commit.");
                                return true;
                            }, BuildingScopedMutationKind.Room);
                        await uow.CompleteAsync();
                    }
                }
                Assert.NotNull(revoke);
                Assert.Equal(1, await revoke!.WaitAsync(TimeSpan.FromSeconds(15)));
                using (var scope = app.Services.CreateScope())
                {
                    var sp = scope.ServiceProvider;
                    var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                    using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                            new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D")),
                            new Claim(AbpClaimTypes.Role, delegatedRole)
                        }, "StaleRoleClaim"))))
                    {
                        using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                            .Begin(requiresNew: true, isTransactional: true);
                        var mutated = false;
                        await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(
                            () => sp.GetRequiredService<IBuildingScopedWriteBoundary>()
                                .ExecuteBuildingAsync(b, () =>
                                {
                                    mutated = true;
                                    return Task.FromResult(true);
                                }, BuildingScopedMutationKind.Room));
                        Assert.False(mutated);
                    }
                }
            }

            await CheckRoleRevokeWaitsAsync(removeMembership: false);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForRoleAsync(
                    delegatedRole, BuildingManagementPermissions.Rooms.Manage, true);
                await uow.CompleteAsync();
            }
            await CheckRoleRevokeWaitsAsync(removeMembership: true);
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                await sp.GetRequiredService<IPermissionManager>().SetForUserAsync(
                    operatorB, BuildingManagementPermissions.Rooms.Manage, true);
                await uow.CompleteAsync();
            }

            // Tenant-smuggling resistance for direct ABP grants: a
            // user-scoped permission with a mismatched TenantId does not
            // authorize any Building write, even if a previous permission
            // check/claim is stale. No real tenant data is touched.
            await using (var idConnection = new NpgsqlConnection(idCs))
            {
                await idConnection.OpenAsync();
                var unrelatedTenant = Guid.NewGuid();
                await using (var mismatch = new NpgsqlCommand(
                    """
                    UPDATE "AbpPermissionGrants"
                       SET "TenantId" = @tenant
                     WHERE "ProviderName" = 'U' AND "ProviderKey" = @actor
                       AND "Name" = @permission AND "TenantId" IS NULL
                    """, idConnection))
                {
                    mismatch.Parameters.AddWithValue("tenant", unrelatedTenant);
                    mismatch.Parameters.AddWithValue("actor", operatorB.ToString("D"));
                    mismatch.Parameters.AddWithValue("permission",
                        BuildingManagementPermissions.Rooms.Manage);
                    Assert.Equal(1, await mismatch.ExecuteNonQueryAsync());
                }

                using (var scope = app.Services.CreateScope())
                {
                    var sp = scope.ServiceProvider;
                    var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();
                    using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(AbpClaimTypes.UserId, operatorB.ToString("D")),
                            new Claim(ClaimTypes.NameIdentifier, operatorB.ToString("D"))
                        }, "MismatchedTenantGrant"))))
                    {
                        using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
                            .Begin(requiresNew: true, isTransactional: true);
                        var mutated = false;
                        await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(
                            () => sp.GetRequiredService<IBuildingScopedWriteBoundary>()
                                .ExecuteBuildingAsync(b, () =>
                                {
                                    mutated = true;
                                    return Task.FromResult(true);
                                }, BuildingScopedMutationKind.Room));
                        Assert.False(mutated);
                    }
                }

                await using var restore = new NpgsqlCommand(
                    """
                    UPDATE "AbpPermissionGrants"
                       SET "TenantId" = NULL
                     WHERE "ProviderName" = 'U' AND "ProviderKey" = @actor
                       AND "Name" = @permission AND "TenantId" = @tenant
                    """, idConnection);
                restore.Parameters.AddWithValue("actor", operatorB.ToString("D"));
                restore.Parameters.AddWithValue("permission",
                    BuildingManagementPermissions.Rooms.Manage);
                restore.Parameters.AddWithValue("tenant", unrelatedTenant);
                Assert.Equal(1, await restore.ExecuteNonQueryAsync());
            }

            // The PostgreSQL-backed HTTP path must also honor fresh ABP
            // Identity account state and durable user permission changes.
            // A still-valid membership is NEVER sufficient by itself.
            async Task ChangeAccountAsync(bool active, bool locked)
            {
                await using var db = new NgocTrangHouseManagementSystemDbContext(
                    new DbContextOptionsBuilder<NgocTrangHouseManagementSystemDbContext>()
                        .UseNpgsql(idCs).Options);
                var until = locked ? DateTimeOffset.UtcNow.AddMinutes(15)
                    : (DateTimeOffset?)null;
                // This manually instantiated ABP DbContext has no injected
                // DbSet/ABP audit services. Update ONLY the test-generated
                // Identity database through a parameterized SQL statement.
                // Subsequent HTTP requests load fresh records via the real
                // ABP Identity repository and IdentityUserManager.
                var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AbpUsers"
                       SET "IsActive" = {active},
                           "LockoutEnabled" = {locked},
                           "LockoutEnd" = {until}
                     WHERE "Id" = {operatorB}
                    """);
                Assert.Equal(1, updated);
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
            using (var response = await clientB.GetAsync(
                grants + "/" + b.ToString("D")))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.GetAsync(
                grants + "/" + b.ToString("D")))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var items = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                Assert.Contains(items.RootElement.EnumerateArray(), x =>
                    x.GetProperty("operatorUserId").GetGuid() == operatorB &&
                    x.GetProperty("revisionNumber").GetInt32() == 2 &&
                    x.GetProperty("revokedAtUtc").ValueKind != JsonValueKind.Null);
            }
            using (var response = await clientB.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(ChildId(floorGet, floorB)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientB.GetAsync(ChildId(roomGet, roomB)))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await clientRoot.GetAsync(GetAt(b)))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Recovery founder lookup must not arbitrarily pick a
            // self-issued grant. A scratch-only privileged insertion of a
            // second "founder" invalidates the unique founder proof.
            using (var scope = app.Services.CreateScope())
            {
                using var uow = scope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                Assert.Equal(root, await scope.ServiceProvider
                    .GetRequiredService<IBuildingOwnerRecoveryFounderReader>()
                    .ReadFoundingOwnerAsync(a));
                await uow.CompleteAsync();
            }
            await using (var conn = new NpgsqlConnection(buildingCs))
            {
                await conn.OpenAsync();
                await using var forge = new NpgsqlCommand(
                    """
                    INSERT INTO "BuildingManagementOperatorBuildingGrants"
                        ("Id","OperatorUserId","BuildingId","GrantedByUserId",
                         "GrantedAtUtc","RevisionNumber","ExtraProperties",
                         "ConcurrencyStamp")
                    VALUES (@id,@other,@building,@other,@at,1,'{}',@stamp)
                    """, conn);
                forge.Parameters.AddWithValue("id", Guid.NewGuid());
                // Cosigner now has a legitimate delegated grant on A from
                // the commit-lease test; use a separate historical identity.
                forge.Parameters.AddWithValue("other", disabledFounder);
                forge.Parameters.AddWithValue("building", a);
                forge.Parameters.AddWithValue("at", DateTime.UtcNow.AddMinutes(-1));
                forge.Parameters.AddWithValue("stamp", Guid.NewGuid().ToString("N"));
                Assert.Equal(1, await forge.ExecuteNonQueryAsync());
            }
            using (var scope = app.Services.CreateScope())
            {
                using var uow = scope.ServiceProvider
                    .GetRequiredService<IUnitOfWorkManager>()
                    .Begin(requiresNew: true, isTransactional: true);
                Assert.Null(await scope.ServiceProvider
                    .GetRequiredService<IBuildingOwnerRecoveryFounderReader>()
                    .ReadFoundingOwnerAsync(a));
                await uow.CompleteAsync();
            }
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

    private static async Task AttestIdentityRuntimeLoginAsync(
        NpgsqlConnection admin, string isolatedIdentityConnection)
    {
        var original = new NpgsqlConnectionStringBuilder(isolatedIdentityConnection)
        {
            Pooling = false, CommandTimeout = 20
        };
        if (original.Host is not ("127.0.0.1" or "localhost") ||
            !Regex.IsMatch(original.Database ?? string.Empty,
                @"^bm_slicea_staging_identity_[0-9a-f]{32}$"))
            throw new InvalidOperationException(
                "Refusing Identity runtime audit outside generated scratch database.");

        var login = "bm_identity_runtime_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        Assert.Matches(@"^bm_identity_runtime_[0-9a-f]{32}$", login);
        var created = false;
        await using var idAdmin = new NpgsqlConnection(original.ConnectionString);
        try
        {
            await ExecuteAsync(admin,
                $"""CREATE ROLE "{login}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD '{password}'""");
            created = true;
            await idAdmin.OpenAsync();
            Assert.StartsWith("bm_slicea_staging_identity_", idAdmin.Database);
            await ExecuteAsync(idAdmin,
                $"""GRANT USAGE ON SCHEMA public TO "{login}" """);
            await ExecuteAsync(idAdmin,
                $"""
                GRANT SELECT ON TABLE "AbpUsers", "AbpRoles",
                    "AbpUserRoles", "AbpPermissionGrants" TO "{login}"
                """);
            var runtimeCs = new NpgsqlConnectionStringBuilder(
                original.ConnectionString)
            {
                Username = login, Password = password,
                Pooling = false
            };
            using var stream = typeof(SliceAStagingPostgreSqlHttp_Tests)
                .Assembly.GetManifestResourceStream(
                    "SliceA_IdentityRuntimeDbRole_ReadOnlyAudit.sql")
                ?? throw new InvalidOperationException(
                    "Missing embedded review-only Identity runtime audit SQL.");
            using var reader = new System.IO.StreamReader(stream);
            var audit = reader.ReadToEnd().TrimStart('\uFEFF');
            Assert.Equal(1, Regex.Matches(audit,
                @"(?im)^SELECT\s+runtime_login").Count);
            Assert.False(Regex.IsMatch(audit,
                @"(?im)^\s*(CREATE|ALTER|DROP|GRANT|REVOKE|UPDATE|DELETE|INSERT|TRUNCATE|SET|DO|CALL)\b"));

            await using (var runtime = new NpgsqlConnection(runtimeCs.ConnectionString))
            {
                await runtime.OpenAsync();
                async Task<(bool IdentityPresent, bool Role, bool All)> CheckAsync()
                {
                    await using var cmd = new NpgsqlCommand(audit, runtime);
                    await using var result = await cmd.ExecuteReaderAsync();
                    Assert.True(await result.ReadAsync());
                    Assert.Equal(login, result.GetString(0));
                    Assert.Equal(login, result.GetString(1));
                    Assert.Equal(original.Database, result.GetString(2));
                    Assert.Equal("public", result.GetString(3));
                    var exists = result.GetBoolean(4);
                    var rolePass = result.GetBoolean(5);
                    var all = result.GetBoolean(10);
                    return (exists, rolePass, all);
                }
                var minimal = await CheckAsync();
                Assert.True(minimal.IdentityPresent);
                Assert.True(minimal.Role);
                Assert.True(minimal.All);

                // Test negative findings without ever executing unsafe
                // statements as the limited login.
                await ExecuteAsync(idAdmin,
                    $"""GRANT TRUNCATE ON TABLE "AbpUsers" TO "{login}" """);
                var dangerousTable = await CheckAsync();
                Assert.True(dangerousTable.Role);
                Assert.False(dangerousTable.All);
                await ExecuteAsync(idAdmin,
                    $"""REVOKE TRUNCATE ON TABLE "AbpUsers" FROM "{login}" """);

                await ExecuteAsync(idAdmin,
                    $"""GRANT CREATE ON SCHEMA public TO "{login}" """);
                var dangerousSchema = await CheckAsync();
                Assert.False(dangerousSchema.Role);
                Assert.False(dangerousSchema.All);
                await ExecuteAsync(idAdmin,
                    $"""REVOKE CREATE ON SCHEMA public FROM "{login}" """);
                Assert.True((await CheckAsync()).All);
            }
        }
        finally
        {
            if (created)
            {
                if (idAdmin.State != System.Data.ConnectionState.Open)
                    await idAdmin.OpenAsync();
                await ExecuteAsync(idAdmin,
                    $"""REVOKE ALL ON TABLE "AbpUsers", "AbpRoles", "AbpUserRoles", "AbpPermissionGrants" FROM "{login}" """);
                await ExecuteAsync(idAdmin,
                    $"""REVOKE ALL ON SCHEMA public FROM "{login}" """);
                await ExecuteAsync(admin, $"""DROP ROLE "{login}" """);
            }
        }
    }

    /// <summary>
    /// Test-only server exception capture. Stores only exception type,
    /// PostgreSQL SQLSTATE and first short diagnostic message. This never
    /// changes API error responses or weakens Owner authorization.
    /// </summary>
    private sealed class StagingServerErrorCollector : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public ILogger CreateLogger(string categoryName)
            => new Observer(this, categoryName);
        public void Dispose() { }

        public void Clear()
        {
            while (_errors.TryDequeue(out _)) { }
        }

        public string Describe()
            => _errors.IsEmpty
                ? "No server-side error captured by ILogger."
                : string.Join(" | ", _errors.TakeLast(8));

        private sealed class Observer : ILogger
        {
            private readonly StagingServerErrorCollector _collector;
            private readonly string _category;
            public Observer(StagingServerErrorCollector collector, string category)
            {
                _collector = collector;
                _category = category;
            }
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NoopScope.Instance;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;

            public void Log<TState>(
                LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (level < LogLevel.Error) return;
                var postgres = exception;
                while (postgres is not null && postgres is not PostgresException)
                    postgres = postgres.InnerException;
                var pg = postgres as PostgresException;
                var message = exception?.Message ?? "No exception supplied";
                if (message.Length > 240) message = message[..240];
                _collector._errors.Enqueue(
                    $"{_category} {exception?.GetType().Name ?? "Error"}" +
                    (pg is null ? "" : $" PG={pg.SqlState} Constraint={pg.ConstraintName}") +
                    $" {message}");
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
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
            context.Services.Configure<BuildingOwnerRecoverySecurityOptions>(
                o => o.CoSignerUserIds = new System.Collections.Generic.HashSet<Guid>(
                    new[] { Guid.Parse(config["BuildingManagement:OwnerRecovery:CoSignerUserIds:0"]!) }));
            context.Services.AddTransient<SliceAStagingOwnerRecoveryEvidenceStore>();
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOwnerRecoveryApprovalReader>(sp =>
                    sp.GetRequiredService<SliceAStagingOwnerRecoveryEvidenceStore>()));
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOwnerRecoveryFounderReader>(sp =>
                    sp.GetRequiredService<SliceAStagingOwnerRecoveryEvidenceStore>()));
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOwnerRecoveryWriter, SliceAStagingOwnerRecoveryWriter>());

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
            context.Services.AddTransient<SliceAStagingIdentityCommitLease>();
            context.Services.AddTransient<SliceAStagingOperatorGrantQuery>();
            context.Services.AddTransient<SliceBStagingContractQuery>();
            context.Services.AddTransient<SliceBStagingReservationQuery>();
            context.Services.AddTransient<SliceBStagingContractMutation>();
            // Explicitly exercise the same Slice B financial adapter that
            // the real Host wires ONLY on two isolated staging databases.
            context.Services.AddTransient<SliceBStagingFinalInvoiceMutation>();
            context.Services.AddTransient<SliceBStagingRoomChangeRequests>();
            context.Services.AddTransient<SliceBStagingRoomScopedServiceTariffs>();
            context.Services.AddTransient<SliceCStagingUtilityRates>();
            context.Services.AddTransient<SliceCStagingMeterReadings>();
            context.Services.AddTransient<SliceCStagingUtilityMeters>();
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

    private sealed class MutableUtcTimeProvider : TimeProvider
    {
        // Zero means the real clock, so all existing tests keep native time.
        private long _ticks;

        public override DateTimeOffset GetUtcNow()
        {
            var ticks = Interlocked.Read(ref _ticks);
            return ticks == 0 ? DateTimeOffset.UtcNow :
                new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        public void SetUtc(DateTimeOffset value) =>
            Interlocked.Exchange(ref _ticks, value.UtcTicks);

        public void Reset() => Interlocked.Exchange(ref _ticks, 0);
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
