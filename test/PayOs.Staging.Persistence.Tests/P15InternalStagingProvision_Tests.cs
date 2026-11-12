using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Floors;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Permissions;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DependencyInjection;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Testing;
using Volo.Abp.Timing;
using Volo.Abp.Uow;
using Xunit;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using IdentityRole = Volo.Abp.Identity.IdentityRole;
using IdentityUser = Volo.Abp.Identity.IdentityUser;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// P1.5 FIXTURE PROVISIONING ONLY. Prepares the three purpose-named
/// disposable PostgreSQL databases on 127.0.0.1:15432 so the separate
/// internal-staging Host acceptance script (verify-payos-p15-internal-staging.ps1)
/// can exercise the REAL Host over the internal simulated payOS provider:
/// real OpenIddict password-grant tokens, real checkout/webhook/reconcile
/// HTTP, all loopback, zero real payOS traffic.
///
/// This test does NOT prove the HTTP matrix: it seeds deterministic fixture
/// rows (GUIDs mirrored verbatim in the PowerShell script) and real ABP
/// Identity accounts whose password hashes are produced by the same
/// PasswordHasher implementation the Host's sign-in manager verifies.
/// Unlike the D04-B fixture, NO payOS intent/inbox rows are seeded here:
/// the P1.5 lane creates intents through the real checkout endpoint, and
/// the simulated provider + dispatcher run inside the Host itself.
///
/// Never run without the disposable-container opt-ins: the shared test
/// module refuses any non-15432 database, and this test additionally
/// requires an explicit internal-staging consent phrase plus locally
/// supplied test passwords. No password, hash or token is ever written
/// to disk.
/// </summary>
public sealed class P15InternalStagingProvision_Tests
    : AbpIntegratedTest<P15InternalStagingProvisionTestModule>
{
    protected override void SetAbpApplicationCreationOptions(
        AbpApplicationCreationOptions options)
    {
        // Match the D04-B fixture: ABP's property-injected dependencies are
        // supplied by Autofac, not by 'new DbContext'.
        options.UseAutofac();
    }

    public const string InternalStagingOptIn =
        "I_ACCEPT_DISPOSABLE_P15_INTERNAL_STAGING_ONLY";
    public const string OperatorPasswordEnv =
        "BM_PAYOS_P15_INTERNAL_OPERATOR_PASSWORD";
    public const string OutsiderPasswordEnv =
        "BM_PAYOS_P15_INTERNAL_OUTSIDER_PASSWORD";

    // Fixed IDs shared verbatim with verify-payos-p15-internal-staging.ps1.
    // The script greps this file for these literals before it runs.
    // Building A hosts the positive checkout lane; Building B grants nobody
    // so the native HTTP matrix can prove a cross-building checkout refusal.
    public static readonly Guid OperatorId =
        Guid.Parse("0150c000-0000-4000-8000-000000000001");
    public static readonly Guid OutsiderId =
        Guid.Parse("0150c000-0000-4000-8000-000000000002");

    public static readonly Guid BuildingA =
        Guid.Parse("0150a000-0000-4000-8000-000000000001");
    public static readonly Guid FloorA =
        Guid.Parse("0150a000-0000-4000-8000-000000000002");
    public static readonly Guid RoomA =
        Guid.Parse("0150a000-0000-4000-8000-000000000003");
    public static readonly Guid TenantA =
        Guid.Parse("0150a000-0000-4000-8000-000000000004");
    public static readonly Guid ContractA =
        Guid.Parse("0150a000-0000-4000-8000-000000000005");
    public static readonly Guid PaymentA =
        Guid.Parse("0150a000-0000-4000-8000-000000000006");

    public static readonly Guid BuildingB =
        Guid.Parse("0150b000-0000-4000-8000-000000000001");
    public static readonly Guid FloorB =
        Guid.Parse("0150b000-0000-4000-8000-000000000002");
    public static readonly Guid RoomB =
        Guid.Parse("0150b000-0000-4000-8000-000000000003");
    public static readonly Guid TenantB =
        Guid.Parse("0150b000-0000-4000-8000-000000000004");
    public static readonly Guid ContractB =
        Guid.Parse("0150b000-0000-4000-8000-000000000005");
    public static readonly Guid PaymentB =
        Guid.Parse("0150b000-0000-4000-8000-000000000006");

    public const long AmountA = 175000;
    public const long AmountB = 185000;

    private const string IdentityConnection =
        "Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_identity;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    private const string BuildingConnection =
        "Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_building;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    private const string PayOsConnection =
        "Host=127.0.0.1;Port=15432;Database=bm_payos_staging_p15;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    [Fact]
    public async Task P15INTERNAL_PREP01_Provisions_fixture_and_real_identity_accounts()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_P15_INTERNAL") !=
            InternalStagingOptIn)
            throw new InvalidOperationException(
                "P1.5 internal staging fixture refuses to run without the " +
                "explicit disposable internal-staging consent phrase.");
        var operatorPassword = RequirePassword(OperatorPasswordEnv);
        var outsiderPassword = RequirePassword(OutsiderPasswordEnv);

        // 1. The Identity database must ALREADY be migrated and seeded by
        // the real DbMigrator before this fixture runs.
        await AssertIdentitySeededByDbMigratorAsync();

        // 2. Canonical legacy BuildingManagement schema in the disposable
        // slice-A staging building database, migrated through the real EF
        // migration list (NOT the PayOs payment migration context).
        var buildingOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
            .UseNpgsql(BuildingConnection).Options;
        await using var building = new BuildingManagementDbContext(buildingOptions);
        await building.Database.MigrateAsync();
        Assert.Empty(await building.Database.GetPendingMigrationsAsync());

        // 3. Deterministic financial fixture. Building A grants the operator
        // (Contracts.Manage + active grant) and hosts the pending deposit
        // payment the checkout lane targets. Building B deliberately grants
        // nobody, so the HTTP matrix proves a cross-building checkout is
        // refused. Writes go through ABP's registered EF provider because a
        // manually new'ed BuildingManagementDbContext would NRE in
        // AbpDbContext.SaveChangesAsync.
        var now = DateTime.UtcNow;
        using (var seedScope = ServiceProvider.CreateScope())
        {
            var seedUowManager = seedScope.ServiceProvider
                .GetRequiredService<IUnitOfWorkManager>();
            using var seedUow = seedUowManager.Begin(
                new AbpUnitOfWorkOptions { IsTransactional = false });
            var db = await seedScope.ServiceProvider
                .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
                .GetDbContextAsync();

            SeedBuilding(db, BuildingA, FloorA, RoomA, TenantA, ContractA,
                PaymentA, "P15 internal building A", "901", AmountA, "A");
            SeedBuilding(db, BuildingB, FloorB, RoomB, TenantB, ContractB,
                PaymentB, "P15 internal building B", "902", AmountB, "B");
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BuildingManagementOperatorBuildingGrants"
                  ("Id","OperatorUserId","BuildingId","GrantedByUserId",
                   "GrantedAtUtc","RevisionNumber","ExtraProperties","ConcurrencyStamp")
                VALUES ({Guid.NewGuid()},{OperatorId},{BuildingA},{OperatorId},
                        {now.AddMinutes(-10)},{1},{"{}"},{Guid.NewGuid().ToString("N")})
                """);
            await seedUow.CompleteAsync();
        }

        // 4. payOS staging schema: migrate only, seed NOTHING. Intents are
        // created by the real checkout endpoint over the real Host in this
        // lane; the fixture must not pre-state what the matrix observes.
        var payOsOptions = new DbContextOptionsBuilder<PayOsPaymentMigrationDbContext>()
            .UseNpgsql(PayOsConnection,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_PayOs"))
            .Options;
        await using var payOs = new PayOsPaymentMigrationDbContext(payOsOptions);
        await payOs.Database.MigrateAsync();
        Assert.Empty(await payOs.Database.GetPendingMigrationsAsync());

        // 5. TWO real ABP Identity accounts in the DbMigrator-migrated
        // Identity schema: the operator holds the Contracts.Manage grant,
        // the outsider holds nothing. Password hashes are produced by the
        // exact PasswordHasher implementation the Host verifies against.
        using (var scope = ServiceProvider.CreateScope())
        {
            var uowManager = scope.ServiceProvider
                .GetRequiredService<IUnitOfWorkManager>();
            using var uow = uowManager.Begin(
                new AbpUnitOfWorkOptions { IsTransactional = false });
            var identityDb = await scope.ServiceProvider
                .GetRequiredService<IDbContextProvider<NgocTrangHouseManagementSystemDbContext>>()
                .GetDbContextAsync();
            Assert.False(await identityDb.Users.AnyAsync(u =>
                u.UserName == "operator-p15" || u.UserName == "outsider-p15"),
                "P15_INTERNAL_FIXTURE_STALE: fixture accounts already exist; " +
                "recreate the disposable container instead of reusing it.");

            var hasher = new PasswordHasher<IdentityUser>();
            var operatorUser = new IdentityUser(OperatorId, "operator-p15",
                "operator-p15@example.invalid");
            var outsider = new IdentityUser(OutsiderId, "outsider-p15",
                "outsider-p15@example.invalid");

            identityDb.Users.AddRange(operatorUser, outsider);
            identityDb.Set<PermissionGrant>().AddRange(
                new PermissionGrant(Guid.NewGuid(),
                    BuildingManagementPermissions.Contracts.Manage,
                    "U", OperatorId.ToString("D")));
            await identityDb.SaveChangesAsync();
            identityDb.ChangeTracker.Clear();

            // Normalized columns are normally populated by
            // IdentityUserManager; this fixture bypasses the manager, so the
            // native password grant must find these accounts via explicit
            // normalized values.
            await identityDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AbpUsers" SET "PasswordHash"={hasher.HashPassword(operatorUser, operatorPassword)},
                  "NormalizedUserName"={operatorUser.UserName!.ToUpperInvariant()},
                  "NormalizedEmail"={operatorUser.Email!.ToUpperInvariant()}
                 WHERE "Id"={OperatorId}
                """);
            await identityDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AbpUsers" SET "PasswordHash"={hasher.HashPassword(outsider, outsiderPassword)},
                  "NormalizedUserName"={outsider.UserName!.ToUpperInvariant()},
                  "NormalizedEmail"={outsider.Email!.ToUpperInvariant()}
                 WHERE "Id"={OutsiderId}
                """);
            identityDb.ChangeTracker.Clear();
            await uow.CompleteAsync();
        }

        // 6. Independent durable proof from all three databases before the
        // Host is allowed to start. No assertion depends on EF state.
        await AssertFixturePersistedAsync();

        // Fixture passwords must not linger in the test process environment.
        Environment.SetEnvironmentVariable(OperatorPasswordEnv, null);
        Environment.SetEnvironmentVariable(OutsiderPasswordEnv, null);
    }

    private static string RequirePassword(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length < 16)
            throw new InvalidOperationException(
                "P1.5 internal staging fixture requires a locally generated " +
                "disposable test password (>=16 chars) in " + name + ".");
        return value;
    }

    private static async Task AssertIdentitySeededByDbMigratorAsync()
    {
        await using var connection = new NpgsqlConnection(IdentityConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM "AbpUsers"),
              (SELECT count(*) FROM "OpenIddictApplications"
                WHERE "ClientId"='NgocTrangHouseManagementSystem_App')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var users = reader.GetInt64(0);
        var clients = reader.GetInt64(1);
        if (users < 1 || clients != 1)
            throw new InvalidOperationException(
                "P15_REQUIRES_DBMIGRATOR: run the real DbMigrator " +
                "against bm_slicea_staging_p15_identity before this fixture " +
                $"(users={users}, passwordGrantClient={clients}).");
    }

    private static void SeedBuilding(BuildingManagementDbContext db,
        Guid buildingId, Guid floorId, Guid roomId, Guid tenantId,
        Guid contractId, Guid paymentId, string buildingName,
        string roomNumber, long amount, string suffix)
    {
        db.Buildings.Add(new Building(buildingId, buildingName,
            "Disposable P15 internal address " + suffix));
        db.Floors.Add(new Floor(floorId, buildingId, 1, "Floor 1"));
        db.Rooms.Add(new Room(roomId, floorId, roomNumber,
            "P15 internal room " + suffix, 25m, 2, RoomStatus.Occupied));
        db.Tenants.Add(new Tenant(tenantId,
            "Disposable P15 internal tenant " + suffix,
            "0900000" + suffix.PadLeft(3, '0').Substring(suffix.Length - 1, 3),
            null, null, null, IdentityType.CitizenId,
            "TEST" + Guid.NewGuid().ToString("N")[..12]));
        // Checkout accepts Signed or Active contracts; reconciliation also
        // accepts Signed, so sign without activating to keep both open.
        var contract = new Contract(contractId,
            "P15-" + suffix + "-" + Guid.NewGuid().ToString("N"),
            roomId, DateTime.UtcNow.Date, null, amount);
        contract.Sign();
        db.Contracts.Add(contract);
        db.ContractTenants.Add(new ContractTenant(Guid.NewGuid(), contractId,
            tenantId, ContractTenantRole.PrimaryTenant));
        db.Payments.Add(new Payment(paymentId,
            "P15PAY-" + suffix + "-" + Guid.NewGuid().ToString("N"),
            tenantId, PaymentPurpose.ContractDeposit,
            PaymentMethod.BankTransfer, PaymentChannel.VietQr,
            amount, contractId: contractId));
    }

    private static async Task AssertFixturePersistedAsync()
    {
        // Identity: two active accounts with real password hashes, exactly
        // one Contracts.Manage grant for the operator, and the seeded
        // password-capable public client.
        await using (var identity = new NpgsqlConnection(IdentityConnection))
        {
            await identity.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM "AbpUsers"
                    WHERE "Id" IN (@operator,@outsider)
                      AND "IsActive"=TRUE AND "IsDeleted"=FALSE
                      AND "PasswordHash" IS NOT NULL
                      AND "NormalizedUserName" IS NOT NULL),
                  (SELECT count(*) FROM "AbpPermissionGrants"
                    WHERE "ProviderName"='U'
                      AND "ProviderKey"=@operatorKey
                      AND "Name"=@managePermission),
                  (SELECT count(*) FROM "OpenIddictApplications"
                    WHERE "ClientId"='NgocTrangHouseManagementSystem_App')
                """, identity);
            check.Parameters.AddWithValue("operator", OperatorId);
            check.Parameters.AddWithValue("outsider", OutsiderId);
            check.Parameters.AddWithValue("operatorKey", OperatorId.ToString("D"));
            check.Parameters.AddWithValue("managePermission",
                BuildingManagementPermissions.Contracts.Manage);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

        // Building: two buildings, two pending deposit payments, one active
        // operator grant (Building A only), and the outbox table exists in
        // the payOS lane (migrated schema, zero rows seeded).
        await using (var building = new NpgsqlConnection(BuildingConnection))
        {
            await building.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM "Buildings"
                    WHERE "Id" IN (@buildingA,@buildingB) AND "IsDeleted"=FALSE),
                  (SELECT count(*) FROM "BuildingManagementPayments"
                    WHERE "Id" IN (@paymentA,@paymentB) AND "Status"=1),
                  (SELECT count(*) FROM "BuildingManagementOperatorBuildingGrants"
                    WHERE "BuildingId"=@buildingA
                      AND "OperatorUserId"=@operator
                      AND "RevokedAtUtc" IS NULL)
                """, building);
            check.Parameters.AddWithValue("buildingA", BuildingA);
            check.Parameters.AddWithValue("buildingB", BuildingB);
            check.Parameters.AddWithValue("paymentA", PaymentA);
            check.Parameters.AddWithValue("paymentB", PaymentB);
            check.Parameters.AddWithValue("operator", OperatorId);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

        // payOS staging: schema migrated, and NO intent/inbox/outbox rows —
        // the whole durable state of this lane is produced by the real Host
        // HTTP matrix itself.
        await using (var payOs = new NpgsqlConnection(PayOsConnection))
        {
            await payOs.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM pg_tables WHERE schemaname='public'
                     AND tablename IN (
                       'BuildingManagementPayOsPaymentIntents',
                       'BuildingManagementPayOsWebhookInbox',
                       'BuildingManagementPayOsStagingOutbox')),
                  (SELECT count(*) FROM "BuildingManagementPayOsPaymentIntents"),
                  (SELECT count(*) FROM "BuildingManagementPayOsStagingOutbox")
                """, payOs);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
        }
    }
}

[DependsOn(typeof(BuildingManagementEntityFrameworkCoreModule))]
public sealed class P15InternalStagingProvisionTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Fail closed before registering any DB services: the P1.5 internal
        // staging fixture only runs from the disposable PG16 runner with all
        // explicit safety opt-ins.
        if (Environment.GetEnvironmentVariable("BM_PAYOS_P15_INTERNAL") !=
                P15InternalStagingProvision_Tests.InternalStagingOptIn ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
                != "disposable-only" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432")
            throw new InvalidOperationException(
                "P1.5 internal staging fixture refuses non-disposable database.");

        // Same narrow, OpenIddict-free composition as the D04 acceptance
        // module: the native EF Identity model plus the real
        // BuildingManagement EF model, Autofac property injection, UTC
        // clock, and no implicit ABP transactions.
        context.Services.AddAbpDbContext<NgocTrangHouseManagementSystemDbContext>(
            options => options.AddDefaultRepositories(includeAllEntities: true));
        context.Services.AddTransient<NgocTrangHouseManagementSystemDbContext>(sp =>
        {
            var identityDb = ActivatorUtilities.CreateInstance<
                    NgocTrangHouseManagementSystemDbContext>(sp);
            try
            {
                identityDb.LazyServiceProvider =
                    sp.GetRequiredService<IAbpLazyServiceProvider>();
                _ = identityDb.UnitOfWorkManager;
                return identityDb;
            }
            catch
            {
                identityDb.Dispose();
                throw;
            }
        });

        var identityConnection =
            "Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_identity;" +
            "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
        var buildingConnection =
            "Host=127.0.0.1;Port=15432;Database=bm_slicea_staging_p15_building;" +
            "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
        Configure<AbpClockOptions>(options => options.Kind = DateTimeKind.Utc);
        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = identityConnection;
            options.ConnectionStrings[BuildingManagementDbProperties.ConnectionStringName] =
                buildingConnection;
        });
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        Configure<AbpUnitOfWorkDefaultOptions>(options =>
        {
            options.TransactionBehavior = UnitOfWorkTransactionBehavior.Disabled;
        });
    }
}
