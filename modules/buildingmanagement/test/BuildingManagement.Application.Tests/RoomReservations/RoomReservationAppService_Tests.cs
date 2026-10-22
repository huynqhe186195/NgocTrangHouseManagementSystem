using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
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

namespace BuildingManagement.RoomReservations;

public abstract class
    RoomReservationAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IRoomReservationAppService
        _roomReservationAppService;

    private readonly IContractAppService
        _contractAppService;

    private readonly ITenantAppService
        _tenantAppService;

    private readonly IRepository<Building, Guid>
        _buildingRepository;

    private readonly IRepository<Floor, Guid>
        _floorRepository;

    private readonly IRepository<Room, Guid>
        _roomRepository;

    private readonly IRepository<RoomReservation, Guid>
        _roomReservationRepository;

    private readonly IClock _clock;

    protected RoomReservationAppService_Tests()
    {
        _roomReservationAppService =
            GetRequiredService<
                IRoomReservationAppService>();

        _contractAppService =
            GetRequiredService<
                IContractAppService>();

        _tenantAppService =
            GetRequiredService<
                ITenantAppService>();

        _buildingRepository =
            GetRequiredService<
                IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<
                IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<
                IRepository<Room, Guid>>();

        _roomReservationRepository =
            GetRequiredService<
                IRepository<RoomReservation, Guid>>();

        _clock =
            GetRequiredService<IClock>();
    }

    // Business dates are interpreted in Vietnam, not in the UTC calendar day.
    private DateTime GetVietnamToday()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(
            _clock.Now,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")
        ).Date;
    }

    private static DateTime GetLastDayOfMonth(DateTime date)
    {
        return new DateTime(date.Year, date.Month, 1)
            .AddMonths(1)
            .AddDays(-1);
    }

    // Cutoff = the first Sunday on or after day 7 of next month.
    private static DateTime GetNextMonthCutoff(DateTime vietnamToday)
    {
        var seventhOfNextMonth = new DateTime(
            vietnamToday.Year,
            vietnamToday.Month,
            1
        ).AddMonths(1).AddDays(6);

        var daysUntilSunday =
            (7 - (int)seventhOfNextMonth.DayOfWeek) % 7;

        return seventhOfNextMonth.AddDays(daysUntilSunday);
    }

    private static string NewReservationNumber()
    {
        return $"RSV-TEST-{Guid.NewGuid():N}";
    }

    private static string NewContractNumber()
    {
        return $"HD-RSV-{Guid.NewGuid():N}";
    }

    private async Task<Guid> CreateRoomAsync(RoomStatus status = RoomStatus.Available)
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

                await _buildingRepository.InsertAsync(
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

                await _floorRepository.InsertAsync(
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

                await _roomRepository.InsertAsync(
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

    private Task<RoomReservationDto> CreateReservationAsync(Guid roomId,
        Guid tenantId, string? reservationNumber = null, DateTime? expectedMoveInDate = null)
    {
        return _roomReservationAppService.CreateAsync(
            new CreateRoomReservationDto
            {
                ReservationNumber =
                    reservationNumber
                    ?? NewReservationNumber(),

                RoomId = roomId,
                TenantId = tenantId,

                ExpectedMoveInDate =
                    expectedMoveInDate ?? GetVietnamToday().AddDays(1),

                QuotedMonthlyRent =
                    3_000_000,

                RequiredDepositAmount =
                    3_000_000,

                MinimumReservationDepositAmount =
                    500_000,

                Notes =
                    "Reservation test"
            }
        );
    }

    private async Task<ContractDto> CreateActiveContractAsync(Guid roomId, DateTime endDate)
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
                    ContractTenantRole
                        .PrimaryTenant
            }
        );

        return await _contractAppService
            .ActivateAsync(
                contract.Id
            );
    }

    private async Task<RoomReservation> CreateReservedReservationAsync( Guid roomId, Guid tenantId)
    {
        var reservation =
            new RoomReservation(
                Guid.NewGuid(),
                NewReservationNumber(),
                roomId,
                tenantId,
                _clock.Now.Date.AddDays(30),
                3_000_000,
                3_000_000,
                500_000,
                "Reserved reservation"
            );

        reservation.MarkReserved(
            _clock.Now,
            _clock.Now.Date.AddDays(20)
        );

        await WithUnitOfWorkAsync(
            async () =>
            {
                await _roomReservationRepository
                    .InsertAsync(
                        reservation,
                        autoSave: true
                    );
            }
        );

        return reservation;
    }

    // RR01
    [Fact]
    public async Task Should_Create_Pending_Room_Reservation()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var reservationNumber =
            $"rsv-{Guid.NewGuid():N}";

        var result =
            await CreateReservationAsync(
                roomId,
                tenant.Id,
                reservationNumber
            );

        result.Id.ShouldNotBe(
            Guid.Empty
        );

        result.RoomId.ShouldBe(
            roomId
        );

        result.TenantId.ShouldBe(
            tenant.Id
        );

        result.ReservationNumber.ShouldBe(
            reservationNumber
                .ToUpperInvariant()
        );

        result.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );

        result.ReservedAt.ShouldBeNull();

        result.DepositDueDate.ShouldBeNull();

        result.ConvertedContractId.ShouldBeNull();

        result.CancelledAt.ShouldBeNull();

        result.CancellationReason.ShouldBeNull();

        result.CancellationNotes.ShouldBeNull();

        result.RequiredDepositAmount.ShouldBe(3_000_000);

        result.MinimumReservationDepositAmount.ShouldBe(500_000);
    }

    // RR02
    [Fact]
    public async Task Should_Throw_When_Room_Does_Not_Exist()
    {
        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        ),
                        tenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomNotFound
        );
    }

    // RR03
    [Fact]
    public async Task Should_Throw_When_Tenant_Does_Not_Exist()
    {
        var roomId =
            await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        roomId,
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantNotFound
        );
    }

    // RR04
    [Fact]
    public async Task Should_Throw_When_Reservation_Number_Already_Exists()
    {
        var firstRoomId =
            await CreateRoomAsync();

        var secondRoomId =
            await CreateRoomAsync();

        var firstTenant =
            await CreateTenantAsync();

        var secondTenant =
            await CreateTenantAsync();

        var reservationNumber =
            $"RSV-DUP-{Guid.NewGuid():N}";

        await CreateReservationAsync(
            firstRoomId,
            firstTenant.Id,
            reservationNumber
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        secondRoomId,
                        secondTenant.Id,
                        reservationNumber
                            .ToLowerInvariant()
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomReservationNumberAlreadyExists
        );
    }

    // RR05
    [Fact]
    public async Task Should_Throw_When_Quoted_Monthly_Rent_Is_Invalid()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _roomReservationAppService
                        .CreateAsync(
                            new CreateRoomReservationDto
                            {
                                ReservationNumber =
                                    NewReservationNumber(),

                                RoomId =
                                    roomId,

                                TenantId =
                                    tenant.Id,

                                ExpectedMoveInDate =
                                    GetVietnamToday().AddDays(1),

                                QuotedMonthlyRent =
                                    0,

                                RequiredDepositAmount =
                                    3_000_000,

                                MinimumReservationDepositAmount =
                                    500_000
                            }
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidQuotedMonthlyRent
        );
    }

    // RR06
    [Fact]
    public async Task Should_Throw_When_Required_Deposit_Is_Invalid()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _roomReservationAppService
                        .CreateAsync(
                            new CreateRoomReservationDto
                            {
                                ReservationNumber =
                                    NewReservationNumber(),

                                RoomId =
                                    roomId,

                                TenantId =
                                    tenant.Id,

                                ExpectedMoveInDate =
                                    GetVietnamToday().AddDays(1),

                                QuotedMonthlyRent =
                                    3_000_000,

                                RequiredDepositAmount =
                                    0,

                                MinimumReservationDepositAmount =
                                    500_000
                            }
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidRequiredDepositAmount
        );
    }

    // RR07
    [Fact]
    public async Task Should_Throw_When_Move_In_Date_Is_In_The_Past()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        roomId,
                        tenant.Id,
                        expectedMoveInDate:
                            GetVietnamToday().AddDays(-1)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidExpectedMoveInDate
        );
    }

    // RR08
    [Fact]
    public async Task Should_Not_Reserve_Maintenance_Room()
    {
        var roomId =
            await CreateRoomAsync(
                RoomStatus.Maintenance
            );

        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        roomId,
                        tenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomNotAvailableForReservation
        );
    }

    // RR09
    [Fact]
    public async Task Should_Not_Create_Duplicate_Active_Reservation_For_Same_Tenant_And_Room()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        await CreateReservationAsync(
            roomId,
            tenant.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        roomId,
                        tenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomReservationAlreadyExistsForTenant
        );
    }

    // RR10
    [Fact]
    public async Task Should_Allow_Multiple_Pending_Reservations_From_Different_Tenants()
    {
        var roomId =
            await CreateRoomAsync();

        var firstTenant =
            await CreateTenantAsync();

        var secondTenant =
            await CreateTenantAsync();

        var first =
            await CreateReservationAsync(
                roomId,
                firstTenant.Id
            );

        var second =
            await CreateReservationAsync(
                roomId,
                secondTenant.Id
            );

        first.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );

        second.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );

        first.Id.ShouldNotBe(
            second.Id
        );
    }

    // RR11
    [Fact]
    public async Task Should_Allow_Future_Reservation_After_Active_Contract_Ends()
    {
        var roomId =
            await CreateRoomAsync();

        var contractEndDate =
            GetLastDayOfMonth(GetVietnamToday());

        await CreateActiveContractAsync(
            roomId,
            contractEndDate
        );

        var futureTenant =
            await CreateTenantAsync();

        var result =
            await CreateReservationAsync(
                roomId,
                futureTenant.Id,
                expectedMoveInDate:
                    contractEndDate.AddDays(1)
            );

        result.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );

        result.ExpectedMoveInDate.ShouldBe(
            contractEndDate.AddDays(1)
        );
    }

    // RR12
    [Fact]
    public async Task Should_Not_Allow_Reservation_Overlapping_Active_Contract()
    {
        var roomId =
            await CreateRoomAsync();

        var contractEndDate =
            GetLastDayOfMonth(GetVietnamToday());

        await CreateActiveContractAsync(
            roomId,
            contractEndDate
        );

        var futureTenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        roomId,
                        futureTenant.Id,
                        expectedMoveInDate:
                            contractEndDate
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomNotAvailableForReservation
        );
    }

    // RR13
    [Fact]
    public async Task Should_Not_Allow_New_Reservation_When_Room_Already_Has_Reserved_Reservation()
    {
        var roomId =
            await CreateRoomAsync();

        var firstTenant =
            await CreateTenantAsync();

        var secondTenant =
            await CreateTenantAsync();

        await CreateReservedReservationAsync(
            roomId,
            firstTenant.Id
        );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    CreateReservationAsync(
                        roomId,
                        secondTenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomAlreadyReserved
        );
    }

    // RR14
    [Fact]
    public async Task Should_Get_Existing_Room_Reservation()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var created =
            await CreateReservationAsync(
                roomId,
                tenant.Id
            );

        var result =
            await _roomReservationAppService
                .GetAsync(
                    created.Id
                );

        result.Id.ShouldBe(
            created.Id
        );

        result.RoomId.ShouldBe(
            roomId
        );

        result.TenantId.ShouldBe(
            tenant.Id
        );
    }

    // RR15
    [Fact]
    public async Task Should_Throw_When_Room_Reservation_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _roomReservationAppService
                        .GetAsync(
                            Guid.Parse(
                                "11111111-1111-1111-1111-111111111111"
                            )
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomReservationNotFound
        );
    }

    // RR16
    [Fact]
    public async Task Should_Get_Room_Reservation_List()
    {
        var firstRoomId =
            await CreateRoomAsync();

        var secondRoomId =
            await CreateRoomAsync();

        var firstTenant =
            await CreateTenantAsync();

        var secondTenant =
            await CreateTenantAsync();

        var first =
            await CreateReservationAsync(
                firstRoomId,
                firstTenant.Id
            );

        var second =
            await CreateReservationAsync(
                secondRoomId,
                secondTenant.Id
            );

        var result =
            await _roomReservationAppService
                .GetListAsync();

        result.ShouldContain(
            x => x.Id == first.Id
        );

        result.ShouldContain(
            x => x.Id == second.Id
        );
    }

    // RR17
    [Fact]
    public async Task Should_Cancel_Pending_Room_Reservation()
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

        var result =
            await _roomReservationAppService
                .CancelAsync(
                    reservation.Id,
                    new CancelRoomReservationDto
                    {
                        Reason =
                            ReservationCancellationReason
                                .CustomerCancelled,

                        Notes =
                            "Customer changed plan"
                    }
                );

        result.Status.ShouldBe(
            RoomReservationStatus.Cancelled
        );

        result.CancellationReason.ShouldBe(
            ReservationCancellationReason
                .CustomerCancelled
        );

        result.CancellationNotes.ShouldBe(
            "Customer changed plan"
        );

        result.CancelledAt.ShouldNotBeNull();
    }

    // RR18
    [Fact]
    public async Task Should_Not_Cancel_Room_Reservation_Twice()
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
                            .CustomerCancelled
                }
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _roomReservationAppService
                        .CancelAsync(
                            reservation.Id,
                            new CancelRoomReservationDto
                            {
                                Reason =
                                    ReservationCancellationReason
                                        .CustomerCancelled
                            }
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomReservationCannotBeCancelled
        );
    }

    // RR19
    [Fact]
    public async Task Should_Not_Allow_Frontend_To_Set_NoShow()
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
                    _roomReservationAppService
                        .CancelAsync(
                            reservation.Id,
                            new CancelRoomReservationDto
                            {
                                Reason =
                                    ReservationCancellationReason
                                        .NoShow
                            }
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidReservationCancellationReason
        );
    }

    // RR20
    [Fact]
    public async Task Should_Not_Allow_Frontend_To_Set_Superseded_By_Renewal()
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
                    _roomReservationAppService
                        .CancelAsync(
                            reservation.Id,
                            new CancelRoomReservationDto
                            {
                                Reason =
                                    ReservationCancellationReason
                                        .SupersededByRenewal
                            }
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidReservationCancellationReason
        );
    }

    //RR21 — Minimum deposit bằng 0 phải bị chặn
    // RR21
    [Fact]
    public async Task Should_Throw_When_Minimum_Reservation_Deposit_Is_Zero()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _roomReservationAppService.CreateAsync(
                        new CreateRoomReservationDto
                        {
                            ReservationNumber =
                                NewReservationNumber(),

                            RoomId =
                                roomId,

                            TenantId =
                                tenant.Id,

                            ExpectedMoveInDate =
                                GetVietnamToday().AddDays(1),

                            QuotedMonthlyRent =
                                3_000_000,

                            RequiredDepositAmount =
                                3_000_000,

                            MinimumReservationDepositAmount =
                                0
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidMinimumReservationDepositAmount
        );
    }

    //RR22 — Minimum không được lớn hơn tổng tiền cọc
    // RR22
    [Fact]
    public async Task Should_Throw_When_Minimum_Reservation_Deposit_Exceeds_Required_Deposit()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _roomReservationAppService.CreateAsync(
                        new CreateRoomReservationDto
                        {
                            ReservationNumber =
                                NewReservationNumber(),

                            RoomId =
                                roomId,

                            TenantId =
                                tenant.Id,

                            ExpectedMoveInDate =
                                GetVietnamToday().AddDays(1),

                            QuotedMonthlyRent =
                                3_000_000,

                            RequiredDepositAmount =
                                3_000_000,

                            MinimumReservationDepositAmount =
                                3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidMinimumReservationDepositAmount
        );
    }

    //RR23 — 500k hợp lệ và phải được trả đúng ra DTO
    // RR23
    [Fact]
    public async Task Should_Create_Reservation_With_Minimum_Deposit_Amount()
    {
        var roomId =
            await CreateRoomAsync();

        var tenant =
            await CreateTenantAsync();

        var result =
            await _roomReservationAppService.CreateAsync(
                new CreateRoomReservationDto
                {
                    ReservationNumber =
                        NewReservationNumber(),

                    RoomId =
                        roomId,

                    TenantId =
                        tenant.Id,

                    ExpectedMoveInDate = TimeZoneInfo.ConvertTimeFromUtc(
                        _clock.Now,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")
                    ).Date.AddDays(1),

                    QuotedMonthlyRent =
                        3_000_000,

                    RequiredDepositAmount =
                        3_000_000,

                    MinimumReservationDepositAmount =
                        500_000,

                    Notes =
                        "Minimum deposit test"
                }
            );

        result.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );

        result.RequiredDepositAmount.ShouldBe(
            3_000_000
        );

        result.MinimumReservationDepositAmount.ShouldBe(
            500_000
        );
    }


    // RR24 - An empty room accepts move-in today (Vietnam calendar date).
    [Fact]
    public async Task Should_Allow_Available_Room_Move_In_Today()
    {
        var roomId = await CreateRoomAsync();
        var tenant = await CreateTenantAsync();
        var today = GetVietnamToday();

        var result = await CreateReservationAsync(
            roomId,
            tenant.Id,
            expectedMoveInDate: today
        );

        result.ExpectedMoveInDate.ShouldBe(today);
        result.Status.ShouldBe(RoomReservationStatus.PendingPayment);
    }

    // RR25 - The published cutoff is inclusive for an available room.
    [Fact]
    public async Task Should_Allow_Available_Room_Move_In_On_Cutoff()
    {
        var roomId = await CreateRoomAsync();
        var tenant = await CreateTenantAsync();
        var cutoff = GetNextMonthCutoff(GetVietnamToday());

        var result = await CreateReservationAsync(
            roomId,
            tenant.Id,
            expectedMoveInDate: cutoff
        );

        result.ExpectedMoveInDate.ShouldBe(cutoff);
        result.Status.ShouldBe(RoomReservationStatus.PendingPayment);
    }

    // RR26 - Exactly one day after the cutoff is not allowed.
    [Fact]
    public async Task Should_Reject_Available_Room_Move_In_After_Cutoff()
    {
        var roomId = await CreateRoomAsync();
        var tenant = await CreateTenantAsync();
        var afterCutoff = GetNextMonthCutoff(GetVietnamToday()).AddDays(1);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => CreateReservationAsync(
                roomId,
                tenant.Id,
                expectedMoveInDate: afterCutoff
            )
        );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.InvalidExpectedMoveInDate
        );
    }

    // RR27 - A future tenant may choose the cutoff while the current
    // contract ends on the last day of this month.
    [Fact]
    public async Task Should_Allow_Occupied_Room_Move_In_On_Cutoff_After_Contract_End()
    {
        var roomId = await CreateRoomAsync();
        var today = GetVietnamToday();
        await CreateActiveContractAsync(
            roomId,
            GetLastDayOfMonth(today)
        );

        var futureTenant = await CreateTenantAsync();
        var cutoff = GetNextMonthCutoff(today);

        var result = await CreateReservationAsync(
            roomId,
            futureTenant.Id,
            expectedMoveInDate: cutoff
        );

        result.ExpectedMoveInDate.ShouldBe(cutoff);
        result.Status.ShouldBe(RoomReservationStatus.PendingPayment);
        await WithUnitOfWorkAsync(async () =>
        {
            var room = await _roomRepository.GetAsync(roomId);
            room.Status.ShouldBe(RoomStatus.Occupied);
        });
    }

    // RR28 - An existing contract does not extend the cutoff window.
    [Fact]
    public async Task Should_Reject_Occupied_Room_Move_In_After_Cutoff()
    {
        var roomId = await CreateRoomAsync();
        var today = GetVietnamToday();
        await CreateActiveContractAsync(
            roomId,
            GetLastDayOfMonth(today)
        );

        var futureTenant = await CreateTenantAsync();
        var afterCutoff = GetNextMonthCutoff(today).AddDays(1);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => CreateReservationAsync(
                roomId,
                futureTenant.Id,
                expectedMoveInDate: afterCutoff
            )
        );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.InvalidExpectedMoveInDate
        );
    }

    // RR29 - An occupied room without a recorded active contract cannot
    // be assumed to be available for a future reservation.
    [Fact]
    public async Task Should_Reject_Occupied_Room_Without_Active_Contract()
    {
        var roomId = await CreateRoomAsync(RoomStatus.Occupied);
        var tenant = await CreateTenantAsync();

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => CreateReservationAsync(roomId, tenant.Id)
        );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.RoomNotAvailableForReservation
        );
    }

    // RR30 - An open-ended active contract has no assured handover date.
    [Fact]
    public async Task Should_Reject_Occupied_Room_With_Open_Ended_Active_Contract()
    {
        var roomId = await CreateRoomAsync();

        // Arrange an open-ended active contract without changing the
        // shared CreateActiveContractAsync fixture used by older tests.
        var currentContract = await _contractAppService.CreateAsync(
            new CreateContractDto
            {
                ContractNumber = NewContractNumber(),
                RoomId = roomId,
                StartDate = _clock.Now.Date.AddDays(-30),
                EndDate = null,
                MonthlyRent = 3_000_000
            }
        );

        var currentTenant = await CreateTenantAsync();
        await _contractAppService.AddTenantAsync(
            currentContract.Id,
            new AddContractTenantDto
            {
                TenantId = currentTenant.Id,
                Role = ContractTenantRole.PrimaryTenant
            }
        );
        await _contractAppService.ActivateAsync(currentContract.Id);

        var tenant = await CreateTenantAsync();
        var firstDayOfNextMonth = new DateTime(
            GetVietnamToday().Year,
            GetVietnamToday().Month,
            1
        ).AddMonths(1);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => CreateReservationAsync(
                roomId,
                tenant.Id,
                expectedMoveInDate: firstDayOfNextMonth
            )
        );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.RoomNotAvailableForReservation
        );
    }

    // RR31 - An inactive room must not be offered for move-in.
    [Fact]
    public async Task Should_Reject_Reservation_For_Inactive_Room()
    {
        var roomId = await CreateRoomAsync(RoomStatus.Inactive);
        var tenant = await CreateTenantAsync();

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => CreateReservationAsync(roomId, tenant.Id)
        );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.RoomNotAvailableForReservation
        );
    }

    // RR32 - Do not offer a room marked Reserved even when a matching
    // reservation record is not found. This is a fail-closed condition.
    [Fact]
    public async Task Should_Reject_Room_Marked_Reserved()
    {
        var roomId = await CreateRoomAsync(RoomStatus.Reserved);
        var tenant = await CreateTenantAsync();

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => CreateReservationAsync(roomId, tenant.Id)
        );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.RoomAlreadyReserved
        );
    }
}
