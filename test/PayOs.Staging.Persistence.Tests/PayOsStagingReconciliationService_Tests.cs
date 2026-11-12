using System;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Contracts;
using BuildingManagement.Rooms;
using BuildingManagement.Floors;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Exercises the read-only D04 boundary: a merchant PAID status together with
/// an accepted signed inbox may confirm the payOS intent for later independent
/// financial review, but NEVER completes Payment or creates cash postings.
/// </summary>
public class PayOsStagingReconciliationService_Tests
{
    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException("Refusing non-disposable PG16 database.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static async Task<(PayOsPaymentIntent Intent, Payment Payment)> SeedAsync(
        bool inbox = false, bool cancelled = false)
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var payment = new Payment(Guid.NewGuid(), "PAYOS-STAGE-ONLY", tenant,
            PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, 150000m, contractId: Guid.NewGuid());
        var intent = new PayOsPaymentIntent(Guid.NewGuid(), Guid.NewGuid(), tenant,
            DateTime.UtcNow.Ticks, 150000, DateTime.UtcNow.AddHours(1),
            paymentId: payment.Id);
        intent.AttachProviderLink("STAGE" + intent.OrderCode);
        if (cancelled) intent.CancelUnpaid();
        db.PayOsPaymentIntents.Add(intent);
        if (inbox)
        {
            db.PayOsWebhookInbox.Add(new PayOsWebhookInboxRecord(
                Guid.NewGuid(), intent.BuildingId, intent.Id, intent.OrderCode,
                "BANK-" + intent.OrderCode, new string('a', 64), DateTime.UtcNow));
        }
        await db.SaveChangesAsync();
        return (intent, payment);
    }

    private static PayOsStagingReconciliationService Service(
        PayOsPaymentMigrationDbContext db, Payment payment,
        PayOsPaymentIntent intent, PayOsPaymentLinkSnapshot providerSnapshot,
        Guid? overrideBuildingId = null,
        bool revokeAfterFirstAccess = false)
    {
        var payments = new Mock<IRepository<Payment, Guid>>();
        var contracts = new Mock<IRepository<Contract, Guid>>();
        var rooms = new Mock<IRepository<Room, Guid>>();
        var floors = new Mock<IRepository<Floor, Guid>>();
        var floor = new Floor(Guid.NewGuid(), overrideBuildingId ?? intent.BuildingId, 1, "Test floor");
        var room = new Room(Guid.NewGuid(), floor.Id, "101", "Room 101", 10m, 1, RoomStatus.Occupied);
        var contract = new Contract(payment.ContractId!.Value, "RECON", room.Id, DateTime.UtcNow.AddDays(-1), null, 150000m);
        contract.Sign();
        payments.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        contracts.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(contract);
        rooms.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(room);
        floors.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(floor);
        var access = new Mock<IBuildingCrudAccessPolicy>();
        var accessCount = 0;
        access.Setup(x => x.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract,
                payment.ContractId!.Value,
                BuildingBusinessCapability.ManageContract))
            .Returns(() => !revokeAfterFirstAccess || ++accessCount == 1
                ? Task.CompletedTask
                : Task.FromException(new AbpAuthorizationException("Building grant revoked during provider lookup.")));

        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.IsAuthenticated).Returns(true);
        user.SetupGet(x => x.Id).Returns(Guid.NewGuid());
        return new PayOsStagingReconciliationService(
            db, new PaidFakeProvider(providerSnapshot), payments.Object,
            contracts.Object, rooms.Object, floors.Object,
            access.Object, user.Object);
    }

    [Fact]
    public async Task Signed_inbox_and_provider_full_payment_only_confirm_for_independent_D04_review()
    {
        var (intent, payment) = await SeedAsync(inbox: true);
        await using var db = Db();
        var service = Service(db, payment, intent, FullPaid(intent));
        var result = await service.CheckAsync(payment.Id);
        Assert.True(result.ConfirmedPendingFinancialReview);
        Assert.Equal(PayOsReconciliationDisposition.FundsDetectedPendingIndependentPosting,
            result.Disposition);
        var stored = await db.PayOsPaymentIntents.AsNoTracking().SingleAsync(x => x.Id == intent.Id);
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting, stored.Status);
        Assert.Equal("BANK-" + intent.OrderCode, stored.ProviderReference);
        var received = await db.PayOsWebhookInbox.AsNoTracking()
            .SingleAsync(x => x.PaymentIntentId == intent.Id);
        Assert.Equal(PayOsWebhookInboxStatus.ReconciledNoPosting, received.Status);
        // The D04 source Payment remains pending: no ledger or receipt was written.
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }


    [Fact]
    public async Task Repeated_reconciliation_is_idempotent_and_keeps_D04_pending()
    {
        var (intent, payment) = await SeedAsync(inbox: true);
        await using var db = Db();
        var service = Service(db, payment, intent, FullPaid(intent));
        var first = await service.CheckAsync(payment.Id);
        var second = await service.CheckAsync(payment.Id);
        Assert.True(first.ConfirmedPendingFinancialReview);
        Assert.True(second.ConfirmedPendingFinancialReview);
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting,
            (await db.PayOsPaymentIntents.AsNoTracking().SingleAsync(x => x.Id == intent.Id)).Status);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task Provider_paid_but_no_signed_inbox_does_not_confirm_funds()
    {
        var (intent, payment) = await SeedAsync();
        await using var db = Db();
        var result = await Service(db, payment, intent, FullPaid(intent)).CheckAsync(payment.Id);
        Assert.False(result.ConfirmedPendingFinancialReview);
        Assert.Equal(PayOsReconciliationDisposition.ConflictRequiresReview, result.Disposition);
        var original = await db.PayOsPaymentIntents.AsNoTracking()
            .SingleAsync(x => x.Id == intent.Id);
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment, original.Status);
    }

    [Fact]
    public async Task Retargeted_contract_to_another_building_cannot_reconcile_old_payment()
    {
        var (intent, payment) = await SeedAsync(inbox: true);
        await using var db = Db();
        var result = await Service(
            db, payment, intent, FullPaid(intent), Guid.NewGuid()).CheckAsync(payment.Id);
        Assert.False(result.ConfirmedPendingFinancialReview);
        Assert.Equal(PayOsReconciliationDisposition.ConflictRequiresReview, result.Disposition);
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment,
            (await db.PayOsPaymentIntents.AsNoTracking()
                .SingleAsync(x => x.Id == intent.Id)).Status);
    }

    [Fact]
    public async Task Revoked_building_grant_after_provider_lookup_cannot_confirm_or_post()
    {
        var (intent, payment) = await SeedAsync(inbox: true);
        await using var db = Db();
        var service = Service(db, payment, intent, FullPaid(intent),
            revokeAfterFirstAccess: true);

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            service.CheckAsync(payment.Id));

        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment,
            (await db.PayOsPaymentIntents.AsNoTracking()
                .SingleAsync(x => x.Id == intent.Id)).Status);
        Assert.Equal(PayOsWebhookInboxStatus.PendingReconciliation,
            (await db.PayOsWebhookInbox.AsNoTracking()
                .SingleAsync(x => x.PaymentIntentId == intent.Id)).Status);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task Cancelled_intent_with_late_paid_status_requires_manual_review()
    {
        var (intent, payment) = await SeedAsync(cancelled: true, inbox: true);
        await using var db = Db();
        var result = await Service(db, payment, intent, FullPaid(intent)).CheckAsync(payment.Id);
        Assert.False(result.ConfirmedPendingFinancialReview);
        Assert.Equal(PayOsReconciliationDisposition.LateFundsRequireReview, result.Disposition);
        Assert.Equal(PayOsPaymentIntentStatus.Cancelled,
            (await db.PayOsPaymentIntents.AsNoTracking().SingleAsync(x => x.Id == intent.Id)).Status);
    }

    private static PayOsPaymentLinkSnapshot FullPaid(PayOsPaymentIntent intent) =>
        new(intent.OrderCode, intent.AmountVnd, intent.AmountVnd, 0,
            intent.PaymentLinkId!, "PAID");

    private sealed class PaidFakeProvider : IPayOsPaymentLinkProvider
    {
        private readonly PayOsPaymentLinkSnapshot _snapshot;
        public PaidFakeProvider(PayOsPaymentLinkSnapshot snapshot) => _snapshot = snapshot;
        public Task<PayOsPaymentLinkSnapshot> GetAsync(
            long orderCode, CancellationToken cancellationToken = default)
            => Task.FromResult(_snapshot);
        public Task<PayOsPaymentLinkSnapshot?> FindAsync(
            long orderCode, CancellationToken cancellationToken = default)
            => Task.FromResult<PayOsPaymentLinkSnapshot?>(_snapshot);
        public Task<PayOsCheckoutResult> CreateAsync(
            PayOsCheckoutCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
