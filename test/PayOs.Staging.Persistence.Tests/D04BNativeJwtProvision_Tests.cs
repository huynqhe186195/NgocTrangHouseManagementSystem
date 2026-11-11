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
using BuildingManagement.Migrations;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Permissions;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using Npgsql;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Testing;
using Volo.Abp.Uow;
using Volo.Abp;
using Xunit;
using IdentityRole = Volo.Abp.Identity.IdentityRole;
using IdentityUser = Volo.Abp.Identity.IdentityUser;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// P1.1 FIXTURE PROVISIONING ONLY. Prepares the three purpose-named
/// disposable PostgreSQL databases on 127.0.0.1:15432 so that the separate
/// native Host acceptance script (verify-payos-d04b-native-jwt-matrix.ps1)
/// can exercise REAL OpenIddict password-grant tokens over real HTTPS.
///
/// This test does NOT prove authentication: it seeds deterministic fixture
/// rows (GUIDs mirrored verbatim in the PowerShell script) and real ABP
/// Identity accounts whose password hashes are produced by the same
/// PasswordHasher implementation the Host's sign-in manager verifies.
/// The login, bearer validation and dual-actor HTTP authorization matrix
/// run only against the native Host started by that script.
///
/// Never run without the disposable-container opt-ins: the shared test
/// module refuses any non-15432 database, and this test additionally
/// requires an explicit native-JWT consent phrase plus locally supplied
/// test passwords. No password, hash or token is ever written to disk.
/// </summary>
public sealed class D04BNativeJwtProvision_Tests
    : AbpIntegratedTest<D04RealLegacyWriterAcceptanceTestModule>
{
    protected override void SetAbpApplicationCreationOptions(
        AbpApplicationCreationOptions options)
    {
        // Match the real-writer acceptance test: ABP's property-injected
        // dependencies (LazyServiceProvider/UnitOfWorkManager) are supplied
        // by Autofac, not by 'new DbContext'.
        options.UseAutofac();
    }

    public const string NativeJwtOptIn = "I_ACCEPT_DISPOSABLE_D04B_NATIVE_JWT_ONLY";
    public const string ReviewerPasswordEnv = "BM_PAYOS_D04_NATIVE_JWT_REVIEWER_PASSWORD";
    public const string OwnerPasswordEnv = "BM_PAYOS_D04_NATIVE_JWT_OWNER_PASSWORD";
    public const string OutsiderPasswordEnv = "BM_PAYOS_D04_NATIVE_JWT_OUTSIDER_PASSWORD";

    // Fixed IDs shared verbatim with verify-payos-d04b-native-jwt-matrix.ps1.
    // The script greps this file for these literals before it runs.
    public static readonly Guid ReviewerId =
        Guid.Parse("0d04c000-0000-4000-8000-000000000001");
    public static readonly Guid OwnerId =
        Guid.Parse("0d04c000-0000-4000-8000-000000000002");
    public static readonly Guid OutsiderId =
        Guid.Parse("0d04c000-0000-4000-8000-000000000003");

    public static readonly Guid BuildingA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000001");
    public static readonly Guid FloorA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000002");
    public static readonly Guid RoomA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000003");
    public static readonly Guid TenantA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000004");
    public static readonly Guid ContractA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000005");
    public static readonly Guid PaymentA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000006");
    public static readonly Guid IntentA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000007");
    public static readonly Guid InboxA =
        Guid.Parse("0d04a000-0000-4000-8000-000000000008");
    public static readonly Guid RoomA2 =
        Guid.Parse("0d04a000-0000-4000-8000-000000000009");
    public static readonly Guid ContractA2 =
        Guid.Parse("0d04a000-0000-4000-8000-00000000000a");
    public static readonly Guid PaymentA2 =
        Guid.Parse("0d04a000-0000-4000-8000-00000000000b");
    public static readonly Guid IntentA2 =
        Guid.Parse("0d04a000-0000-4000-8000-00000000000c");
    public static readonly Guid InboxA2 =
        Guid.Parse("0d04a000-0000-4000-8000-00000000000d");

    public static readonly Guid BuildingB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000001");
    public static readonly Guid FloorB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000002");
    public static readonly Guid RoomB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000003");
    public static readonly Guid TenantB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000004");
    public static readonly Guid ContractB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000005");
    public static readonly Guid PaymentB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000006");
    public static readonly Guid IntentB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000007");
    public static readonly Guid InboxB =
        Guid.Parse("0d04b000-0000-4000-8000-000000000008");

    public const long AmountA = 175000;
    public const long AmountB = 185000;
    public const long OrderCodeA = 940000001;
    public const long OrderCodeA2 = 940000003;
    public const long OrderCodeB = 940000002;
    public const string ReferenceA = "D04NATIVEJWT-A-REF";
    public const string ReferenceA2 = "D04NATIVEJWT-A2-REF";
    public const string ReferenceB = "D04NATIVEJWT-B-REF";

    private const string IdentityConnection =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    private const string StagingConnection =
        "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    [Fact]
    public async Task D04NATIVEJWT_PREP01_Provisions_fixture_and_real_identity_accounts()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_D04_NATIVE_JWT") !=
            NativeJwtOptIn)
            throw new InvalidOperationException(
                "D04 NATIVE JWT fixture refuses to run without the explicit " +
                "disposable native-JWT consent phrase.");
        var reviewerPassword = RequirePassword(ReviewerPasswordEnv);
        var ownerPassword = RequirePassword(OwnerPasswordEnv);
        var outsiderPassword = RequirePassword(OutsiderPasswordEnv);

        // 1. The Identity database must ALREADY be migrated and seeded by
        // the real DbMigrator before this fixture runs. Verify the admin
        // seed and the password-grant public client exist; otherwise fail
        // closed with an actionable message instead of a raw EF relation error.
        await AssertIdentitySeededByDbMigratorAsync();

        // 2. Canonical legacy BuildingManagement schema in payos_d04_legacy_test.
        var legacyOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
            .UseNpgsql(D04RealLegacyWriterAcceptance_Tests.Connection).Options;
        await using var legacy = new BuildingManagementDbContext(legacyOptions);
        var canonical = legacy.Database.GetMigrations().ToArray();
        Assert.NotEmpty(canonical);
        // After the P3.1 merge of the spec-kit-h4-security lane (PR #5),
        // the canonical EF chain legitimately ends with that lane's H4
        // settlement journal migration instead of the 2H deferred-commit
        // intent. Both are known, reviewed migrations on this branch.
        Assert.Equal("20261105150000_Phase2HD04H4SettlementJournal",
            canonical[^1]);
        Assert.DoesNotContain(canonical, name =>
            name.Contains("StagePayOsD04LegacySourceJournal",
                StringComparison.Ordinal));
        Assert.Empty(await legacy.Database.GetAppliedMigrationsAsync());
        await legacy.Database.MigrateAsync();
        Assert.Empty(await legacy.Database.GetPendingMigrationsAsync());

        // The D04 draft DDL is applied explicitly in this disposable DB only,
        // exactly like the existing real-writer acceptance test. Its literal
        // bytes bypass EF composite formatting ({64} regex quantifiers).
        var upSql = Assert.Single(new StagePayOsD04LegacySourceJournal()
            .UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Contains("'^[0-9a-f]{64}$'", upSql);
        await ExecuteRawSqlAsync(
            D04RealLegacyWriterAcceptance_Tests.Connection, upSql);

        // 3. Deterministic financial fixture. Building A grants both actors
        // and hosts two independent intake triples (positive lane + a second
        // lane for the mid-run permission revocation test). Building B
        // deliberately grants nobody, so the native HTTP matrix can prove a
        // cross-building review is refused.
        // The business writes go through ABP's registered EF provider (like
        // the real-writer acceptance test): a manually new'ed
        // BuildingManagementDbContext has no LazyServiceProvider and its
        // AbpDbContext.SaveChangesAsync would NRE.
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
                PaymentA, "D04 native JWT building A", "901", AmountA, "A");
            SeedContractAndPayment(db, FloorA, RoomA2, TenantA,
                ContractA2, PaymentA2, "A2");
            SeedBuilding(db, BuildingB, FloorB, RoomB, TenantB, ContractB,
                PaymentB, "D04 native JWT building B", "902", AmountB, "B");
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BuildingManagementOperatorBuildingGrants"
                  ("Id","OperatorUserId","BuildingId","GrantedByUserId",
                   "GrantedAtUtc","RevisionNumber","ExtraProperties","ConcurrencyStamp")
                VALUES ({Guid.NewGuid()},{ReviewerId},{BuildingA},{OwnerId},
                        {now.AddMinutes(-10)},{1},{"{}"},{Guid.NewGuid().ToString("N")}),
                       ({Guid.NewGuid()},{OwnerId},{BuildingA},{OwnerId},
                        {now.AddMinutes(-10)},{1},{"{}"},{Guid.NewGuid().ToString("N")})
                """);
            await seedUow.CompleteAsync();
        }

        // 4. Staging payOS schema + one confirmed/reconciled intent per
        // fixture triple. Mirrors the Host gate registration history table name.
        var stagingOptions = new DbContextOptionsBuilder<PayOsPaymentMigrationDbContext>()
            .UseNpgsql(StagingConnection,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_PayOs"))
            .Options;
        await using var staging = new PayOsPaymentMigrationDbContext(stagingOptions);
        await staging.Database.MigrateAsync();
        Assert.Empty(await staging.Database.GetPendingMigrationsAsync());
        staging.PayOsPaymentIntents.AddRange(
            NewConfirmedIntent(IntentA, BuildingA, TenantA, PaymentA,
                OrderCodeA, AmountA, ReferenceA, now),
            NewConfirmedIntent(IntentA2, BuildingA, TenantA, PaymentA2,
                OrderCodeA2, AmountA, ReferenceA2, now),
            NewConfirmedIntent(IntentB, BuildingB, TenantB, PaymentB,
                OrderCodeB, AmountB, ReferenceB, now));
        staging.PayOsWebhookInbox.AddRange(
            NewReconciledInbox(InboxA, BuildingA, IntentA, OrderCodeA,
                ReferenceA, now),
            NewReconciledInbox(InboxA2, BuildingA, IntentA2, OrderCodeA2,
                ReferenceA2, now),
            NewReconciledInbox(InboxB, BuildingB, IntentB, OrderCodeB,
                ReferenceB, now));
        await staging.SaveChangesAsync();
        staging.ChangeTracker.Clear();

        // 5. THREE real ABP Identity accounts in the DbMigrator-migrated
        // Identity schema. The password hashes are produced by the exact
        // PasswordHasher implementation the Host verifies against, then
        // persisted through a parameterized column update because
        // IdentityUser intentionally exposes no public password setter.
        using (var scope = ServiceProvider.CreateScope())
        {
            // ABP resolves a DbContext only inside a unit of work. The module
            // disables implicit transactions, so D04 money code keeps owning
            // its own SERIALIZABLE transaction; this fixture writes plain rows.
            var uowManager = scope.ServiceProvider
                .GetRequiredService<IUnitOfWorkManager>();
            using var uow = uowManager.Begin(
                new AbpUnitOfWorkOptions { IsTransactional = false });
            var identityDb = await scope.ServiceProvider
                .GetRequiredService<IDbContextProvider<NgocTrangHouseManagementSystemDbContext>>()
                .GetDbContextAsync();
            Assert.False(await identityDb.Users.AnyAsync(u =>
                u.UserName == "reviewer-uat" || u.UserName == "owner-uat" ||
                u.UserName == "outsider-uat"),
                "D04_NATIVE_JWT_FIXTURE_STALE: fixture accounts already exist; " +
                "recreate the disposable container instead of reusing it.");

            var hasher = new PasswordHasher<IdentityUser>();
            var reviewer = new IdentityUser(ReviewerId, "reviewer-uat",
                "reviewer-uat@example.invalid");
            var owner = new IdentityUser(OwnerId, "owner-uat",
                "owner-uat@example.invalid");
            var outsider = new IdentityUser(OutsiderId, "outsider-uat",
                "outsider-uat@example.invalid");

            var reviewerRoleId = Guid.NewGuid();
            var ownerRoleId = Guid.NewGuid();
            reviewer.AddRole(reviewerRoleId);
            owner.AddRole(ownerRoleId);
            identityDb.Roles.Add(new IdentityRole(reviewerRoleId, "BankReviewer"));
            identityDb.Roles.Add(new IdentityRole(ownerRoleId, "Owner"));
            identityDb.Users.AddRange(reviewer, owner, outsider);
            identityDb.Set<PermissionGrant>().AddRange(
                new PermissionGrant(Guid.NewGuid(),
                    BuildingManagementPermissions.Payments.ReviewBankEvidence,
                    "U", ReviewerId.ToString("D")),
                new PermissionGrant(Guid.NewGuid(),
                    BuildingManagementPermissions.Payments.ApproveD04Posting,
                    "U", OwnerId.ToString("D")));
            await identityDb.SaveChangesAsync();
            identityDb.ChangeTracker.Clear();

            // ABP's TokenController/SignInManager resolves a user by the
            // normalized columns, which are normally populated by
            // IdentityUserManager. This fixture intentionally bypasses the
            // manager (no OpenIddict module boot in the narrow test module),
            // so the normalized values must be written explicitly or the
            // native password grant would never find these accounts.
            await identityDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AbpUsers" SET "PasswordHash"={hasher.HashPassword(reviewer, reviewerPassword)},
                  "NormalizedUserName"={reviewer.UserName!.ToUpperInvariant()},
                  "NormalizedEmail"={reviewer.Email!.ToUpperInvariant()}
                 WHERE "Id"={ReviewerId}
                """);
            await identityDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AbpUsers" SET "PasswordHash"={hasher.HashPassword(owner, ownerPassword)},
                  "NormalizedUserName"={owner.UserName!.ToUpperInvariant()},
                  "NormalizedEmail"={owner.Email!.ToUpperInvariant()}
                 WHERE "Id"={OwnerId}
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

        // 6. Independent, durable proof from all three databases before the
        // native Host is allowed to start. No assertion depends on EF state.
        await AssertFixturePersistedAsync();

        // Fixture passwords must not linger in the test process environment.
        Environment.SetEnvironmentVariable(ReviewerPasswordEnv, null);
        Environment.SetEnvironmentVariable(OwnerPasswordEnv, null);
        Environment.SetEnvironmentVariable(OutsiderPasswordEnv, null);
    }

    private static string RequirePassword(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length < 16)
            throw new InvalidOperationException(
                "D04 native JWT fixture requires a locally generated " +
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
                "D04_NATIVE_JWT_REQUIRES_DBMIGRATOR: run the real DbMigrator " +
                "against payos_d04_identity_test before this fixture " +
                $"(users={users}, passwordGrantClient={clients}).");
    }

    private static void SeedBuilding(BuildingManagementDbContext db,
        Guid buildingId, Guid floorId, Guid roomId, Guid tenantId,
        Guid contractId, Guid paymentId, string buildingName,
        string roomNumber, long amount, string suffix)
    {
        db.Buildings.Add(new Building(buildingId, buildingName,
            "Disposable D04 native JWT address " + suffix));
        db.Floors.Add(new Floor(floorId, buildingId, 1, "Floor 1"));
        db.Rooms.Add(new Room(roomId, floorId, roomNumber,
            "D04 native JWT room " + suffix, 25m, 2, RoomStatus.Occupied));
        db.Tenants.Add(new Tenant(tenantId,
            "Disposable D04 native JWT tenant " + suffix,
            "0900000" + suffix.PadLeft(3, '0').Substring(suffix.Length - 1, 3),
            null, null, null, IdentityType.CitizenId,
            "TEST" + Guid.NewGuid().ToString("N")[..12]));
        db.Contracts.Add(NewActiveContract(contractId, roomId, amount, suffix));
        db.ContractTenants.Add(new ContractTenant(Guid.NewGuid(), contractId,
            tenantId, ContractTenantRole.PrimaryTenant));
        db.Payments.Add(new Payment(paymentId,
            "D04NATIVEPAY-" + suffix + "-" + Guid.NewGuid().ToString("N"),
            tenantId, PaymentPurpose.ContractDeposit,
            PaymentMethod.BankTransfer, PaymentChannel.VietQr,
            amount, contractId: contractId));
    }

    /// <summary>Second intake triple inside the existing building and floor.</summary>
    private static void SeedContractAndPayment(BuildingManagementDbContext db,
        Guid floorId, Guid roomId, Guid tenantId, Guid contractId,
        Guid paymentId, string suffix)
    {
        db.Rooms.Add(new Room(roomId, floorId, "9" + suffix,
            "D04 native JWT room " + suffix, 20m, 2, RoomStatus.Occupied));
        db.Contracts.Add(NewActiveContract(contractId, roomId, AmountA, suffix));
        db.ContractTenants.Add(new ContractTenant(Guid.NewGuid(), contractId,
            tenantId, ContractTenantRole.PrimaryTenant));
        db.Payments.Add(new Payment(paymentId,
            "D04NATIVEPAY-" + suffix + "-" + Guid.NewGuid().ToString("N"),
            tenantId, PaymentPurpose.ContractDeposit,
            PaymentMethod.BankTransfer, PaymentChannel.VietQr,
            AmountA, contractId: contractId));
    }

    private static Contract NewActiveContract(Guid contractId, Guid roomId,
        long amount, string suffix)
    {
        var contract = new Contract(contractId,
            "D04NATIVE-" + suffix + "-" + Guid.NewGuid().ToString("N"),
            roomId, DateTime.UtcNow.Date, null, amount);
        contract.Activate();
        return contract;
    }

    private static PayOsPaymentIntent NewConfirmedIntent(Guid id, Guid buildingId,
        Guid payerId, Guid paymentId, long orderCode, long amount,
        string reference, DateTime now)
    {
        var intent = new PayOsPaymentIntent(id, buildingId, payerId, orderCode,
            amount, now.AddDays(2), paymentId: paymentId);
        intent.AttachProviderLink("D04NATIVEJWT-LINK-" + id.ToString("N"));
        intent.MarkConfirmed(reference, now.AddMinutes(-5));
        return intent;
    }

    private static PayOsWebhookInboxRecord NewReconciledInbox(Guid id,
        Guid buildingId, Guid intentId, long orderCode, string reference,
        DateTime now)
    {
        var inbox = new PayOsWebhookInboxRecord(id, buildingId, intentId,
            orderCode, reference, new string('c', 64), now.AddMinutes(-4));
        inbox.MarkReconciled(now.AddMinutes(-3));
        return inbox;
    }

    private static async Task ExecuteRawSqlAsync(string connectionString,
        string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task AssertFixturePersistedAsync()
    {
        // Identity: 3 active users with real password hashes, exactly one
        // Owner role membership, two user-provider grants and the seeded
        // password-capable public client.
        await using (var identity = new NpgsqlConnection(IdentityConnection))
        {
            await identity.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM "AbpUsers"
                    WHERE "Id" IN (@reviewer,@owner,@outsider)
                      AND "IsActive"=TRUE AND "IsDeleted"=FALSE
                      AND "PasswordHash" IS NOT NULL
                      AND "NormalizedUserName" IS NOT NULL),
                  (SELECT count(*) FROM "AbpUserRoles"
                    WHERE "UserId"=@owner),
                  (SELECT count(*) FROM "AbpPermissionGrants"
                    WHERE "ProviderName"='U'
                      AND (("ProviderKey"=@reviewerKey AND "Name"=@reviewPermission)
                        OR ("ProviderKey"=@ownerKey AND "Name"=@ownerPermission))),
                  (SELECT count(*) FROM "OpenIddictApplications"
                    WHERE "ClientId"='NgocTrangHouseManagementSystem_App')
                """, identity);
            check.Parameters.AddWithValue("reviewer", ReviewerId);
            check.Parameters.AddWithValue("owner", OwnerId);
            check.Parameters.AddWithValue("outsider", OutsiderId);
            check.Parameters.AddWithValue("reviewerKey", ReviewerId.ToString("D"));
            check.Parameters.AddWithValue("ownerKey", OwnerId.ToString("D"));
            check.Parameters.AddWithValue("reviewPermission",
                BuildingManagementPermissions.Payments.ReviewBankEvidence);
            check.Parameters.AddWithValue("ownerPermission",
                BuildingManagementPermissions.Payments.ApproveD04Posting);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(2L, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }

        // Legacy: two buildings, three pending deposit payments, two active
        // operator-building grants, and all four draft D04 tables present.
        await using (var legacy = new NpgsqlConnection(
            D04RealLegacyWriterAcceptance_Tests.Connection))
        {
            await legacy.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM "Buildings"
                    WHERE "Id" IN (@buildingA,@buildingB) AND "IsDeleted"=FALSE),
                  (SELECT count(*) FROM "BuildingManagementPayments"
                    WHERE "Id" IN (@paymentA,@paymentA2,@paymentB) AND "Status"=1),
                  (SELECT count(*) FROM "BuildingManagementOperatorBuildingGrants"
                    WHERE "BuildingId"=@buildingA
                      AND "OperatorUserId" IN (@reviewer,@owner)
                      AND "RevokedAtUtc" IS NULL),
                  (SELECT count(*) FROM pg_tables WHERE schemaname='public'
                     AND tablename IN (
                       'BuildingManagementPayOsD04BankReviews',
                       'BuildingManagementPayOsD04OwnerApprovals',
                       'BuildingManagementPayOsD04ReceiptJournal',
                       'BuildingManagementPayOsD04Outbox'))
                """, legacy);
            check.Parameters.AddWithValue("buildingA", BuildingA);
            check.Parameters.AddWithValue("buildingB", BuildingB);
            check.Parameters.AddWithValue("paymentA", PaymentA);
            check.Parameters.AddWithValue("paymentA2", PaymentA2);
            check.Parameters.AddWithValue("paymentB", PaymentB);
            check.Parameters.AddWithValue("reviewer", ReviewerId);
            check.Parameters.AddWithValue("owner", OwnerId);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(3L, reader.GetInt64(1));
            Assert.Equal(2L, reader.GetInt64(2));
            Assert.Equal(4L, reader.GetInt64(3));
        }

        // Staging: three confirmed intents, each with one reconciled inbox.
        await using (var staging = new NpgsqlConnection(StagingConnection))
        {
            await staging.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM "BuildingManagementPayOsPaymentIntents"
                    WHERE "Id" IN (@intentA,@intentA2,@intentB) AND "Status"=2),
                  (SELECT count(*) FROM "BuildingManagementPayOsWebhookInbox"
                     WHERE "PaymentIntentId" IN (@intentA,@intentA2,@intentB)
                       AND "Status"=1)
                """, staging);
            check.Parameters.AddWithValue("intentA", IntentA);
            check.Parameters.AddWithValue("intentA2", IntentA2);
            check.Parameters.AddWithValue("intentB", IntentB);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(3L, reader.GetInt64(1));
        }
    }
}