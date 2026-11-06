using System;
using System.Collections.Concurrent;
using System.Net.Http;
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
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

public class PayOsStagingCheckoutService_Tests
{
    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException("Only the dedicated disposable PostgreSQL database is allowed.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static (PayOsStagingCheckoutService Service, Guid PaymentId) CreateService(
        PayOsPaymentMigrationDbContext db, IPayOsPaymentLinkProvider provider,
        bool authorized = true, decimal amount = 150000, Guid? reusedPaymentId = null)
    {
        var building = Guid.NewGuid();
        var floor = new Floor(Guid.NewGuid(), building, 3, "Third floor");
        var room = new Room(Guid.NewGuid(), floor.Id, "301", "Room 301",
            20m, 2, RoomStatus.Occupied);
        var contract = new Contract(Guid.NewGuid(), "CTR-QA", room.Id,
            DateTime.UtcNow.AddDays(-1), null, 150000m);
        contract.Sign();
        var paymentId = reusedPaymentId ?? Guid.NewGuid();
        var payment = new Payment(paymentId, "PAY-QA", Guid.NewGuid(),
            PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, amount, contractId: contract.Id);

        var payments = new Mock<IRepository<Payment, Guid>>();
        var contracts = new Mock<IRepository<Contract, Guid>>();
        var rooms = new Mock<IRepository<Room, Guid>>();
        var floors = new Mock<IRepository<Floor, Guid>>();
        payments.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        contracts.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(contract);
        rooms.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(room);
        floors.Setup(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(floor);

        var access = new Mock<IBuildingCrudAccessPolicy>();
        access.Setup(x => x.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract,
                contract.Id, BuildingBusinessCapability.ManageContract))
            .Returns(() => authorized
                ? Task.CompletedTask
                : Task.FromException(new AbpAuthorizationException("Denied")));
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.IsAuthenticated).Returns(true);
        user.SetupGet(x => x.Id).Returns(Guid.NewGuid());

        var service = new PayOsStagingCheckoutService(
            db, provider, payments.Object, contracts.Object, rooms.Object,
            floors.Object, access.Object, user.Object,
            new PayOsStagingCheckoutOptions(
                new Uri("https://example.test/paid"),
                new Uri("https://example.test/cancelled")));
        return (service, paymentId);
    }

    [Fact]
    public async Task Repeating_a_checkout_keeps_one_db_intent_and_one_provider_order()
    {
        using var provider = new FakeProvider();
        await using var db = Db();
        var (service, paymentId) = CreateService(db, provider);
        var first = await service.StartOrRecoverAsync(paymentId);
        var second = await service.StartOrRecoverAsync(paymentId);
        Assert.Equal(PayOsCheckoutViewStatus.Created, first.Status);
        Assert.Equal(PayOsCheckoutViewStatus.Recovered, second.Status);
        Assert.NotNull(first.QrCode);
        Assert.Null(second.QrCode);
        Assert.Equal(first.OrderCode, second.OrderCode);
        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        Assert.Equal(1, provider.Created);
        Assert.Equal(1, await db.PayOsPaymentIntents.CountAsync(x => x.PaymentId == paymentId));
    }

    [Fact]
    public async Task Ambiguous_create_timeout_recovers_same_order_instead_of_new_request()
    {
        using var provider = new FakeProvider { FailAfterProviderCreates = true };
        await using var db = Db();
        var (service, paymentId) = CreateService(db, provider);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.StartOrRecoverAsync(paymentId));
        var persisted = await db.PayOsPaymentIntents.AsNoTracking()
            .SingleAsync(x => x.PaymentId == paymentId);
        Assert.Equal(PayOsPaymentIntentStatus.Created, persisted.Status);

        provider.FailAfterProviderCreates = false;
        var recovered = await service.StartOrRecoverAsync(paymentId);
        Assert.Equal(PayOsCheckoutViewStatus.Recovered, recovered.Status);
        Assert.Equal(persisted.OrderCode, recovered.OrderCode);
        Assert.Equal(1, provider.Created);
        Assert.Equal(1, await db.PayOsPaymentIntents.CountAsync(x => x.PaymentId == paymentId));
    }

    [Fact]
    public async Task Missing_building_contract_capability_prevents_checkout_and_outbound_provider_call()
    {
        using var provider = new FakeProvider();
        await using var db = Db();
        var (service, paymentId) = CreateService(db, provider, authorized: false);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            service.StartOrRecoverAsync(paymentId));
        Assert.Equal(0, provider.Created);
        Assert.Equal(0, await db.PayOsPaymentIntents.CountAsync(x => x.PaymentId == paymentId));
    }

    [Fact]
    public async Task Invalid_deposit_amount_is_not_sent_to_provider()
    {
        using var provider = new FakeProvider();
        await using var db = Db();
        var (service, paymentId) = CreateService(db, provider, amount: 150000.50m);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            service.StartOrRecoverAsync(paymentId));
        Assert.Equal(0, provider.Created);
    }

    private sealed class FakeProvider : IPayOsPaymentLinkProvider, IDisposable
    {
        private readonly ConcurrentDictionary<long, PayOsPaymentLinkSnapshot> _links = new();
        public int Created { get; private set; }
        public bool FailAfterProviderCreates { get; set; }

        public Task<PayOsCheckoutResult> CreateAsync(
            PayOsCheckoutCommand command, CancellationToken cancellationToken = default)
        {
            Created++;
            var link = "STAGE" + command.OrderCode;
            _links[command.OrderCode] = new PayOsPaymentLinkSnapshot(
                command.OrderCode, command.AmountVnd, 0, command.AmountVnd,
                link, "PENDING");
            if (FailAfterProviderCreates)
                throw new HttpRequestException("Simulated provider response lost");
            return Task.FromResult(new PayOsCheckoutResult(
                command.OrderCode, command.AmountVnd, link,
                new Uri("https://pay.payos.vn/web/" + link), "SYNTHETIC-QR"));
        }

        public Task<PayOsPaymentLinkSnapshot?> FindAsync(
            long orderCode, CancellationToken cancellationToken = default)
        {
            _links.TryGetValue(orderCode, out var result);
            return Task.FromResult(result);
        }

        public Task<PayOsPaymentLinkSnapshot> GetAsync(
            long orderCode, CancellationToken cancellationToken = default)
        {
            if (!_links.TryGetValue(orderCode, out var result))
                throw new HttpRequestException("No provider order");
            return Task.FromResult(result);
        }

        public void Dispose() { }
    }
}
