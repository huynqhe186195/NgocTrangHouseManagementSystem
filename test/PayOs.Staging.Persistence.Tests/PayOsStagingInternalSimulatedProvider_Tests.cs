using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Floors;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// P1.5 internal simulated provider + outbox pattern, run ONLY on the
/// dedicated disposable PG16 database (BM_PAYOS_EF_TEST_DB=1). No real
/// payOS traffic is possible from this assembly.
/// </summary>
public class PayOsStagingInternalSimulatedProvider_Tests
{
    private static readonly Lazy<Task> Migrations = new(async () =>
    {
        await using var db = new PayOsPaymentMigrationDbContextFactory()
            .CreateDbContext(Array.Empty<string>());
        await db.Database.MigrateAsync();
    });

    private static async Task<PayOsPaymentMigrationDbContext> DbAsync()
    {
        await Migrations.Value;
        return Db();
    }

    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException("Refusing non-disposable PG16 database.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static async Task<PayOsPaymentIntent> SeedIntentAsync(
        bool attachLink = true, bool confirmed = false, string reference = "SIM-BANK-REF")
    {
        await Migrations.Value;
        await using var db = Db();
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTime.UtcNow.Ticks, 150000,
            DateTime.UtcNow.AddMinutes(30), paymentId: Guid.NewGuid());
        if (attachLink)
        {
            intent.AttachProviderLink("SIM-P15-" + intent.OrderCode);
            if (confirmed)
            {
                db.PayOsWebhookInbox.Add(new PayOsWebhookInboxRecord(
                    Guid.NewGuid(), intent.BuildingId, intent.Id, intent.OrderCode,
                    reference, new string('b', 64), DateTime.UtcNow));
            }
        }
        db.PayOsPaymentIntents.Add(intent);
        await db.SaveChangesAsync();
        if (confirmed)
        {
            db.ChangeTracker.Clear();
            var tracked = await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id);
            tracked.MarkConfirmed(reference, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        return intent;
    }

    [Fact]
    public async Task CreateAsync_derives_deterministic_link_from_reserved_order()
    {
        var intent = await SeedIntentAsync(attachLink: false);
        await Migrations.Value;
        await using var db = Db();
        using var provider = new PayOsStagingInternalSimulatedProvider(db);
        var result = await provider.CreateAsync(new PayOsCheckoutCommand(
            intent.OrderCode, intent.AmountVnd, "NT" + intent.OrderCode,
            new Uri("https://example.test/paid"),
            new Uri("https://example.test/cancelled"),
            DateTime.UtcNow.AddMinutes(10)));

        Assert.Equal(intent.OrderCode, result.OrderCode);
        Assert.Equal(intent.AmountVnd, result.AmountVnd);
        Assert.Equal("SIM-P15-" + intent.OrderCode, result.PaymentLinkId);
        Assert.Equal("https://pay.payos.vn/web/SIM-P15-" + intent.OrderCode,
            result.CheckoutUrl.ToString());
        // The simulated provider never mutates the intent: the caller's
        // durable attach transaction owns that transition.
        var persisted = await db.PayOsPaymentIntents.AsNoTracking()
            .SingleAsync(x => x.Id == intent.Id);
        Assert.Null(persisted.PaymentLinkId);
        Assert.Equal(PayOsPaymentIntentStatus.Created, persisted.Status);
    }

    [Fact]
    public async Task CreateAsync_refuses_amount_mismatch_and_never_finds_another_order()
    {
        var intent = await SeedIntentAsync(attachLink: false);
        await Migrations.Value;
        await using var db = Db();
        using var provider = new PayOsStagingInternalSimulatedProvider(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new PayOsCheckoutCommand(
                intent.OrderCode, intent.AmountVnd + 1, "desc",
                new Uri("https://example.test/paid"),
                new Uri("https://example.test/cancelled"),
                DateTime.UtcNow.AddMinutes(10))));
    }

    [Fact]
    public async Task FindAsync_reports_pending_before_webhook_and_404_for_unknown_order()
    {
        var intent = await SeedIntentAsync(attachLink: true, confirmed: false);
        await Migrations.Value;
        await using var db = Db();
        using var provider = new PayOsStagingInternalSimulatedProvider(db);
        var pending = await provider.FindAsync(intent.OrderCode);
        Assert.NotNull(pending);
        Assert.Equal("PENDING", pending!.Status);
        Assert.Equal(0, pending.AmountPaidVnd);

        Assert.Null(await provider.FindAsync(DateTime.UtcNow.Ticks + 7));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetAsync(DateTime.UtcNow.Ticks + 7));
    }

    [Fact]
    public async Task GetAsync_reports_PAID_once_a_signed_webhook_row_exists()
    {
        // The inbox row IS the simulated provider's money-arrived signal,
        // so PAID is visible BEFORE operator reconciliation confirms.
        var intent = await SeedIntentAsync(attachLink: true, confirmed: false);
        await Migrations.Value;
        await using var db = Db();
        db.PayOsWebhookInbox.Add(new PayOsWebhookInboxRecord(
            Guid.NewGuid(), intent.BuildingId, intent.Id, intent.OrderCode,
            "SIM-PAID-" + intent.OrderCode, new string('c', 64), DateTime.UtcNow));
        await db.SaveChangesAsync();

        using var provider = new PayOsStagingInternalSimulatedProvider(db);
        var paid = await provider.GetAsync(intent.OrderCode);
        Assert.Equal("PAID", paid.Status);
        Assert.Equal(intent.AmountVnd, paid.AmountPaidVnd);
        Assert.Equal(0, paid.AmountRemainingVnd);
    }

    [Fact]
    public async Task Cancelled_intent_reports_CANCELLED_never_PAID()
    {
        var intent = await SeedIntentAsync(attachLink: true);
        await Migrations.Value;
        await using var db = Db();
        var tracked = await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id);
        tracked.CancelUnpaid();
        await db.SaveChangesAsync();

        using var provider = new PayOsStagingInternalSimulatedProvider(db);
        var snapshot = await provider.GetAsync(intent.OrderCode);
        Assert.Equal("CANCELLED", snapshot.Status);
        Assert.Equal(0, snapshot.AmountPaidVnd);
    }
}

public class PayOsStagingOutboxWriterHooks_Tests
{
    private const string Key = "isolated-payOS-webhook-checksum";
    private static long _orderSequence = DateTime.UtcNow.Ticks;
    private static readonly Lazy<Task> Migrations = new(async () =>
    {
        await using var db = new PayOsPaymentMigrationDbContextFactory()
            .CreateDbContext(Array.Empty<string>());
        await db.Database.MigrateAsync();
    });

    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException("Refusing non-disposable PG16 database.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static async Task<PayOsPaymentIntent> SeedAsync(bool attachLink = true)
    {
        await Migrations.Value;
        await using var db = Db();
        var order = Interlocked.Increment(ref _orderSequence);
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), order, 150000,
            DateTime.UtcNow.AddDays(1), paymentId: Guid.NewGuid());
        if (attachLink) intent.AttachProviderLink("LINK-" + order);
        db.PayOsPaymentIntents.Add(intent);
        await db.SaveChangesAsync();
        return intent;
    }

    private static byte[] Signed(PayOsPaymentIntent intent)
    {
        var source = "amount=" + intent.AmountVnd +
            "&code=00&currency=VND&orderCode=" + intent.OrderCode +
            "&paymentLinkId=" + intent.PaymentLinkId +
            "&reference=OUTBOX-" + intent.OrderCode;
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            code = "00",
            success = true,
            data = new
            {
                amount = intent.AmountVnd, code = "00", currency = "VND",
                orderCode = intent.OrderCode, paymentLinkId = intent.PaymentLinkId,
                reference = "OUTBOX-" + intent.OrderCode
            },
            signature
        }));
    }

    [Fact]
    public async Task Accepted_webhook_queues_reconcile_outbox_row_in_same_transaction()
    {
        var intent = await SeedAsync();
        await Migrations.Value;
        await using var db = Db();
        var writer = new PayOsStagingWebhookInboxWriter(db, Key);
        Assert.Equal(PayOsWebhookIngestResult.StoredPendingReconciliation,
            await writer.RecordAsync(Signed(intent)));

        var outbox = await db.PayOsStagingOutbox.AsNoTracking().SingleAsync(
            x => x.PaymentIntentId == intent.Id);
        Assert.Equal(PayOsStagingOutboxKind.ReconcileIntent, outbox.Kind);
        Assert.Equal(PayOsStagingOutboxStatus.Pending, outbox.Status);
        Assert.Equal(intent.BuildingId, outbox.BuildingId);
        Assert.Equal(intent.OrderCode, outbox.OrderCode);
        Assert.Equal(0, outbox.AttemptCount);
    }

    [Fact]
    public async Task Duplicate_webhook_replay_does_not_queue_second_outbox_row()
    {
        var intent = await SeedAsync();
        var raw = Signed(intent);
        await using var db1 = Db();
        Assert.Equal(PayOsWebhookIngestResult.StoredPendingReconciliation,
            await new PayOsStagingWebhookInboxWriter(db1, Key).RecordAsync(raw));
        await using var db2 = Db();
        Assert.Equal(PayOsWebhookIngestResult.Duplicate,
            await new PayOsStagingWebhookInboxWriter(db2, Key).RecordAsync(raw));
        await using var verify = Db();
        Assert.Equal(1, await verify.PayOsStagingOutbox.CountAsync(
            x => x.PaymentIntentId == intent.Id));
    }

    [Fact]
    public async Task Rejected_signature_never_queues_outbox_work()
    {
        var intent = await SeedAsync();
        await Migrations.Value;
        await using var db = Db();
        var raw = Signed(intent);
        raw[raw.Length - 2] ^= 1;
        Assert.Equal(PayOsWebhookIngestResult.Rejected,
            await new PayOsStagingWebhookInboxWriter(db, Key).RecordAsync(raw));
        Assert.Equal(0, await db.PayOsStagingOutbox.CountAsync(
            x => x.PaymentIntentId == intent.Id));
    }

    [Fact]
    public async Task Operator_reconciliation_completes_the_reconcile_outbox_row()
    {
        var intent = await SeedAsync();
        var paymentId = intent.PaymentId!.Value;
        await Migrations.Value;
        await using var db = Db();
        var writer = new PayOsStagingWebhookInboxWriter(db, Key);
        Assert.Equal(PayOsWebhookIngestResult.StoredPendingReconciliation,
            await writer.RecordAsync(Signed(intent)));
        db.ChangeTracker.Clear();

        var payment = new BuildingManagement.Payments.Payment(
            paymentId, "OUTBOX-PAY", intent.PayerId,
            PaymentPurpose.ContractDeposit,
            PaymentMethod.BankTransfer,
            PaymentChannel.VietQr,
            150000m, contractId: Guid.NewGuid());
        var payments = new Moq.Mock<Volo.Abp.Domain.Repositories.IRepository
            <BuildingManagement.Payments.Payment, Guid>>();
        payments.Setup(x => x.FindAsync(paymentId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);
        // Full contract -> room -> floor chain to the intent's building,
        // mirroring the sibling reconciliation test composition.
        var floor = new Floor(Guid.NewGuid(), intent.BuildingId, 1, "Outbox floor");
        var room = new Room(Guid.NewGuid(), floor.Id, "101", "Room 101",
            10m, 1, RoomStatus.Occupied);
        var contract = new Contract(payment.ContractId!.Value, "OUTBOX-CTR",
            room.Id, DateTime.UtcNow.AddDays(-1), null, 150000m);
        contract.Sign();
        var contracts = new Moq.Mock<Volo.Abp.Domain.Repositories.IRepository
            <Contract, Guid>>();
        contracts.Setup(x => x.FindAsync(payment.ContractId!.Value, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(contract);
        var rooms = new Moq.Mock<Volo.Abp.Domain.Repositories.IRepository
            <Room, Guid>>();
        rooms.Setup(x => x.FindAsync(room.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        var floors = new Moq.Mock<Volo.Abp.Domain.Repositories.IRepository
            <Floor, Guid>>();
        floors.Setup(x => x.FindAsync(floor.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(floor);
        var access = new Moq.Mock<BuildingManagement.Security.IBuildingCrudAccessPolicy>();
        access.Setup(x => x.EnsureResourceAsync(
                BuildingManagement.Security.BuildingBusinessResourceKind.Contract,
                It.IsAny<Guid>(),
                BuildingManagement.Security.BuildingBusinessCapability.ManageContract))
            .Returns(Task.CompletedTask);
        var user = new Moq.Mock<ICurrentUser>();
        user.SetupGet(x => x.IsAuthenticated).Returns(true);
        user.SetupGet(x => x.Id).Returns(Guid.NewGuid());

        // The simulated provider itself plays the provider side: it sees the
        // inbox row and reports PAID. One assembly, end to end, no network.
        var provider = new PayOsStagingInternalSimulatedProvider(Db()); // migration applied by earlier seed
        var service = new PayOsStagingReconciliationService(
            db, provider, payments.Object, contracts.Object, rooms.Object,
            floors.Object, access.Object, user.Object);

        var result = await service.CheckAsync(paymentId);
        Assert.True(result.ConfirmedPendingFinancialReview);

        await using var verify = Db();
        var outbox = await verify.PayOsStagingOutbox.AsNoTracking()
            .SingleAsync(x => x.PaymentIntentId == intent.Id);
        Assert.Equal(PayOsStagingOutboxKind.ReconcileIntent, outbox.Kind);
        Assert.Equal(PayOsStagingOutboxStatus.Completed, outbox.Status);
        Assert.NotNull(outbox.CompletedAtUtc);
        provider.Dispose();
    }
}
