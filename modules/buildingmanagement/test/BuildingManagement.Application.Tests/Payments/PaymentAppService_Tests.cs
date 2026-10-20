using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Floors;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly IRepository<DepositSettlement, Guid>
    _depositSettlementRepository;

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

        _depositSettlementRepository =
    GetRequiredService<
        IRepository<DepositSettlement, Guid>>();
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

    private Task<PaymentDto> CreatePaymentAsync(
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

    private Task<PaymentDto> CreateContractDepositPaymentAsync(
        Guid contractId,
        decimal amount = 500_000,
        PaymentMethod method =
            PaymentMethod.Cash,
        PaymentChannel channel =
            PaymentChannel.Manual,
        string? paymentNumber = null,
        string? transactionReference = null)
    {
        return _paymentAppService
            .CreateContractDepositAsync(
                new CreateContractDepositPaymentDto
                {
                    PaymentNumber =
                        paymentNumber
                        ?? NewPaymentNumber(),

                    ContractId =
                        contractId,

                    Amount =
                        amount,

                    Method =
                        method,

                    Channel =
                        channel,

                    TransactionReference =
                        transactionReference,

                    Notes =
                        "Contract deposit payment test"
                }
            );
    }

    private async Task<DepositSettlement?> FindSettlementByPaymentIdAsync(
        Guid paymentId)
    {
        DepositSettlement? result = null;

        await WithUnitOfWorkAsync(
            async () =>
            {
                result =
                    await _depositSettlementRepository
                        .FindAsync(
                            x =>
                                x.PaymentId ==
                                    paymentId
                        );
            }
        );

        return result;
    }

    private Task<PaymentDto> CompletePaymentAsync(
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

    // PAY21
    [Fact]
    public async Task Should_Create_Pending_Contract_Deposit_Payment()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateActiveContractAsync(
                roomId,
                _clock.Now.Date.AddMonths(6)
            );

        var payment =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                1_000_000
            );

        payment.Purpose.ShouldBe(
            PaymentPurpose.ContractDeposit
        );

        payment.ContractId.ShouldBe(
            contract.Id
        );

        payment.RoomReservationId
            .ShouldBeNull();

        payment.Amount.ShouldBe(
            1_000_000
        );

        payment.Status.ShouldBe(
            PaymentStatus.Pending
        );

        payment.PaidAt.ShouldBeNull();

        payment.DepositDisposition
            .ShouldBeNull();
    }

    // PAY22
    [Fact]
    public async Task Should_Create_Completed_Settlement_When_Contract_Deposit_Is_Completed()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateActiveContractAsync(
                roomId,
                _clock.Now.Date.AddMonths(6)
            );

        var payment =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                1_000_000
            );

        var completed =
            await CompletePaymentAsync(
                payment.Id
            );

        completed.Status.ShouldBe(
            PaymentStatus.Completed
        );

        completed.PaidAt.ShouldNotBeNull();

        var settlement =
            await FindSettlementByPaymentIdAsync(
                payment.Id
            );

        settlement.ShouldNotBeNull();

        settlement!.Type.ShouldBe(
            DepositSettlementType
                .AllocateToContractDeposit
        );

        settlement.Status.ShouldBe(
            DepositSettlementStatus.Completed
        );

        settlement.Amount.ShouldBe(
            1_000_000
        );

        settlement.PaymentId.ShouldBe(
            payment.Id
        );

        settlement.TargetContractId.ShouldBe(
            contract.Id
        );

        settlement.SourceContractId
            .ShouldBeNull();

        settlement.SourceRoomReservationId
            .ShouldBeNull();

        settlement.CompletedAt
            .ShouldNotBeNull();
    }

    // PAY23
    [Fact]
    public async Task Should_Recheck_Remaining_Contract_Deposit_When_Completing()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateActiveContractAsync(
                roomId,
                _clock.Now.Date.AddMonths(6)
            );

        var first =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                2_000_000
            );

        var second =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                2_000_000
            );

        await CompletePaymentAsync(
            first.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CompletePaymentAsync(
                        second.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractDepositExceedsRemaining
        );

        var secondAfterFailure =
            await _paymentAppService.GetAsync(
                second.Id
            );

        secondAfterFailure.Status.ShouldBe(
            PaymentStatus.Pending
        );

        var settlement =
            await FindSettlementByPaymentIdAsync(
                second.Id
            );

        settlement.ShouldBeNull();
    }

    // PAY24
    [Fact]
    public async Task Should_Not_Create_Contract_Deposit_When_Already_Fully_Paid()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateActiveContractAsync(
                roomId,
                _clock.Now.Date.AddMonths(6)
            );

        var payment =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                3_000_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateContractDepositPaymentAsync(
                        contract.Id,
                        100_000
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractDepositAlreadyFullyPaid
        );
    }

    // PAY25
    [Fact]
    public async Task Should_Require_Transaction_Reference_For_Contract_Deposit()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateActiveContractAsync(
                roomId,
                _clock.Now.Date.AddMonths(6)
            );

        var payment =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                500_000,
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

        var settlement =
            await FindSettlementByPaymentIdAsync(
                payment.Id
            );

        settlement.ShouldBeNull();
    }

    // PAY26
    [Fact]
    public async Task Should_Not_Create_Contract_Deposit_For_Draft_Contract()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId =
                        roomId,

                    StartDate =
                        _clock.Now.Date,

                    EndDate =
                        _clock.Now.Date
                            .AddMonths(6),

                    MonthlyRent =
                        3_000_000
                }
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateContractDepositPaymentAsync(
                        contract.Id,
                        500_000
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractDepositNotPayable
        );
    }

    // PAY27
    [Fact]
    public async Task Should_Not_Create_Contract_Deposit_Exceeding_Remaining()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateActiveContractAsync(
                roomId,
                _clock.Now.Date.AddMonths(6)
            );

        var first =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                500_000
            );

        await CompletePaymentAsync(
            first.Id
        );

        // Required = 3,000,000
        // Covered  =   500,000
        // Remaining = 2,500,000

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateContractDepositPaymentAsync(
                        contract.Id,
                        2_600_000
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractDepositExceedsRemaining
        );
    }

    private async Task<ContractDto> CreateActiveContractForTenantAsync(
        Guid roomId,
        Guid tenantId,
        DateTime? endDate = null)
    {
        var contract =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId =
                        roomId,

                    StartDate =
                        _clock.Now.Date,

                    EndDate =
                        (endDate
                         ?? _clock.Now.Date.AddMonths(6))
                        .Date,

                    MonthlyRent =
                        3_000_000
                }
            );

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId =
                    tenantId,

                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        return await _contractAppService
            .ActivateAsync(
                contract.Id
            );
    }

    private async Task<
    List<DepositSettlement>>
    GetReservationAllocationsAsync(
        Guid reservationId,
        Guid contractId)
    {
        var result =
            new List<DepositSettlement>();

        await WithUnitOfWorkAsync(
            async () =>
            {
                result =
                    await _depositSettlementRepository
                        .GetListAsync(
                            x =>
                                x.SourceRoomReservationId ==
                                    reservationId
                                &&
                                x.TargetContractId ==
                                    contractId
                                &&
                                x.Type ==
                                    DepositSettlementType
                                        .AllocateToContractDeposit
                        );
            }
        );

        return result;
    }

    // PAY28
    [Fact]
    public async Task
        Should_Allocate_Reservation_Deposit_To_Contract_Deposit()
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
                500_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                tenant.Id
            );

        var result =
            await _paymentAppService
                .AllocateReservationDepositToContractAsync(
                    reservation.Id,
                    contract.Id
                );

        result.AllocatedAmount.ShouldBe(
            500_000
        );

        result.CoveredDeposit.ShouldBe(
            500_000
        );

        result.RemainingDeposit.ShouldBe(
            2_500_000
        );

        var reservationAfter =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfter.Status.ShouldBe(
            RoomReservationStatus
                .ConvertedToContract
        );

        reservationAfter
            .ConvertedContractId
            .ShouldBe(
                contract.Id
            );

        var settlements =
            await GetReservationAllocationsAsync(
                reservation.Id,
                contract.Id
            );

        settlements.Count.ShouldBe(1);

        settlements[0].PaymentId.ShouldBe(
            payment.Id
        );

        settlements[0].Amount.ShouldBe(
            500_000
        );

        settlements[0].Status.ShouldBe(
            DepositSettlementStatus.Completed
        );
    }

    // PAY30
    [Fact]
    public async Task
        Should_Fully_Cover_Contract_Deposit_From_Full_Reservation_Deposit()
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
                3_000_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                tenant.Id
            );

        var result =
            await _paymentAppService
                .AllocateReservationDepositToContractAsync(
                    reservation.Id,
                    contract.Id
                );

        result.AllocatedAmount.ShouldBe(
            3_000_000
        );

        result.CoveredDeposit.ShouldBe(
            3_000_000
        );

        result.RemainingDeposit.ShouldBe(
            0
        );
    }

    // PAY31
    [Fact]
    public async Task
        Should_Allocate_Multiple_Reservation_Deposit_Payments()
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

        var first =
            await CreatePaymentAsync(
                reservation.Id,
                500_000
            );

        await CompletePaymentAsync(
            first.Id
        );

        var second =
            await CreatePaymentAsync(
                reservation.Id,
                1_000_000
            );

        await CompletePaymentAsync(
            second.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                tenant.Id
            );

        var result =
            await _paymentAppService
                .AllocateReservationDepositToContractAsync(
                    reservation.Id,
                    contract.Id
                );

        result.AllocatedAmount.ShouldBe(
            1_500_000
        );

        result.CoveredDeposit.ShouldBe(
            1_500_000
        );

        result.RemainingDeposit.ShouldBe(
            1_500_000
        );

        var settlements =
            await GetReservationAllocationsAsync(
                reservation.Id,
                contract.Id
            );

        settlements.Count.ShouldBe(2);

        settlements.Sum(
            x => x.Amount
        ).ShouldBe(
            1_500_000
        );
    }

    // PAY32
    [Fact]
    public async Task
        Should_Not_Allocate_Reservation_Deposit_To_Different_Room()
    {
        var reservationRoomId =
            await CreateRoomAsync();

        var contractRoomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                reservationRoomId,
                tenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                500_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                contractRoomId,
                tenant.Id
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _paymentAppService
                        .AllocateReservationDepositToContractAsync(
                            reservation.Id,
                            contract.Id
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationContractMismatch
        );
    }

    // PAY33
    [Fact]
    public async Task
        Should_Not_Allocate_When_Primary_Tenant_Does_Not_Match()
    {
        var roomId =
            await CreateRoomAsync();

        var reservationTenant =
            await CreateTenantAsync();

        var contractTenant =
            await CreateTenantAsync();

        var reservation =
            await CreateReservationAsync(
                roomId,
                reservationTenant.Id
            );

        var payment =
            await CreatePaymentAsync(
                reservation.Id,
                500_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                contractTenant.Id
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _paymentAppService
                        .AllocateReservationDepositToContractAsync(
                            reservation.Id,
                            contract.Id
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationContractMismatch
        );
    }

    // PAY34
    [Fact]
    public async Task
        Should_Not_Allocate_To_Non_Active_Contract()
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
                500_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var contract =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId =
                        roomId,

                    StartDate =
                        _clock.Now.Date,

                    EndDate =
                        _clock.Now.Date
                            .AddMonths(6),

                    MonthlyRent =
                        3_000_000
                }
            );

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

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _paymentAppService
                        .AllocateReservationDepositToContractAsync(
                            reservation.Id,
                            contract.Id
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationDepositCannotBeAllocated
        );
    }

    // PAY35
    [Fact]
    public async Task
        Should_Not_Allocate_Uncompleted_Reservation_Deposit()
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

        await CreatePaymentAsync(
            reservation.Id,
            500_000
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                tenant.Id
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _paymentAppService
                        .AllocateReservationDepositToContractAsync(
                            reservation.Id,
                            contract.Id
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ReservationDepositCannotBeAllocated
        );
    }

    // PAY36
    [Fact]
    public async Task
        Should_Allocate_Only_Remaining_Contract_Deposit()
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

        var reservationPayment =
            await CreatePaymentAsync(
                reservation.Id,
                500_000
            );

        await CompletePaymentAsync(
            reservationPayment.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                tenant.Id
            );

        var contractPayment =
            await CreateContractDepositPaymentAsync(
                contract.Id,
                2_700_000
            );

        await CompletePaymentAsync(
            contractPayment.Id
        );

        var result =
            await _paymentAppService
                .AllocateReservationDepositToContractAsync(
                    reservation.Id,
                    contract.Id
                );

        result.AllocatedAmount.ShouldBe(
            300_000
        );

        result.CoveredDeposit.ShouldBe(
            3_000_000
        );

        result.RemainingDeposit.ShouldBe(
            0
        );

        var settlements =
            await GetReservationAllocationsAsync(
                reservation.Id,
                contract.Id
            );

        settlements.Sum(
            x => x.Amount
        ).ShouldBe(
            300_000
        );
    }

    // PAY29
    [Fact]
    public async Task
        Should_Not_Duplicate_Reservation_Deposit_Allocation()
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
                500_000
            );

        await CompletePaymentAsync(
            payment.Id
        );

        var contract =
            await CreateActiveContractForTenantAsync(
                roomId,
                tenant.Id
            );

        await _paymentAppService
            .AllocateReservationDepositToContractAsync(
                reservation.Id,
                contract.Id
            );

        var second =
            await _paymentAppService
                .AllocateReservationDepositToContractAsync(
                    reservation.Id,
                    contract.Id
                );

        second.AllocatedAmount.ShouldBe(
            0
        );

        second.CoveredDeposit.ShouldBe(
            500_000
        );

        second.RemainingDeposit.ShouldBe(
            2_500_000
        );

        var settlements =
            await GetReservationAllocationsAsync(
                reservation.Id,
                contract.Id
            );

        settlements.Count.ShouldBe(1);

        settlements.Sum(
            x => x.Amount
        ).ShouldBe(
            500_000
        );
    }

    private async Task<ContractDto> CreateActiveContractAsync(
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