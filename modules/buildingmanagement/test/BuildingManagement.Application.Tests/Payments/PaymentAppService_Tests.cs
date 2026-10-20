using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Shouldly;
using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Volo.Abp.Timing;
using Xunit;

namespace BuildingManagement.Payments;

public abstract class PaymentAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IPaymentAppService
        _paymentAppService;

    private readonly IRoomReservationAppService
        _roomReservationAppService;

    private readonly ITenantAppService
        _tenantAppService;

    private readonly IContractAppService
        _contractAppService;

    private readonly IContractRenewalHoldAppService
        _renewalHoldAppService;

    private readonly IRepository<Building, Guid>
        _buildingRepository;

    private readonly IRepository<Floor, Guid>
        _floorRepository;

    private readonly IRepository<Room, Guid>
        _roomRepository;

    private readonly IClock
        _clock;

    protected PaymentAppService_Tests()
    {
        _paymentAppService =
            GetRequiredService<
                IPaymentAppService>();

        _roomReservationAppService =
            GetRequiredService<
                IRoomReservationAppService>();

        _tenantAppService =
            GetRequiredService<
                ITenantAppService>();

        _contractAppService =
            GetRequiredService<
                IContractAppService>();

        _renewalHoldAppService =
            GetRequiredService<
                IContractRenewalHoldAppService>();

        _buildingRepository =
            GetRequiredService<
                IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<
                IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<
                IRepository<Room, Guid>>();

        _clock =
            GetRequiredService<IClock>();
    }

    private static string NewPaymentNumber()
    {
        return $"PAY-TEST-{Guid.NewGuid():N}";
    }

    private static string NewReservationNumber()
    {
        return $"RSV-PAY-{Guid.NewGuid():N}";
    }

    private static string NewContractNumber()
    {
        return $"HD-PAY-{Guid.NewGuid():N}";
    }

    private static string NewTransactionReference()
    {
        return $"TRX-{Guid.NewGuid():N}";
    }

    private async Task<Guid> CreateRoomAsync(
        RoomStatus status = RoomStatus.Available)
    {
        var buildingId =
            Guid.NewGuid();

        var floorId =
            Guid.NewGuid();

        var roomId =
            Guid.NewGuid();

        await WithUnitOfWorkAsync(
            async () =>
            {
                var building =
                    new Building(
                        buildingId,
                        $"Test Building {Guid.NewGuid():N}",
                        "Test Address"
                    );

                await _buildingRepository
                    .InsertAsync(
                        building,
                        autoSave: true
                    );

                var floor =
                    new Floor(
                        floorId,
                        buildingId,
                        1,
                        "Floor 1"
                    );

                await _floorRepository
                    .InsertAsync(
                        floor,
                        autoSave: true
                    );

                var room =
                    new Room(
                        roomId,
                        floorId,
                        "101",
                        "Room 101",
                        25,
                        2,
                        status
                    );

                await _roomRepository
                    .InsertAsync(
                        room,
                        autoSave: true
                    );
            }
        );

        return roomId;
    }

    private Task<TenantDto> CreateTenantAsync()
    {
        return _tenantAppService.CreateAsync(
            TenantTestData.Create()
        );
    }

    private async Task<RoomReservationDto>
        CreateReservationAsync(
            Guid roomId,
            Guid tenantId,
            DateTime? expectedMoveInDate = null)
    {
        return await _roomReservationAppService
            .CreateAsync(
                new CreateRoomReservationDto
                {
                    ReservationNumber =
                        NewReservationNumber(),

                    RoomId =
                        roomId,

                    TenantId =
                        tenantId,

                    ExpectedMoveInDate =
                        expectedMoveInDate
                        ?? _clock.Now.Date.AddDays(30),

                    QuotedMonthlyRent =
                        3_000_000,

                    RequiredDepositAmount =
                        3_000_000,

                    MinimumReservationDepositAmount =
                        500_000,

                    Notes =
                        "Payment test reservation"
                }
            );
    }

    private Task<PaymentDto>
        CreatePaymentAsync(
            Guid reservationId,
            decimal amount = 500_000,
            PaymentMethod method =
                PaymentMethod.Cash,
            PaymentChannel channel =
                PaymentChannel.Manual,
            string? paymentNumber = null,
            string? transactionReference = null)
    {
        return _paymentAppService
            .CreateReservationDepositAsync(
                new CreateReservationDepositPaymentDto
                {
                    PaymentNumber =
                        paymentNumber
                        ?? NewPaymentNumber(),

                    RoomReservationId =
                        reservationId,

                    Amount =
                        amount,

                    Method =
                        method,

                    Channel =
                        channel,

                    TransactionReference =
                        transactionReference,

                    Notes =
                        "Payment test"
                }
            );
    }

    private Task<PaymentDto>
        CompletePaymentAsync(
            Guid paymentId,
            string? transactionReference = null)
    {
        return _paymentAppService.CompleteAsync(
            paymentId,
            new CompletePaymentDto
            {
                TransactionReference =
                    transactionReference
            }
        );
    }

    private async Task<ContractDto>
        CreateActiveContractAsync(
            Guid roomId,
            DateTime endDate)
    {
        var contract =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId = roomId,

                    StartDate = _clock.Now.Date.AddDays(-30),

                    EndDate = endDate.Date,

                    MonthlyRent = 3_000_000,
                }
            );

        var tenant =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId =
                    tenant.Id,

                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        return await _contractAppService
            .ActivateAsync(
                contract.Id
            );
    }

    private async Task<RoomStatus>
        GetRoomStatusAsync(Guid roomId)
    {
        var result =
            RoomStatus.Available;

        await WithUnitOfWorkAsync(
            async () =>
            {
                var room =
                    await _roomRepository
                        .GetAsync(roomId);

                result =
                    room.Status;
            }
        );

        return result;
    }

    // PAY01
    [Fact]
    public async Task
        Should_Create_Pending_Reservation_Deposit_Payment()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var paymentNumber =
            $"pay-{Guid.NewGuid():N}";

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                paymentNumber:
                    paymentNumber
            );

        payment.Id.ShouldNotBe(
            Guid.Empty
        );

        payment.PaymentNumber.ShouldBe(
            paymentNumber.ToUpperInvariant()
        );

        payment.TenantId.ShouldBe(
            tenant.Id
        );

        payment.RoomReservationId.ShouldBe(
            reservation.Id
        );

        payment.Purpose.ShouldBe(
            PaymentPurpose.ReservationDeposit
        );

        payment.Method.ShouldBe(
            PaymentMethod.Cash
        );

        payment.Channel.ShouldBe(
            PaymentChannel.Manual
        );

        payment.Amount.ShouldBe(
            500_000
        );

        payment.Status.ShouldBe(
            PaymentStatus.Pending
        );

        payment.PaidAt.ShouldBeNull();

        payment.DepositDisposition.ShouldBeNull();

        payment.ContractId.ShouldBeNull();

        payment.InvoiceId.ShouldBeNull();
    }

    // PAY02
    [Fact]
    public async Task
        Should_Not_Create_Payment_With_Invalid_Amount()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreatePaymentAsync(
                        reservation.Id,
                        amount: 0
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidPaymentAmount
        );
    }

    // PAY03
    [Fact]
    public async Task
        Should_Not_Create_Duplicate_Payment_Number()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var paymentNumber =
            $"PAY-DUP-{Guid.NewGuid():N}";

        await CreatePaymentAsync(
            reservation.Id,
            amount: 500_000,
            paymentNumber:
                paymentNumber
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreatePaymentAsync(
                        reservation.Id,
                        amount: 500_000,
                        paymentNumber:
                            paymentNumber
                                .ToLowerInvariant()
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .PaymentNumberAlreadyExists
        );
    }

    // PAY04
    [Fact]
    public async Task
        Should_Not_Allow_Invalid_Method_Channel_Combination()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreatePaymentAsync(
                        reservation.Id,
                        method:
                            PaymentMethod.Cash,
                        channel:
                            PaymentChannel.VietQr
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidPaymentMethodChannel
        );
    }

    // PAY05
    [Fact]
    public async Task
        Should_Require_Transaction_Reference_For_Non_Cash_Payment()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000,
                method:
                    PaymentMethod.BankTransfer,
                channel:
                    PaymentChannel.VietQr
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CompletePaymentAsync(
                        payment.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TransactionReferenceRequired
        );

        var paymentAfterFailure =
            await _paymentAppService.GetAsync(
                payment.Id
            );

        paymentAfterFailure.Status.ShouldBe(
            PaymentStatus.Pending
        );
    }

    // PAY06
    [Fact]
    public async Task
        Should_Complete_BankTransfer_VietQr_With_Transaction_Reference()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000,
                method:
                    PaymentMethod.BankTransfer,
                channel:
                    PaymentChannel.VietQr
            );

        var transactionReference =
            NewTransactionReference();

        var completed =
            await CompletePaymentAsync(
                payment.Id,
                transactionReference
            );

        completed.Status.ShouldBe(
            PaymentStatus.Completed
        );

        completed.TransactionReference.ShouldBe(
            transactionReference
        );

        completed.PaidAt.ShouldNotBeNull();

        completed.DepositDisposition.ShouldBe(
            DepositDisposition.Unapplied
        );
    }

    // PAY07
    [Fact]
    public async Task
        Should_Not_Complete_First_Deposit_Below_Minimum()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 100_000
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CompletePaymentAsync(
                        payment.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationDepositBelowMinimum
        );

        var paymentAfterFailure =
            await _paymentAppService.GetAsync(
                payment.Id
            );

        paymentAfterFailure.Status.ShouldBe(
            PaymentStatus.Pending
        );

        var reservationAfterFailure =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterFailure.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );
    }

    // PAY08
    [Fact]
    public async Task
        Should_Reserve_Room_When_First_Minimum_Deposit_Is_Completed()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000
            );

        var completed =
            await CompletePaymentAsync(
                payment.Id
            );

        completed.Status.ShouldBe(
            PaymentStatus.Completed
        );

        completed.DepositDisposition.ShouldBe(
            DepositDisposition.Unapplied
        );

        completed.PaidAt.ShouldNotBeNull();

        var reservationAfterPayment =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterPayment.Status.ShouldBe(
            RoomReservationStatus.Reserved
        );

        reservationAfterPayment
            .ReservedAt
            .ShouldNotBeNull();

        reservationAfterPayment
            .DepositDueDate
            .ShouldNotBeNull();

        var paidAt =
            completed.PaidAt!.Value;

        var expectedDueDate =
            new DateTime(
                paidAt.Year,
                paidAt.Month,
                DateTime.DaysInMonth(
                    paidAt.Year,
                    paidAt.Month
                )
            );

        reservationAfterPayment
            .DepositDueDate
            .ShouldBe(
                expectedDueDate
            );

        var roomStatus =
            await GetRoomStatusAsync(
                roomId
            );

        roomStatus.ShouldBe(
            RoomStatus.Reserved
        );
    }

    // PAY09
    [Fact]
    public async Task
        Should_Allow_Subsequent_Deposit_Below_Minimum()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var firstPayment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000
            );

        await CompletePaymentAsync(
            firstPayment.Id
        );

        var secondPayment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 100_000
            );

        var completed =
            await CompletePaymentAsync(
                secondPayment.Id
            );

        completed.Status.ShouldBe(
            PaymentStatus.Completed
        );

        completed.Amount.ShouldBe(
            100_000
        );

        completed.DepositDisposition.ShouldBe(
            DepositDisposition.Unapplied
        );
    }

    // PAY10
    [Fact]
    public async Task
        Should_Not_Create_Payment_Exceeding_Remaining_Deposit()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var firstPayment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000
            );

        await CompletePaymentAsync(
            firstPayment.Id
        );

        // Remaining = 2,500,000
        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreatePaymentAsync(
                        reservation.Id,
                        amount: 2_600_000
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationDepositExceedsRemaining
        );
    }

    // PAY11
    [Fact]
    public async Task
        Should_Recheck_Remaining_Amount_When_Completing_Pending_Payment()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var firstPayment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 2_000_000
            );

        var secondPayment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 2_000_000
            );

        await CompletePaymentAsync(
            firstPayment.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CompletePaymentAsync(
                        secondPayment.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationDepositExceedsRemaining
        );

        var secondAfterFailure =
            await _paymentAppService.GetAsync(
                secondPayment.Id
            );

        secondAfterFailure.Status.ShouldBe(
            PaymentStatus.Pending
        );
    }

    // PAY12
    [Fact]
    public async Task
        Should_Block_First_Deposit_When_Room_Has_Active_Renewal_Hold()
    {
        var roomId =
            await CreateRoomAsync();

        var contractEndDate =
            _clock.Now.Date.AddDays(10);

        var currentContract =
            await CreateActiveContractAsync(
                roomId,
                contractEndDate
            );

        var futureTenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                futureTenant.Id,
                contractEndDate.AddDays(1)
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000
            );

        await _renewalHoldAppService.CreateAsync(
            new CreateContractRenewalHoldDto
            {
                CurrentContractId =
                    currentContract.Id,

                Notes =
                    "Active renewal hold"
            }
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CompletePaymentAsync(
                        payment.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ActiveRenewalHoldBlocksReservationDeposit
        );

        var paymentAfterFailure =
            await _paymentAppService.GetAsync(
                payment.Id
            );

        paymentAfterFailure.Status.ShouldBe(
            PaymentStatus.Pending
        );

        var reservationAfterFailure =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterFailure.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );
    }

    // PAY13
    [Fact]
    public async Task
        Should_Keep_Room_Occupied_When_Future_Tenant_Completes_Deposit()
    {
        var roomId =
            await CreateRoomAsync();

        var contractEndDate =
            _clock.Now.Date.AddDays(10);

        await CreateActiveContractAsync(
            roomId,
            contractEndDate
        );

        var futureTenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                futureTenant.Id,
                contractEndDate.AddDays(1)
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var reservationAfterPayment =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterPayment.Status.ShouldBe(
            RoomReservationStatus.Reserved
        );

        var roomStatus =
            await GetRoomStatusAsync(
                roomId
            );

        roomStatus.ShouldBe(
            RoomStatus.Occupied
        );
    }

    // PAY14
    [Fact]
    public async Task
        Should_Not_Allow_Duplicate_Transaction_Reference()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var transactionReference =
            NewTransactionReference();

        var firstPayment =
            await CreatePaymentAsync(
                reservation.Id,
                amount: 500_000,
                method:
                    PaymentMethod.BankTransfer,
                channel:
                    PaymentChannel.VietQr
            );

        await CompletePaymentAsync(
            firstPayment.Id,
            transactionReference
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreatePaymentAsync(
                        reservation.Id,
                        amount: 100_000,
                        method:
                            PaymentMethod.BankTransfer,
                        channel:
                            PaymentChannel.VietQr,
                        transactionReference:
                            transactionReference
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TransactionReferenceAlreadyExists
        );
    }

    // PAY15
    [Fact]
    public async Task
        Should_Cancel_Pending_Payment()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id
            );

        var cancelled =
            await _paymentAppService.CancelAsync(
                payment.Id
            );

        cancelled.Status.ShouldBe(
            PaymentStatus.Cancelled
        );

        cancelled.PaidAt.ShouldBeNull();

        cancelled.DepositDisposition.ShouldBeNull();

        var reservationAfterCancel =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterCancel.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );
    }

    // PAY16
    [Fact]
    public async Task
        Should_Not_Cancel_Completed_Payment()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _paymentAppService
                        .CancelAsync(
                            payment.Id
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .PaymentCannotBeCancelled
        );
    }

    // PAY17
    [Fact]
    public async Task
        Should_Not_Complete_Payment_Twice()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CompletePaymentAsync(
                        payment.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .PaymentCannotBeCompleted
        );
    }

    // PAY18
    [Fact]
    public async Task
        Should_Not_Create_Payment_For_Cancelled_Reservation()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        await _roomReservationAppService
            .CancelAsync(
                reservation.Id,
                new CancelRoomReservationDto
                {
                    Reason =
                        ReservationCancellationReason
                            .CustomerCancelled,

                    Notes =
                        "Cancelled before payment"
                }
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreatePaymentAsync(
                        reservation.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomReservationNotPayable
        );
    }

    // PAY19
    [Fact]
    public async Task
        Should_Get_Existing_Payment_And_List()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var created =
            await CreatePaymentAsync(
                reservation.Id
            );

        var result =
            await _paymentAppService.GetAsync(
                created.Id
            );

        result.Id.ShouldBe(
            created.Id
        );

        result.PaymentNumber.ShouldBe(
            created.PaymentNumber
        );

        var list =
            await _paymentAppService.GetListAsync();

        list.ShouldContain(
            x => x.Id == created.Id
        );
    }

    // PAY20
    [Fact]
    public async Task
        Should_Throw_When_Payment_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _paymentAppService.GetAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .PaymentNotFound
        );
    }
}