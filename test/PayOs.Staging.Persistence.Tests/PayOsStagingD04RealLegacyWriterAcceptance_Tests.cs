using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Permissions;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Floors;
using BuildingManagement.Migrations;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;
using Volo.Abp.Timing;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// A real BuildingManagement EF writer/ledger test on the deliberately
/// disposable payos_d04_legacy_test database ONLY. The source and authority
/// implementations below are explicitly synthetic, NOT authenticated API,
/// live bank review, real payOS integration or production acceptance.
///
/// RUN ONLY from verify-payos-d04-local.ps1 after it creates its temporary
/// Postgres:16 container (127.0.0.1:15432). Never silently connect to 5432.
/// </summary>
public sealed class D04RealLegacyWriterAcceptance_Tests
    : AbpIntegratedTest<D04RealLegacyWriterAcceptanceTestModule>
{
    protected override void SetAbpApplicationCreationOptions(
        AbpApplicationCreationOptions options)
    {
        // Match the BuildingManagement test base: ABP's property-injected
        // dependencies are supplied by Autofac rather than 'new DbContext'.
        options.UseAutofac();
    }

    internal const string Connection =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5;Command Timeout=180";

    [Fact]
    public async Task D04A_REAL01_Approved_ContractDeposit_Writes_One_Payment_Allocation_Receipt_And_Outbox()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_D04_REAL_WRITER_TEST_DB") !=
                "I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") !=
                "disposable-only" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432")
            throw new InvalidOperationException(
                "D04 REAL writer acceptance test refuses anything except the " +
                "opted-in temporary postgres:16 payos_d04_legacy_test database.");

        var builder = new NpgsqlConnectionStringBuilder(Connection);
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal(15432, builder.Port);
        Assert.Equal("payos_d04_legacy_test", builder.Database);
        await using (var probe = new NpgsqlConnection(Connection))
        {
            await probe.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_database()", probe);
            Assert.Equal("payos_d04_legacy_test",
                (string?)await command.ExecuteScalarAsync());
        }

        var options = new DbContextOptionsBuilder<BuildingManagementDbContext>()
            .UseNpgsql(Connection).Options;
        await using var migrationDb = new BuildingManagementDbContext(options);

        // Full canonical migration chain, never EnsureCreated or a cloned
        // short schema. The draft D04 SQL must NOT join this migration chain.
        var canonical = migrationDb.Database.GetMigrations().ToArray();
        Assert.NotEmpty(canonical);
        Assert.Equal("20261104220000_Phase2HTypedH4DeferredCommitIntent",
            canonical[^1]);
        Assert.DoesNotContain(canonical, name =>
            name.Contains("StagePayOsD04LegacySourceJournal",
                StringComparison.Ordinal));
        Assert.Empty(await migrationDb.Database.GetAppliedMigrationsAsync());
        await migrationDb.Database.MigrateAsync();
        Assert.Empty(await migrationDb.Database.GetPendingMigrationsAsync());

        // Apply the D04 DRAFT SQL explicitly in this disposable database
        // only; no MigrationAttribute is registered in the runtime model.
        var up = Assert.Single(new StagePayOsD04LegacySourceJournal()
            .UpOperations.OfType<SqlOperation>()).Sql;
        // Keep the exact SQL regex that exposed the EF composite formatting
        // error; never remove integrity checks just to make a test pass.
        Assert.Contains("'^[0-9a-f]{64}$'", up);
        // IMPORTANT: Do NOT use EF ExecuteSqlRawAsync(up). EF passes SQL
        // through composite String.Format and tries to interpret regex
        // quantifiers such as {64} in SHA256 CHECKs as parameter indexes,
        // causing FormatException BEFORE PostgreSQL sees the DDL. This SQL
        // is a fixed, repository-owned migration script, not user input.
        // Execute its literal bytes against ONLY the isolated test database.
        await using (var ddlConnection = new NpgsqlConnection(Connection))
        {
            await ddlConnection.OpenAsync();
            await using var ddlTransaction = await ddlConnection.BeginTransactionAsync();
            await using var ddlCommand =
                new NpgsqlCommand(up, ddlConnection, ddlTransaction);
            await ddlCommand.ExecuteNonQueryAsync();
            await ddlTransaction.CommitAsync();
        }

        // Resolve the REAL BuildingManagementDbContext via ABP's registered
        // EF provider under a nontransactional ambient Unit of Work.
        // The D04 writer itself owns the SERIALIZABLE financial transaction.
        // Never call AbpDbContext.SaveChanges on a manually new'ed context.
        using var scope = ServiceProvider.CreateScope();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        using var uow = uowManager.Begin(
            new AbpUnitOfWorkOptions { IsTransactional = false });
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextProvider<BuildingManagementDbContext>>()
            .GetDbContextAsync();

        var b = Guid.NewGuid();
        var f = Guid.NewGuid();
        var r = Guid.NewGuid();
        var t = Guid.NewGuid();
        var c = Guid.NewGuid();
        var p = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var review = Guid.NewGuid();
        var approval = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var reference = "D04-SYNTH-RECEIPT-" + Guid.NewGuid().ToString("N");
        var sha256 = new string('a', 64);

        db.Buildings.Add(new Building(b, "D04 disposable building", "No real address"));
        db.Floors.Add(new Floor(f, b, 1, "Floor 1"));
        db.Rooms.Add(new Room(r, f, "301", "D04 test room", 25m, 2,
            RoomStatus.Occupied));
        db.Tenants.Add(new Tenant(t, "Disposable D04 tenant", "0900000000",
            null, null, null, IdentityType.CitizenId,
            "TEST" + Guid.NewGuid().ToString("N")[..12]));
        var contract = new Contract(c, "D04-REAL-" + Guid.NewGuid().ToString("N"),
            r, now.Date, null, 150000m);
        contract.Activate();
        db.Contracts.Add(contract);
        db.ContractTenants.Add(new ContractTenant(Guid.NewGuid(), c, t,
            ContractTenantRole.PrimaryTenant));
        db.Payments.Add(new Payment(p, "D04PAY-" + Guid.NewGuid().ToString("N"),
            t, PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, 150000m, contractId: c));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Synthetic payOS evidence is supplied directly to the writer's
        // INTERNAL interface. This is NOT a live signature/auth acceptance.
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), b, t, 912345678, 150000,
            now.AddDays(2), paymentId: p);
        intent.AttachProviderLink("SYNTH-LINK-" + intent.Id.ToString("N"));
        intent.MarkConfirmed(reference, now.AddMinutes(-5));
        var inbox = new PayOsWebhookInboxRecord(
            Guid.NewGuid(), b, intent.Id, intent.OrderCode, reference,
            new string('b', 64), now.AddMinutes(-4));
        inbox.MarkReconciled(now.AddMinutes(-3));
        var staged = new SyntheticSignedStagingEvidenceReader(intent, inbox);
        var authority = new SyntheticCommitAuthority();
        var writer = new PayOsD04LegacyPostingWriter(db, staged, authority);

        // Before independent bank evidence + a DIFFERENT Owner approval,
        // even the synthetic positive authorization cannot create cash.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.PostAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Pending,
            (await db.Payments.SingleAsync(x => x.Id == p)).Status);
        Assert.Empty(await db.DepositSettlements
            .Where(x => x.PaymentId == p).ToListAsync());
        Assert.Equal(0L, await CountAsync(db,
            "\"BuildingManagementPayOsD04ReceiptJournal\""));
        db.ChangeTracker.Clear();

        var verifiedAt = now.AddMinutes(-2);
        var approvedAt = now.AddMinutes(-1);
        // Durable synthetic proof and approval on the ACTUAL Building DB.
        // These INSERTs bypass an HTTP endpoint because the real Identity,
        // Owner approval and independently verified banking workflows have
        // not been implemented/activated. Never copy to production code.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BuildingManagementPayOsD04BankReviews"
              ("Id","PayOsIntentId","OrderCode","BuildingId","PaymentId",
               "AmountVnd","BankReference","DocumentSha256",
               "ExternalBankProofReference","ReviewerId","VerifiedAtUtc")
            VALUES ({review},{intent.Id},{intent.OrderCode},{b},{p},
                    {(long)150000},{reference},{sha256},
                    {"SYNTHETIC-TEST-EVIDENCE-NOT-A-BANK-STATEMENT"},
                    {actor},{verifiedAt})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BuildingManagementPayOsD04OwnerApprovals"
              ("Id","BankReviewId","ReviewerId","DocumentSha256",
               "OwnerApproverId","ApprovedAtUtc","ExpiresAtUtc")
            VALUES ({approval},{review},{actor},{sha256},
                    {owner},{approvedAt},{now.AddHours(1)})
            """);

        // Deliberately refuse once at the auth boundary: no partial money.
        authority.DenyNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.PostAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Pending,
            (await db.Payments.SingleAsync(x => x.Id == p)).Status);
        Assert.Equal(0L, await CountAsync(db,
            "\"BuildingManagementPayOsD04ReceiptJournal\""));
        db.ChangeTracker.Clear();

        var first = await writer.PostAsync(intent.Id);
        Assert.Equal(PayOsD04LegacyWriteDisposition.NewlyCommittedPendingReadBack,
            first.Disposition);
        Assert.NotEqual(Guid.Empty, first.ReceiptId);
        Assert.NotEqual(Guid.Empty, first.DepositSettlementId);
        Assert.NotEqual(Guid.Empty, first.OutboxId);
        db.ChangeTracker.Clear();
        var completed = await db.Payments.AsNoTracking().SingleAsync(x => x.Id == p);
        Assert.Equal(PaymentStatus.Completed, completed.Status);
        Assert.Equal(150000m, completed.Amount);
        Assert.Equal(reference, completed.TransactionReference);
        var settlement = await db.DepositSettlements.AsNoTracking()
            .SingleAsync(x => x.Id == first.DepositSettlementId);
        Assert.Equal(p, settlement.PaymentId);
        Assert.Equal(c, settlement.TargetContractId);
        Assert.Equal(DepositSettlementType.AllocateToContractDeposit,
            settlement.Type);
        Assert.Equal(DepositSettlementStatus.Completed, settlement.Status);
        Assert.Equal(150000m, settlement.Amount);
        Assert.Equal(1L, await CountAsync(db,
            "\"BuildingManagementPayOsD04ReceiptJournal\""));
        Assert.Equal(1L, await CountAsync(db,
            "\"BuildingManagementPayOsD04Outbox\""));

        // Independent re-read from canonical legacy DB, not from a return
        // value or fake staging provider. Proves matching journal + outbox.
        var receiptStore = new PayOsD04CommittedReceiptStore(db);
        var proof = await receiptStore.ReadCommittedAsync(intent.Id,
            CancellationToken.None);
        Assert.NotNull(proof);
        Assert.Equal(intent.OrderCode, proof.Plan.OrderCode);
        Assert.Equal(first.ReceiptId, proof.Proof.ReceiptId);
        Assert.Equal(first.OutboxId, proof.Proof.OutboxId);
        Assert.False(proof.OutboxAcknowledged);

        // Replay must be read-only. The synthetic authority must not be
        // asked to authorize NEW cash after a committed matching receipt.
        db.ChangeTracker.Clear();
        var leaseCallsBeforeReplay = authority.LeaseCalls;
        var replay = await writer.PostAsync(intent.Id);
        Assert.Equal(PayOsD04LegacyWriteDisposition.ExistingCommittedNeedsAck,
            replay.Disposition);
        Assert.Equal(first.ReceiptId, replay.ReceiptId);
        Assert.Equal(leaseCallsBeforeReplay, authority.LeaseCalls);
        Assert.Equal(1L, await CountAsync(db,
            "\"BuildingManagementPayOsD04ReceiptJournal\""));
        Assert.Equal(1L, await CountAsync(db,
            "\"BuildingManagementPayOsD04Outbox\""));
        Assert.Single(await db.DepositSettlements.AsNoTracking()
            .Where(x => x.PaymentId == p).ToListAsync());

        // A newly committed receipt MUST NOT automatically set the staged
        // payOS intent to Posted; a separate ACK proof is still required.
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting,
            intent.Status);

        // D04-B checks actual SHARE locks against separate disposable
        // Identity and Building schemas (NOT mocked authorization).
        await VerifyD04BIdentityLeaseAsync(scope.ServiceProvider, db, b, t, actor, owner);
        await uow.CompleteAsync();
    }


    private const string IdentityConnection =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    private static async Task VerifyD04BIdentityLeaseAsync(
        IServiceProvider services, BuildingManagementDbContext db,
        Guid buildingId, Guid tenantId, Guid bankReviewer, Guid owner)
    {
        // Use the *real* ABP Identity/Permission EF model, but not the full
        // application's startup module. Bringing that module into a narrow
        // AbpIntegratedTest starts OpenIddict permission initialization before
        // any disposable Identity schema has been created (and requires a
        // separate Autofac UoW interceptor registration).
        //
        // This fixture verifies persisted ABP entities, Owner membership and
        // real SQL row leases. It does NOT claim IdentityUserManager lifecycle,
        // native OpenIddict login or production business acceptance.
        var identityDb = await services
            .GetRequiredService<IDbContextProvider<NgocTrangHouseManagementSystemDbContext>>()
            .GetDbContextAsync();
        Assert.True(await identityDb.Database.EnsureCreatedAsync());

        var roleId = Guid.NewGuid();
        var reviewerAccount = new IdentityUser(bankReviewer,
            "d04b-review-" + bankReviewer.ToString("N"),
            "d04b-review-" + bankReviewer.ToString("N") + "@example.test");
        var ownerAccount = new IdentityUser(owner,
            "d04b-owner-" + owner.ToString("N"),
            "d04b-owner-" + owner.ToString("N") + "@example.test");
        ownerAccount.AddRole(roleId);

        // Seed through EF against the canonical ABP mapping, not handcrafted
        // Identity tables or partial raw SQL that omits entity-required fields.
        identityDb.Roles.Add(new IdentityRole(roleId, "Owner"));
        identityDb.Users.AddRange(reviewerAccount, ownerAccount);
        identityDb.Set<PermissionGrant>().AddRange(
            new PermissionGrant(Guid.NewGuid(),
                BuildingManagementPermissions.Payments.ReviewBankEvidence,
                "U", bankReviewer.ToString("D")),
            new PermissionGrant(Guid.NewGuid(),
                BuildingManagementPermissions.Payments.ApproveD04Posting,
                "U", owner.ToString("D")));
        await identityDb.SaveChangesAsync();
        identityDb.ChangeTracker.Clear();

        // Independently verify durable native Identity/Permission rows before
        // using the existing cross-DB SHARE-lock authority. This is evidence
        // of actual persistence, not a seeded boolean permission mock.
        await using (var identity = new NpgsqlConnection(IdentityConnection))
        {
            await identity.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM "AbpUsers"
                    WHERE "Id" IN (@reviewer,@owner) AND "IsActive"=TRUE)
                  + (SELECT count(*) FROM "AbpUserRoles"
                    WHERE "UserId"=@owner AND "RoleId"=@role)
                  + (SELECT count(*) FROM "AbpPermissionGrants"
                    WHERE ("ProviderKey"=@reviewerKey AND "Name"=@reviewPermission)
                       OR ("ProviderKey"=@ownerKey AND "Name"=@ownerPermission))
                """, identity);
            check.Parameters.AddWithValue("reviewer", bankReviewer);
            check.Parameters.AddWithValue("owner", owner);
            check.Parameters.AddWithValue("role", roleId);
            check.Parameters.AddWithValue("reviewerKey", bankReviewer.ToString("D"));
            check.Parameters.AddWithValue("ownerKey", owner.ToString("D"));
            check.Parameters.AddWithValue("reviewPermission",
                BuildingManagementPermissions.Payments.ReviewBankEvidence);
            check.Parameters.AddWithValue("ownerPermission",
                BuildingManagementPermissions.Payments.ApproveD04Posting);
            Assert.Equal(5L, (long)(await check.ExecuteScalarAsync() ??
                throw new InvalidOperationException("No ABP Identity persistence evidence.")));
        }

        var now = DateTime.UtcNow.AddMinutes(-10);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BuildingManagementOperatorBuildingGrants"
              ("Id","OperatorUserId","BuildingId","GrantedByUserId",
               "GrantedAtUtc","RevisionNumber","ExtraProperties","ConcurrencyStamp")
            VALUES ({Guid.NewGuid()},{bankReviewer},{buildingId},{owner},
                    {now},{1},{"{}"},{Guid.NewGuid().ToString("N")}),
                   ({Guid.NewGuid()},{owner},{buildingId},{owner},
                    {now},{1},{"{}"},{Guid.NewGuid().ToString("N")})
            """);

        // Exercise the actual restricted Identity-only SQL login before
        // the regular reviewer/Owner negative and financial posting lanes.
        // The role cannot SELECT/UPDATE native ABP tables directly and
        // acquires the SAME row locks through tightly scoped SECURITY
        // DEFINER functions. Building and payOS role gates remain separate.
        db.ChangeTracker.Clear();
        await D04BRestrictedIdentityAuthorityGate.VerifyAsync(
            db, buildingId, bankReviewer, owner);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsD04B:Enabled"] = "true",
                ["BuildingManagement:PayOsD04B:Activation"] =
                    "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY",
                ["ConnectionStrings:Default"] = IdentityConnection,
                ["ConnectionStrings:BuildingManagement"] = Connection
            }).Build();
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var authority = new PayOsD04StagingDualAuthority(db, config, host.Object);

        // The current Building SERIALIZABLE transaction is mandatory.
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            authority.AcquireCommitLeaseAsync(buildingId, Guid.NewGuid(),
                bankReviewer, owner, CancellationToken.None));

        // WRONG building must fail even with genuine reviewer/Owner roles.
        await using (var wrong = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable))
        {
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireCommitLeaseAsync(Guid.NewGuid(), Guid.NewGuid(),
                    bankReviewer, owner, CancellationToken.None));
            await wrong.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        // D04-B financial segregation negative matrix (real PostgreSQL
        // user-grant, Owner-role and building-grant rows, no role mock).
        // Self-approval must be denied even if an account owns a valid grant.
        await using (var negative = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable))
        {
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireCommitLeaseAsync(buildingId, Guid.NewGuid(),
                    bankReviewer, bankReviewer, CancellationToken.None));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireCommitLeaseAsync(buildingId, Guid.NewGuid(),
                    owner, owner, CancellationToken.None));

            // A genuine Owner cannot impersonate the independent reviewer,
            // and a genuine reviewer cannot become the Owner approver.
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireCommitLeaseAsync(buildingId, Guid.NewGuid(),
                    owner, bankReviewer, CancellationToken.None));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireReviewLeaseAsync(
                    buildingId, owner, CancellationToken.None));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireOwnerApprovalLeaseAsync(
                    buildingId, bankReviewer, CancellationToken.None));
            await negative.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        // A revoked building-specific membership must block an otherwise
        // valid reviewer+Owner pair. Roll back ONLY the synthetic test
        // mutation so the later positive financial writer lane can run.
        await using (var revokedBuilding = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable))
        {
            // The membership migration's immutable-history trigger allows
            // exactly one audited revocation: timestamp + actor + revision.
            // Updating only RevokedAtUtc is intentionally rejected (23514).
            var revokedRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "BuildingManagementOperatorBuildingGrants"
                   SET "RevokedAtUtc"={DateTime.UtcNow},
                       "RevokedByUserId"={owner},
                       "RevisionNumber"="RevisionNumber" + 1
                 WHERE "BuildingId"={buildingId}
                   AND "OperatorUserId"={bankReviewer}
                   AND "RevokedAtUtc" IS NULL
                   AND "RevisionNumber"=1
                """);
            Assert.Equal(1, revokedRows);
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireCommitLeaseAsync(buildingId, Guid.NewGuid(),
                    bankReviewer, owner, CancellationToken.None));
            await revokedBuilding.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        // Complete a SECOND actual ContractDeposit using the reviewer
        // service + different Owner approval + real two-actor authority.
        // This is a disposable test fixture, never a live bank receipt.
        await VerifyD04BManualReviewApprovalAndPostingAsync(
            db, buildingId, tenantId, bankReviewer, owner, authority);

        // Both actors currently active: acquire Identity and Building SHARE
        // locks. Concurrent disable must time out, proving the lease survives
        // beyond a preflight read and blocks revocation through commit.
        await using (var committing = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable))
        {
            await using (var lease = await authority.AcquireCommitLeaseAsync(
                buildingId, Guid.NewGuid(), bankReviewer, owner,
                CancellationToken.None))
            {
                await using var revoke = new NpgsqlConnection(IdentityConnection);
                await revoke.OpenAsync();
                await using var revokeTx = await revoke.BeginTransactionAsync();
                await using (var timeout = new NpgsqlCommand(
                    "SET LOCAL lock_timeout = '200ms'", revoke, revokeTx))
                    await timeout.ExecuteNonQueryAsync();
                await using (var disable = new NpgsqlCommand("""
                    UPDATE "AbpUsers" SET "IsActive"=FALSE
                     WHERE "Id"=@owner
                    """, revoke, revokeTx))
                {
                    disable.Parameters.AddWithValue("owner", owner);
                    var failure = await Assert.ThrowsAsync<PostgresException>(
                        () => disable.ExecuteNonQueryAsync());
                    Assert.Equal("55P03", failure.SqlState);
                }
                await revokeTx.RollbackAsync();
                await committing.CommitAsync(); // Only now may revoke proceed.
            }
        }

        // Revocation already committed BEFORE new lease => deny, even if a
        // JWT might still contain the old Owner role/permission claim.
        await using (var identity = new NpgsqlConnection(IdentityConnection))
        {
            await identity.OpenAsync();
            await using var disable = new NpgsqlCommand("""
                UPDATE "AbpUsers" SET "IsActive"=FALSE WHERE "Id"=@owner
                """, identity);
            disable.Parameters.AddWithValue("owner", owner);
            Assert.Equal(1, await disable.ExecuteNonQueryAsync());
        }
        await using (var revoked = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable))
        {
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                authority.AcquireCommitLeaseAsync(buildingId, Guid.NewGuid(),
                    bankReviewer, owner, CancellationToken.None));
            await revoked.RollbackAsync();
        }
    }


    private static async Task VerifyD04BManualReviewApprovalAndPostingAsync(
        BuildingManagementDbContext db, Guid buildingId, Guid tenantId,
        Guid reviewerId, Guid ownerId,
        PayOsD04StagingDualAuthority authority)
    {
        // The second D04-B source is now persisted in the REAL third,
        // disposable payOS PostgreSQL database, not a synthetic in-memory
        // reader. WriterOnly starts with an empty staging DB, while the full
        // bundle may already have applied the isolated payOS EF migration.
        // MigrateAsync is idempotent and never targets the canonical DB.
        await using var staging = new PayOsPaymentMigrationDbContextFactory()
            .CreateDbContext(Array.Empty<string>());
        await staging.Database.MigrateAsync();

        var floor = await db.Floors.AsNoTracking()
            .SingleAsync(x => x.BuildingId == buildingId);
        var roomId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const long amount = 250000;
        db.Rooms.Add(new Room(roomId, floor.Id, "D04B-TEST",
            "Synthetic second contract room", 20m, 2, RoomStatus.Occupied));
        var contract = new Contract(contractId,
            "D04B-CONTRACT-" + Guid.NewGuid().ToString("N"),
            roomId, now.Date, null, amount);
        contract.Activate();
        db.Contracts.Add(contract);
        db.ContractTenants.Add(new ContractTenant(
            Guid.NewGuid(), contractId, tenantId,
            ContractTenantRole.PrimaryTenant));
        db.Payments.Add(new Payment(paymentId,
            "D04B-PAY-" + Guid.NewGuid().ToString("N"),
            tenantId, PaymentPurpose.ContractDeposit,
            PaymentMethod.BankTransfer, PaymentChannel.VietQr,
            amount, contractId: contractId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var bankReference = "D04B-SYNTH-BANK-" + Guid.NewGuid().ToString("N");
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), buildingId, tenantId,
            RandomNumberGenerator.GetInt32(1, int.MaxValue), amount,
            now.AddDays(2), paymentId: paymentId);
        intent.AttachProviderLink("SYNTH-B-LINK-" + intent.Id.ToString("N"));
        intent.MarkConfirmed(bankReference, now.AddMinutes(-5));
        var inbox = new PayOsWebhookInboxRecord(
            Guid.NewGuid(), buildingId, intent.Id, intent.OrderCode,
            bankReference, new string('d', 64), now.AddMinutes(-4));
        inbox.MarkReconciled(now.AddMinutes(-3));
        staging.PayOsPaymentIntents.Add(intent);
        staging.PayOsWebhookInbox.Add(inbox);
        await staging.SaveChangesAsync();
        staging.ChangeTracker.Clear();
        var staged = new PayOsD04TrustedStagingEvidenceReader(staging);
        Assert.NotNull(await staged.ReadCurrentAsync(intent.Id, CancellationToken.None));

        var reviewer = new Mock<Volo.Abp.Users.ICurrentUser>();
        reviewer.SetupGet(x => x.IsAuthenticated).Returns(true);
        reviewer.SetupGet(x => x.Id).Returns(reviewerId);
        var owner = new Mock<Volo.Abp.Users.ICurrentUser>();
        owner.SetupGet(x => x.IsAuthenticated).Returns(true);
        owner.SetupGet(x => x.Id).Returns(ownerId);

        var manualReview = new PayOsD04ManualEvidenceService(
            db, staged, authority, reviewer.Object);
        var ownerService = new PayOsD04ManualEvidenceService(
            db, staged, authority, owner.Object);
        var writer = new PayOsD04LegacyPostingWriter(db, staged, authority);

        var document = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n")
            .CopyTo(document, 0);
        var evidence = new PayOsD04ManualBankStatementSubmission(
            bankReference, amount, document,
            "SYNTHETIC-PROTECTED-ARCHIVE-REFERENCE-NO-LIVE-STATEMENT",
            ReviewerAttestsBankStatementMatched: true);
        var falseEvidence = evidence with
        {
            ReviewerAttestsBankStatementMatched = false
        };
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            manualReview.RecordIndependentBankReviewAsync(
                intent.Id, falseEvidence));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            manualReview.RecordIndependentBankReviewAsync(
                intent.Id, evidence with { AmountVnd = amount + 1 }));
        // The first (D04-A) contract already has one Owner approval.
        // Neither rejected submission may add a second one.
        Assert.Equal(1L, await CountAsync(db,
            "\"BuildingManagementPayOsD04OwnerApprovals\""));

        var bankReviewId = await manualReview.RecordIndependentBankReviewAsync(
            intent.Id, evidence);
        Assert.NotEqual(Guid.Empty, bankReviewId);
        Assert.Equal(bankReviewId,
            await manualReview.RecordIndependentBankReviewAsync(intent.Id, evidence));

        // No Owner approval means the real writer must not credit a payment.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.PostAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Pending,
            (await db.Payments.SingleAsync(x => x.Id == paymentId)).Status);

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            manualReview.ApproveReviewedDepositAsync(bankReviewId));
        var approvalId = await ownerService.ApproveReviewedDepositAsync(bankReviewId);
        Assert.NotEqual(Guid.Empty, approvalId);
        Assert.Equal(approvalId,
            await ownerService.ApproveReviewedDepositAsync(bankReviewId));

        db.ChangeTracker.Clear();
        var posted = await writer.PostAsync(intent.Id);
        Assert.Equal(PayOsD04LegacyWriteDisposition.NewlyCommittedPendingReadBack,
            posted.Disposition);
        db.ChangeTracker.Clear();
        var paid = await db.Payments.AsNoTracking()
            .SingleAsync(x => x.Id == paymentId);
        Assert.Equal(PaymentStatus.Completed, paid.Status);
        Assert.Equal(amount, paid.Amount);
        Assert.Equal(bankReference, paid.TransactionReference);
        var settlement = await db.DepositSettlements.AsNoTracking()
            .SingleAsync(x => x.PaymentId == paymentId);
        Assert.Equal(DepositSettlementType.AllocateToContractDeposit,
            settlement.Type);
        Assert.Equal(DepositSettlementStatus.Completed, settlement.Status);
        Assert.Equal(amount, settlement.Amount);
        var receipt = await new PayOsD04CommittedReceiptStore(db)
            .ReadCommittedAsync(intent.Id, CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal(posted.ReceiptId, receipt.Proof.ReceiptId);
        Assert.Equal(posted.OutboxId, receipt.Proof.OutboxId);
        Assert.False(receipt.OutboxAcknowledged);

        db.ChangeTracker.Clear();
        var replay = await writer.PostAsync(intent.Id);
        Assert.Equal(PayOsD04LegacyWriteDisposition.ExistingCommittedNeedsAck,
            replay.Disposition);
        Assert.Equal(posted.ReceiptId, replay.ReceiptId);
        Assert.Equal(2L, await CountAsync(db,
            "\"BuildingManagementPayOsD04ReceiptJournal\""));
        Assert.Equal(2L, await CountAsync(db,
            "\"BuildingManagementPayOsD04Outbox\""));
        // A committed legacy receipt alone MUST NOT mark the payOS intent
        // Posted. This is a genuine separate-database read, not a field on
        // the original in-memory PayOsPaymentIntent.
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting,
            (await staging.PayOsPaymentIntents.AsNoTracking()
                .SingleAsync(x => x.Id == intent.Id)).Status);

        // The crash/recovery process must use purpose-scoped PostgreSQL
        // accounts, NEVER the postgres superuser used for migrations/seeding.
        // Those roles may read the immutable committed journal and update
        // ONLY ACK fields, but cannot modify Payments or signed webhook inbox.
        await D04BRestrictedAckDatabaseRolesGate.InstallAsync();

        // Now test the real failure boundary, not simply a second call
        // in this same xUnit process. A separate .NET process reads the
        // committed legacy receipt (staging still Pending), is forcibly
        // killed by its parent, then a NEW .NET process reconstructs the
        // production acknowledger and commits ACK from durable SQL only.
        // A third process must replay that ACK without writing money again.
        await D04BForcedProcessRecoveryGate.VerifyAsync(intent.Id);
        db.ChangeTracker.Clear();
        staging.ChangeTracker.Clear();

        var acknowledger = new PayOsD04StagingReceiptAcknowledger(
            staging, new PayOsD04CommittedReceiptStore(db));
        // The cross-process recovery already committed the two-DB ACK.
        Assert.Equal(PayOsD04AckOutcome.RecoveredAlreadyPosted,
            await acknowledger.AcknowledgeAsync(intent.Id));
        staging.ChangeTracker.Clear();
        Assert.Equal(PayOsPaymentIntentStatus.Posted,
            (await staging.PayOsPaymentIntents.AsNoTracking()
                .SingleAsync(x => x.Id == intent.Id)).Status);

        var acknowledged = await new PayOsD04CommittedReceiptStore(db)
            .ReadCommittedAsync(intent.Id, CancellationToken.None);
        Assert.NotNull(acknowledged);
        Assert.True(acknowledged.OutboxAcknowledged);
        Assert.Equal(posted.ReceiptId, acknowledged.Proof.ReceiptId);

        // A second real ACK is read-only with respect to payment,
        // settlement, journal and outbox; there is no second money entry.
        Assert.Equal(PayOsD04AckOutcome.RecoveredAlreadyPosted,
            await acknowledger.AcknowledgeAsync(intent.Id));

        // Exercise the SAME coordinator used by the authenticated HTTP
        // Owner-post route. An unrelated signed-in bank reviewer is denied
        // even for an existing receipt; the approved Owner may replay only
        // the existing immutable journal and perform a harmless ACK.
        var wrongActor = new PayOsD04ReviewedPostingCoordinator(
            db, writer, acknowledger, reviewer.Object);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            wrongActor.PostApprovedAsync(intent.Id));
        var approvedOwnerRoute = new PayOsD04ReviewedPostingCoordinator(
            db, writer, acknowledger, owner.Object);
        var idempotent = await approvedOwnerRoute.PostApprovedAsync(intent.Id);
        Assert.False(idempotent.AckPending);
        Assert.Equal(posted.ReceiptId, idempotent.ReceiptId);
        Assert.Equal(posted.OutboxId, idempotent.OutboxId);

        db.ChangeTracker.Clear();
        Assert.Equal(2L, await CountAsync(db,
            "\"BuildingManagementPayOsD04ReceiptJournal\""));
        Assert.Equal(2L, await CountAsync(db,
            "\"BuildingManagementPayOsD04Outbox\""));
        Assert.Single(await db.DepositSettlements.AsNoTracking()
            .Where(x => x.PaymentId == paymentId).ToListAsync());

        // A THIRD independent deposit now crosses a real ASP.NET
        // authenticated controller request and the REAL SQL-backed writer,
        // independently persisted review/Owner/Payment/journal/outbox/ACK.
        // Login remains synthetic test cookies, NOT native ABP/OpenIddict UAT.
        await D04BHttpToRealPostgreSqlGateway.VerifyAsync(
            db, staging, buildingId, tenantId, reviewerId, ownerId, authority);
    }

    private static async Task<long> CountAsync(
        BuildingManagementDbContext db, string table)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await conn.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM " + table, conn);
            return (long)(await command.ExecuteScalarAsync() ??
                throw new InvalidOperationException("Missing scalar result."));
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private sealed class SyntheticSignedStagingEvidenceReader :
        IPayOsD04TrustedStagingEvidenceReader
    {
        private readonly PayOsPaymentIntent _intent;
        private readonly PayOsWebhookInboxRecord _inbox;

        public SyntheticSignedStagingEvidenceReader(
            PayOsPaymentIntent intent, PayOsWebhookInboxRecord inbox)
        {
            _intent = intent;
            _inbox = inbox;
        }

        public Task<(PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox)?>
            ReadCurrentAsync(Guid intentId, CancellationToken cancellationToken)
        {
            (PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox)? value =
                _intent.Id == intentId ? (_intent, _inbox) : null;
            return Task.FromResult(value);
        }
    }

    private sealed class SyntheticCommitAuthority : IPayOsD04CurrentDualAuthority
    {
        public bool DenyNext { get; set; }
        public int LeaseCalls { get; private set; }

        public Task<IAsyncDisposable> AcquireCommitLeaseAsync(
            Guid buildingId, Guid paymentId, Guid bankReviewerId,
            Guid ownerApproverId, CancellationToken cancellationToken)
        {
            LeaseCalls++;
            if (DenyNext)
            {
                DenyNext = false;
                throw new InvalidOperationException(
                    "SYNTHETIC AUTH DENY: no financial commit permitted.");
            }
            if (buildingId == Guid.Empty || paymentId == Guid.Empty ||
                bankReviewerId == Guid.Empty || ownerApproverId == Guid.Empty ||
                bankReviewerId == ownerApproverId)
                throw new InvalidOperationException("Synthetic actor scope mismatch.");
            return Task.FromResult<IAsyncDisposable>(new DisposableSyntheticLease());
        }
    }

    private sealed class DisposableSyntheticLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[DependsOn(typeof(BuildingManagementEntityFrameworkCoreModule))]
public sealed class D04RealLegacyWriterAcceptanceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Fail closed before registering any DB services. The test only runs
        // from the disposable PG16 runner with all explicit safety opt-ins.
        if (Environment.GetEnvironmentVariable("BM_PAYOS_D04_REAL_WRITER_TEST_DB")
                != "I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
                != "disposable-only" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432")
            throw new InvalidOperationException(
                "D04 ABP integration fixture refuses non-disposable database.");

        // Register only the application's native ABP EF model against the
        // purpose-named disposable Identity DB. Do NOT depend on the entire
        // application EF module here: that also boots OpenIddict/permission
        // initializers before the test creates its isolated database.
        context.Services.AddAbpDbContext<NgocTrangHouseManagementSystemDbContext>(
            options => options.AddDefaultRepositories(includeAllEntities: true));
        // The ABP Identity DbContext lives in a different assembly from
        // this narrow, deliberately OpenIddict-free acceptance test module.
        // Its convention-based property injection does NOT run here.
        // In Volo.Abp.EntityFrameworkCore.AbpDbContext<T> (10.6.1),
        // UnitOfWorkManager is exactly
        //   LazyServiceProvider.LazyGetRequiredService<IUnitOfWorkManager>();
        // IInjectPropertiesService.InjectProperties() did not initialize
        // this property in the previous test run (NullReferenceException).
        //
        // Supply the REQUIRED public IAbpLazyServiceProvider property
        // explicitly before ABP's UnitOfWorkDbContextProvider<T>.Initialize.
        // We retain the real provider/options, DbContext model and UoW,
        // without booting full OpenIddict/PermissionManagement or weakening
        // the database/money acceptance assertions.
        context.Services.AddTransient<NgocTrangHouseManagementSystemDbContext>(sp =>
        {
            var identityDb = ActivatorUtilities.CreateInstance<
                NgocTrangHouseManagementSystemDbContext>(sp);
            try
            {
                identityDb.LazyServiceProvider =
                    sp.GetRequiredService<IAbpLazyServiceProvider>();
                // Fail at construction with a meaningful dependency error
                // instead of a delayed null reference inside ABP Initialize.
                _ = identityDb.UnitOfWorkManager;
                return identityDb;
            }
            catch
            {
                identityDb.Dispose();
                throw;
            }
        });

        var connection = D04RealLegacyWriterAcceptance_Tests.Connection;
        var identityConnection =
            "Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;" +
            "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
        Configure<AbpClockOptions>(options => options.Kind = DateTimeKind.Utc);
        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = identityConnection;
            options.ConnectionStrings[BuildingManagementDbProperties.ConnectionStringName] =
                connection;
        });
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        // No implicit ABP transaction: D04 writer explicitly owns its
        // SERIALIZABLE money-commit transaction.
        Configure<AbpUnitOfWorkDefaultOptions>(options =>
        {
            options.TransactionBehavior = UnitOfWorkTransactionBehavior.Disabled;
        });
    }
}
