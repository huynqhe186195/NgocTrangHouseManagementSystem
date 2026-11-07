using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Floors;
using BuildingManagement.Migrations;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Volo.Abp;
using Volo.Abp.Data;
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
        await uow.CompleteAsync();
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

        var connection = D04RealLegacyWriterAcceptance_Tests.Connection;
        Configure<AbpClockOptions>(options => options.Kind = DateTimeKind.Utc);
        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = connection;
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
