using System;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
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
        PayOsPaymentLinkSnapshot providerSnapshot)
    {
        var payments = new Mock<IRepository<Payment, Guid>>();
        payments.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        var access = new Mock<IBuildingCrudAccessPolicy>();
        access.Setup(x => x.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract,
                payment.ContractId!.Value,
                BuildingBusinessCapability.ManageContract))
            .Returns(Task.CompletedTask);
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.IsAuthenticated).Returns(true);
        user.SetupGet(x => x.Id).Returns(Guid.NewGuid());
        return new PayOsStagingReconciliationService(
            db, new PaidFakeProvider(providerSnapshot), payments.Object,
            access.Object, user.Object);
    }

    [Fact]
    public async Task Signed_inbox_and_provider_full_payment_only_confirm_for_independent_D04_review()
    {
        var (intent, payment) = await SeedAsync(inbox: true);
        await using var db = Db();
        var service = Service(db, payment, FullPaid(intent));
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
    public async Task Provider_paid_but_no_signed_inbox_does_not_confirm_funds()
    {
        var (intent, payment) = await SeedAsync();
        await using var db = Db();
        var result = await Service(db, payment, FullPaid(intent)).CheckAsync(payment.Id);
        Assert.False(result.ConfirmedPendingFinancialReview);
        Assert.Equal(PayOsReconciliationDisposition.ConflictRequiresReview, result.Disposition);
        var original = await db.PayOsPaymentIntents.AsNoTracking()
            .SingleAsync(x => x.Id == intent.Id);
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment, original.Status);
    }

    [Fact]
    public async Task Cancelled_intent_with_late_paid_status_requires_manual_review()
    {
        var (intent, payment) = await SeedAsync(cancelled: true, inbox: true);
        await using var db = Db();
        var result = await Service(db, payment, FullPaid(intent)).CheckAsync(payment.Id);
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
