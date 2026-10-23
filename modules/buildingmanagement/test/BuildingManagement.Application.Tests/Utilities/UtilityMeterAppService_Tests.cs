using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.Invoices;
using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Validation;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Xunit;

namespace BuildingManagement.Utilities;

public abstract class UtilityMeterAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IUtilityMeterAppService
        _utilityMeterAppService;

    private readonly IRepository<Building, Guid>
        _buildingRepository;

    private readonly IRepository<Floor, Guid>
        _floorRepository;

    private readonly IRepository<Room, Guid>
        _roomRepository;

    private readonly IRoomAppService
    _roomAppService;

    private readonly IMeterReadingAppService
        _meterReadingAppService;

    private readonly IUtilityUsagePreviewAppService _usagePreviewAppService;
    private readonly IUtilityChargePreviewAppService _chargePreviewAppService;
    private readonly IUtilityRateAppService _utilityRateAppService;
    private readonly IMoveOutFinalMeterReadinessAppService _finalMeterReadinessAppService;
    private readonly IRepository<Contract, Guid> _contractRepository;
    private readonly IRepository<MoveOutRequest, Guid> _moveOutRepository;
    private readonly IInvoiceDraftCandidateAppService _invoiceDraftCandidates;
    private readonly IInvoiceDraftStorageAppService _invoiceDraftStorage;

    protected UtilityMeterAppService_Tests()
    {
        _utilityMeterAppService =
            GetRequiredService<IUtilityMeterAppService>();

        _buildingRepository =
            GetRequiredService<IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<IRepository<Room, Guid>>();

        _roomAppService =
    GetRequiredService<IRoomAppService>();

        _meterReadingAppService =
            GetRequiredService<IMeterReadingAppService>();

        _usagePreviewAppService =
            GetRequiredService<IUtilityUsagePreviewAppService>();

        _chargePreviewAppService =
            GetRequiredService<IUtilityChargePreviewAppService>();

        _utilityRateAppService =
            GetRequiredService<IUtilityRateAppService>();

        _finalMeterReadinessAppService =
            GetRequiredService<IMoveOutFinalMeterReadinessAppService>();
        _contractRepository = GetRequiredService<IRepository<Contract, Guid>>();
        _moveOutRepository = GetRequiredService<IRepository<MoveOutRequest, Guid>>();
        _invoiceDraftCandidates = GetRequiredService<IInvoiceDraftCandidateAppService>();
        _invoiceDraftStorage = GetRequiredService<IInvoiceDraftStorageAppService>();
    }

    private async Task<Guid> CreateRoomAsync()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var building = new Building(
                buildingId,
                $"Test Building {Guid.NewGuid():N}",
                "Test Address"
            );

            await _buildingRepository.InsertAsync(
                building,
                autoSave: true
            );

            var floor = new Floor(
                floorId,
                buildingId,
                1,
                "Floor 1"
            );

            await _floorRepository.InsertAsync(
                floor,
                autoSave: true
            );

            var room = new Room(
                roomId,
                floorId,
                "101",
                "Room 101",
                25,
                2,
                RoomStatus.Available
            );

            await _roomRepository.InsertAsync(
                room,
                autoSave: true
            );
        });

        return roomId;
    }

    private static string NewMeterCode()
    {
        return $"METER-{Guid.NewGuid():N}"
            .ToUpperInvariant();
    }

    private async Task<UtilityMeterDto>
        CreateMeterAsync(
            Guid roomId,
            UtilityType utilityType,
            string? meterCode = null,
            decimal initialReading = 0)
    {
        return await _utilityMeterAppService.CreateAsync(
            new CreateUtilityMeterDto
            {
                RoomId = roomId,
                UtilityType = utilityType,
                MeterCode =
                    meterCode ?? NewMeterCode(),
                InitialReading = initialReading,
                InstalledDate =
                    new DateTime(2026, 1, 1)
            }
        );
    }

    // UM1
    [Fact]
    public async Task Should_Create_Electricity_Meter()
    {
        var roomId = await CreateRoomAsync();

        var result = await CreateMeterAsync(
            roomId,
            UtilityType.Electricity,
            initialReading: 1000.125m
        );

        result.Id.ShouldNotBe(Guid.Empty);
        result.RoomId.ShouldBe(roomId);

        result.UtilityType.ShouldBe(
            UtilityType.Electricity
        );

        result.InitialReading.ShouldBe(
            1000.125m
        );

        result.IsActive.ShouldBeTrue();
    }

    // UM2
    [Fact]
    public async Task Should_Create_Water_Meter()
    {
        var roomId = await CreateRoomAsync();

        var result = await CreateMeterAsync(
            roomId,
            UtilityType.Water,
            initialReading: 82.375m
        );

        result.UtilityType.ShouldBe(
            UtilityType.Water
        );

        result.IsActive.ShouldBeTrue();
    }

    // UM3
    [Fact]
    public async Task Should_Throw_When_Room_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService.CreateAsync(
                        new CreateUtilityMeterDto
                        {
                            RoomId = Guid.Parse(
                                "11111111-1111-1111-1111-111111111111"
                            ),
                            UtilityType =
                                UtilityType.Electricity,
                            MeterCode = NewMeterCode(),
                            InitialReading = 0,
                            InstalledDate =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.RoomNotFound
        );
    }

    // UM4
    [Fact]
    public async Task Should_Throw_When_UtilityType_Is_Invalid()
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService.CreateAsync(
                        new CreateUtilityMeterDto
                        {
                            RoomId = roomId,
                            UtilityType =
                                (UtilityType)999,
                            MeterCode = NewMeterCode(),
                            InitialReading = 0,
                            InstalledDate =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidUtilityType
        );
    }

    // UM5
    [Fact]
    public async Task Should_Throw_When_MeterCode_Is_Blank()
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService.CreateAsync(
                        new CreateUtilityMeterDto
                        {
                            RoomId = roomId,
                            UtilityType =
                                UtilityType.Electricity,
                            MeterCode = "   ",
                            InitialReading = 0,
                            InstalledDate =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidMeterCode
        );
    }

    // UM6
    [Fact]
    public async Task Should_Normalize_MeterCode()
    {
        var roomId = await CreateRoomAsync();

        var result =
            await _utilityMeterAppService.CreateAsync(
                new CreateUtilityMeterDto
                {
                    RoomId = roomId,
                    UtilityType =
                        UtilityType.Electricity,
                    MeterCode =
                        "  elec-test-001  ",
                    InitialReading = 0,
                    InstalledDate =
                        new DateTime(2026, 1, 1)
                }
            );

        result.MeterCode.ShouldBe(
            "ELEC-TEST-001"
        );
    }

    // UM7
    [Fact]
    public async Task Should_Throw_When_InitialReading_Is_Negative()
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateMeterAsync(
                        roomId,
                        UtilityType.Electricity,
                        initialReading: -1
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidInitialMeterReading
        );
    }

    // UM8
    [Fact]
    public async Task Should_Throw_When_MeterCode_Already_Exists()
    {
        var roomA = await CreateRoomAsync();
        var roomB = await CreateRoomAsync();

        var code = NewMeterCode();

        await CreateMeterAsync(
            roomA,
            UtilityType.Electricity,
            code
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateMeterAsync(
                        roomB,
                        UtilityType.Electricity,
                        code
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterCodeAlreadyExists
        );
    }

    // UM9
    [Fact]
    public async Task Should_Treat_MeterCode_As_Case_Insensitive()
    {
        var roomA = await CreateRoomAsync();
        var roomB = await CreateRoomAsync();

        var code =
            $"ELEC-{Guid.NewGuid():N}";

        await CreateMeterAsync(
            roomA,
            UtilityType.Electricity,
            code.ToUpperInvariant()
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateMeterAsync(
                        roomB,
                        UtilityType.Electricity,
                        code.ToLowerInvariant()
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterCodeAlreadyExists
        );
    }

    // UM10
    [Fact]
    public async Task Should_Not_Allow_Two_Active_Meters_Of_Same_Type_In_Room()
    {
        var roomId = await CreateRoomAsync();

        await CreateMeterAsync(
            roomId,
            UtilityType.Electricity
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateMeterAsync(
                        roomId,
                        UtilityType.Electricity
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ActiveUtilityMeterAlreadyExists
        );
    }

    // UM11
    [Fact]
    public async Task Should_Allow_Electricity_And_Water_In_Same_Room()
    {
        var roomId = await CreateRoomAsync();

        var electricity =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        var water =
            await CreateMeterAsync(
                roomId,
                UtilityType.Water
            );

        electricity.RoomId.ShouldBe(roomId);
        water.RoomId.ShouldBe(roomId);

        electricity.UtilityType.ShouldBe(
            UtilityType.Electricity
        );

        water.UtilityType.ShouldBe(
            UtilityType.Water
        );
    }

    // UM12
    [Fact]
    public async Task Should_Allow_Same_UtilityType_In_Different_Rooms()
    {
        var roomA = await CreateRoomAsync();
        var roomB = await CreateRoomAsync();

        var meterA =
            await CreateMeterAsync(
                roomA,
                UtilityType.Electricity
            );

        var meterB =
            await CreateMeterAsync(
                roomB,
                UtilityType.Electricity
            );

        meterA.RoomId.ShouldBe(roomA);
        meterB.RoomId.ShouldBe(roomB);
    }

    // UM13
    [Fact]
    public async Task Should_Update_UtilityMeter()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        var newCode = NewMeterCode();

        var updated =
            await _utilityMeterAppService.UpdateAsync(
                created.Id,
                new UpdateUtilityMeterDto
                {
                    MeterCode = newCode,
                    InitialReading = 250.500m,
                    InstalledDate =
                        new DateTime(2026, 2, 1),
                    Notes = "Updated meter"
                }
            );

        updated.Id.ShouldBe(created.Id);
        updated.RoomId.ShouldBe(roomId);

        updated.UtilityType.ShouldBe(
            UtilityType.Electricity
        );

        updated.MeterCode.ShouldBe(newCode);
        updated.InitialReading.ShouldBe(250.500m);
        updated.Notes.ShouldBe("Updated meter");
    }

    // UM14
    [Fact]
    public async Task Should_Update_And_Keep_Own_MeterCode()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        var updated =
            await _utilityMeterAppService.UpdateAsync(
                created.Id,
                new UpdateUtilityMeterDto
                {
                    MeterCode =
                        created.MeterCode,
                    InitialReading = 10,
                    InstalledDate =
                        created.InstalledDate,
                    Notes = "Keep own code"
                }
            );

        updated.MeterCode.ShouldBe(
            created.MeterCode
        );
    }

    // UM15
    [Fact]
    public async Task Should_Throw_When_Update_Uses_Another_MeterCode()
    {
        var roomA = await CreateRoomAsync();
        var roomB = await CreateRoomAsync();

        var first =
            await CreateMeterAsync(
                roomA,
                UtilityType.Electricity
            );

        var second =
            await CreateMeterAsync(
                roomB,
                UtilityType.Electricity
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService.UpdateAsync(
                        second.Id,
                        new UpdateUtilityMeterDto
                        {
                            MeterCode =
                                first.MeterCode,
                            InitialReading = 0,
                            InstalledDate =
                                second.InstalledDate
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterCodeAlreadyExists
        );
    }

    // UM16
    [Fact]
    public async Task Should_Deactivate_UtilityMeter()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        var result =
            await _utilityMeterAppService
                .DeactivateAsync(created.Id);

        result.IsActive.ShouldBeFalse();
    }

    // UM17
    [Fact]
    public async Task Should_Throw_When_Deactivating_Inactive_Meter()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        await _utilityMeterAppService
            .DeactivateAsync(created.Id);

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService
                        .DeactivateAsync(created.Id)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityMeterAlreadyInactive
        );
    }

    // UM18
    [Fact]
    public async Task Should_Allow_New_Meter_After_Old_Meter_Is_Deactivated()
    {
        var roomId = await CreateRoomAsync();

        var oldMeter =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        await _utilityMeterAppService
            .DeactivateAsync(oldMeter.Id);

        var newMeter =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        newMeter.IsActive.ShouldBeTrue();

        newMeter.Id.ShouldNotBe(
            oldMeter.Id
        );
    }

    // UM19
    [Fact]
    public async Task Should_Get_Existing_UtilityMeter()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Water
            );

        var result =
            await _utilityMeterAppService.GetAsync(
                created.Id
            );

        result.Id.ShouldBe(created.Id);
        result.RoomId.ShouldBe(roomId);
    }

    // UM20
    [Fact]
    public async Task Should_Throw_When_UtilityMeter_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService.GetAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityMeterNotFound
        );
    }

    // UM21
    [Fact]
    public async Task Should_Get_UtilityMeter_List()
    {
        var roomId = await CreateRoomAsync();

        var electricity =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        var water =
            await CreateMeterAsync(
                roomId,
                UtilityType.Water
            );

        var result =
            await _utilityMeterAppService
                .GetListAsync();

        result.ShouldContain(
            x => x.Id == electricity.Id
        );

        result.ShouldContain(
            x => x.Id == water.Id
        );
    }

    // UM22
    [Fact]
    public async Task Should_Soft_Delete_UtilityMeter()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        await _utilityMeterAppService.DeleteAsync(
            created.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService.GetAsync(
                        created.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityMeterNotFound
        );
    }

    // UM23
    [Fact]
    public async Task Should_Not_Recreate_Deleted_Meter_With_Same_Code()
    {
        var roomId = await CreateRoomAsync();

        var code = NewMeterCode();

        var created =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity,
                code
            );

        await _utilityMeterAppService.DeleteAsync(
            created.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateMeterAsync(
                        roomId,
                        UtilityType.Electricity,
                        code
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterCodeAlreadyExists
        );
    }
    //UM24 — Active meter chặn Room delete
    [Fact]
    public async Task Should_Not_Delete_Room_With_Active_UtilityMeter()
    {
        var roomId =
            await CreateRoomAsync();

        await CreateMeterAsync(
            roomId,
            UtilityType.Electricity
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _roomAppService.DeleteAsync(
                        roomId
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomHasActiveUtilityMeter
        );

        var room =
            await _roomAppService.GetAsync(
                roomId
            );

        room.Id.ShouldBe(roomId);
    }
    //UM25 — Inactive meter history khiến Room chuyển Inactive
    [Fact]
    public async Task Should_Set_Room_Inactive_When_It_Has_UtilityMeter_History()
    {
        var roomId =
            await CreateRoomAsync();

        var meter =
            await CreateMeterAsync(
                roomId,
                UtilityType.Electricity
            );

        await _utilityMeterAppService
            .DeactivateAsync(meter.Id);

        await _roomAppService.DeleteAsync(
            roomId
        );

        var room =
            await _roomAppService.GetAsync(
                roomId
            );

        room.Status.ShouldBe(
            RoomStatus.Inactive
        );
    }

    private async Task AddHistoricalReadingAsync(Guid meterId)
    {
        await _meterReadingAppService.CreateAsync(
            new CreateMeterReadingDto
            {
                UtilityMeterId = meterId,
                BillingYear = 2026,
                BillingMonth = 10,
                CurrentReading = 1200m,
                ReadingDate = new DateTime(2026, 10, 31)
            }
        );
    }

    // Phase 2G.2A: the meter's opening reference must stay immutable
    // once a reading has been recorded.
    [Fact]
    public async Task UM26_Should_Reject_InitialReading_Change_When_History_Exists()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(
            roomId, UtilityType.Electricity, initialReading: 1000m);
        await AddHistoricalReadingAsync(meter.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.UpdateAsync(meter.Id, new UpdateUtilityMeterDto
            {
                MeterCode = meter.MeterCode,
                InitialReading = 900m,
                InstalledDate = meter.InstalledDate
            }));

        error.Code.ShouldBe(
            UtilityDataIntegrityErrorCodes.MeterIdentityLockedByReadings);
        (await _utilityMeterAppService.GetAsync(meter.Id))
            .InitialReading.ShouldBe(1000m);
    }

    [Fact]
    public async Task UM27_Should_Reject_InstalledDate_Change_When_History_Exists()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(
            roomId, UtilityType.Electricity, initialReading: 1000m);
        await AddHistoricalReadingAsync(meter.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.UpdateAsync(meter.Id, new UpdateUtilityMeterDto
            {
                MeterCode = meter.MeterCode,
                InitialReading = meter.InitialReading,
                InstalledDate = meter.InstalledDate.AddDays(1)
            }));

        error.Code.ShouldBe(
            UtilityDataIntegrityErrorCodes.MeterIdentityLockedByReadings);
        (await _utilityMeterAppService.GetAsync(meter.Id))
            .InstalledDate.ShouldBe(meter.InstalledDate);
    }

    [Fact]
    public async Task UM28_Should_Reject_MeterCode_Change_When_History_Exists()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(
            roomId, UtilityType.Electricity, initialReading: 1000m);
        await AddHistoricalReadingAsync(meter.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.UpdateAsync(meter.Id, new UpdateUtilityMeterDto
            {
                MeterCode = NewMeterCode(),
                InitialReading = meter.InitialReading,
                InstalledDate = meter.InstalledDate
            }));

        error.Code.ShouldBe(
            UtilityDataIntegrityErrorCodes.MeterIdentityLockedByReadings);
        (await _utilityMeterAppService.GetAsync(meter.Id))
            .MeterCode.ShouldBe(meter.MeterCode);
    }

    [Fact]
    public async Task UM29_Should_Allow_Notes_Update_Without_Changing_History()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(
            roomId, UtilityType.Electricity, initialReading: 1000m);
        await AddHistoricalReadingAsync(meter.Id);

        var updated = await _utilityMeterAppService.UpdateAsync(
            meter.Id, new UpdateUtilityMeterDto
            {
                MeterCode = meter.MeterCode,
                InitialReading = meter.InitialReading,
                InstalledDate = meter.InstalledDate,
                Notes = "Maintenance observation"
            });

        updated.Notes.ShouldBe("Maintenance observation");
        updated.InitialReading.ShouldBe(meter.InitialReading);
        updated.MeterCode.ShouldBe(meter.MeterCode);
    }

    // Phase 2G.2B.1 - physical meter replacement and immutable closing index.
    private static ReplaceUtilityMeterDto NewReplacement(
        decimal final = 1050m, decimal opening = 12m,
        DateTime? date = null) => new()
    {
        NewMeterCode = NewMeterCode(),
        OldFinalReading = final,
        NewInitialReading = opening,
        ReplacementDate = date ?? new DateTime(2026, 11, 1),
        Reason = "Verified physical meter replacement"
    };

    [Fact]
    public async Task RM01_Replace_Without_Monthly_Readings_Preserves_Closing_Usage()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        var record = await _utilityMeterAppService.ReplaceAsync(old.Id,
            NewReplacement(final: 1045.500m, opening: 8.125m));

        record.Id.ShouldNotBe(Guid.Empty);
        record.OldMeterId.ShouldBe(old.Id);
        record.RoomId.ShouldBe(roomId);
        record.UtilityType.ShouldBe(UtilityType.Electricity);
        record.OldPreviousReading.ShouldBe(1000m);
        record.OldFinalReading.ShouldBe(1045.500m);
        record.ClosingUsage.ShouldBe(45.500m);
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeFalse();
        var newest = await _utilityMeterAppService.GetAsync(record.NewMeterId);
        newest.IsActive.ShouldBeTrue();
        newest.RoomId.ShouldBe(roomId);
        newest.UtilityType.ShouldBe(old.UtilityType);
        newest.InitialReading.ShouldBe(8.125m);
        newest.InstalledDate.ShouldBe(new DateTime(2026, 11, 1));
        (await _utilityMeterAppService.GetReplacementAsync(record.Id))
            .NewMeterId.ShouldBe(record.NewMeterId);
        (await _utilityMeterAppService.GetReplacementsByRoomAsync(roomId))
            .ShouldContain(x => x.Id == record.Id);
    }

    [Fact]
    public async Task RM02_Replacement_Uses_Latest_Monthly_Reading_Not_Opening()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Water,
            initialReading: 1000m);
        await AddHistoricalReadingAsync(old.Id); // 1200 on 2026-10-31
        var record = await _utilityMeterAppService.ReplaceAsync(old.Id,
            NewReplacement(final: 1212m, opening: 20m));

        record.OldPreviousReading.ShouldBe(1200m);
        record.OldFinalReading.ShouldBe(1212m);
        record.ClosingUsage.ShouldBe(12m);
    }

    [Fact]
    public async Task RM03_Reject_Closing_Reading_Below_Latest()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity, initialReading: 1000m);
        await AddHistoricalReadingAsync(old.Id);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.ReplaceAsync(old.Id, NewReplacement(final: 1199m)));
        error.Code.ShouldBe(MeterReplacementErrorCodes.InvalidClosingReading);
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeTrue();
        (await _utilityMeterAppService.GetReplacementsByRoomAsync(roomId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RM04_Reject_Date_Before_Last_Monthly_Reading()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity, initialReading: 1000m);
        await AddHistoricalReadingAsync(old.Id);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.ReplaceAsync(old.Id,
                NewReplacement(final: 1210m, date: new DateTime(2026, 10, 30))));
        error.Code.ShouldBe(MeterReplacementErrorCodes.InvalidReplacementDate);
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task RM05_Reject_Blank_Reason()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Electricity,
            initialReading: 1000m);
        var dto = NewReplacement();
        dto.Reason = "   ";

        // [Required] on ReplaceUtilityMeterDto.Reason is evaluated by the ABP
        // ValidationInterceptor before ReplaceAsync can throw BusinessException.
        var error = await Assert.ThrowsAsync<AbpValidationException>(() =>
            _utilityMeterAppService.ReplaceAsync(old.Id, dto));
        Assert.Contains(error.ValidationErrors, validationError =>
            validationError.MemberNames.Any(memberName =>
                memberName == nameof(ReplaceUtilityMeterDto.Reason)));

        // Invalid input must not deactivate the original meter.
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task RM06_Reject_Duplicate_New_MeterCode()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Electricity,
            initialReading: 1000m);
        var other = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Electricity);
        var dto = NewReplacement();
        dto.NewMeterCode = other.MeterCode;
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.ReplaceAsync(old.Id, dto));
        error.Code.ShouldBe(BuildingManagementErrorCodes.MeterCodeAlreadyExists);
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task RM07_Replacement_Locks_Old_And_New_Opening_Readings()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Electricity,
            initialReading: 1000m);
        var record = await _utilityMeterAppService.ReplaceAsync(old.Id, NewReplacement());
        var currentNew = await _utilityMeterAppService.GetAsync(record.NewMeterId);
        foreach (var meter in new[] { old, currentNew })
        {
            var error = await Assert.ThrowsAsync<BusinessException>(() =>
                _utilityMeterAppService.UpdateAsync(meter.Id, new UpdateUtilityMeterDto
                {
                    MeterCode = meter.MeterCode,
                    InitialReading = meter.InitialReading + 1m,
                    InstalledDate = meter.InstalledDate
                }));
            error.Code.ShouldBe(MeterReplacementErrorCodes.MeterIdentityLockedByReplacement);
        }
        (await _utilityMeterAppService.UpdateAsync(record.NewMeterId, new UpdateUtilityMeterDto
        {
            MeterCode = currentNew.MeterCode,
            InitialReading = currentNew.InitialReading,
            InstalledDate = currentNew.InstalledDate,
            Notes = "Maintenance note"
        })).Notes.ShouldBe("Maintenance note");
    }

    [Fact]
    public async Task RM08_Cannot_Delete_Either_Replaced_Meter()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Water,
            initialReading: 1000m);
        var record = await _utilityMeterAppService.ReplaceAsync(old.Id, NewReplacement());
        foreach (var id in new[] { old.Id, record.NewMeterId })
        {
            var error = await Assert.ThrowsAsync<BusinessException>(() =>
                _utilityMeterAppService.DeleteAsync(id));
            error.Code.ShouldBe(MeterReplacementErrorCodes.MeterReferencedByReplacement);
        }
    }

    [Fact]
    public async Task RM09_Reject_Second_Replacement_Of_Inactive_Old_Meter()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Water,
            initialReading: 1000m);
        await _utilityMeterAppService.ReplaceAsync(old.Id, NewReplacement());
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.ReplaceAsync(old.Id, NewReplacement()));
        error.Code.ShouldBe(MeterReplacementErrorCodes.OldMeterMustBeActive);
    }

    [Fact]
    public async Task RM10_Replacement_Freezes_Old_Latest_Monthly_Reading()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Electricity,
            initialReading: 1000m);
        var reading = await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = old.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1200m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await _utilityMeterAppService.ReplaceAsync(old.Id, NewReplacement(final: 1210m));
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _meterReadingAppService.UpdateAsync(reading.Id, new UpdateMeterReadingDto
            {
                CurrentReading = 1250m, ReadingDate = reading.ReadingDate
            }));
        error.Code.ShouldBe(MeterReplacementErrorCodes.ReadingLockedByReplacement);
        (await _meterReadingAppService.GetAsync(reading.Id))
            .CurrentReading.ShouldBe(1200m);
    }

    [Fact]
    public async Task RM11_New_Meter_Uses_Its_Own_Opening_For_First_Reading()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Water,
            initialReading: 1000m);
        var record = await _utilityMeterAppService.ReplaceAsync(old.Id,
            NewReplacement(final: 1050m, opening: 20m));
        var reading = await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = record.NewMeterId,
            BillingYear = 2026, BillingMonth = 11,
            CurrentReading = 30m, ReadingDate = new DateTime(2026, 11, 30)
        });
        reading.PreviousReading.ShouldBe(20m);
        reading.Usage.ShouldBe(10m);
    }

    [Fact]
    public async Task RM12_New_Meter_Can_Later_Be_Replaced_Again()
    {
        var roomId = await CreateRoomAsync();
        var meterA = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        var ab = await _utilityMeterAppService.ReplaceAsync(meterA.Id,
            NewReplacement(final: 1030m, opening: 10m,
                date: new DateTime(2026, 10, 10)));
        var bc = await _utilityMeterAppService.ReplaceAsync(ab.NewMeterId,
            NewReplacement(final: 25m, opening: 0m,
                date: new DateTime(2026, 10, 20)));

        bc.OldMeterId.ShouldBe(ab.NewMeterId);
        bc.OldPreviousReading.ShouldBe(10m);
        bc.ClosingUsage.ShouldBe(15m);
        (await _utilityMeterAppService.GetReplacementsByRoomAsync(roomId))
            .Count.ShouldBe(2);
        (await _utilityMeterAppService.GetAsync(ab.NewMeterId)).IsActive.ShouldBeFalse();
        (await _utilityMeterAppService.GetAsync(bc.NewMeterId)).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task RM13_Reject_Negative_Opening_Of_New_Meter()
    {
        var old = await CreateMeterAsync(await CreateRoomAsync(), UtilityType.Water,
            initialReading: 1000m);
        var dto = NewReplacement();
        dto.NewInitialReading = -1m;
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.ReplaceAsync(old.Id, dto));
        error.Code.ShouldBe(MeterReplacementErrorCodes.InvalidNewInitialReading);
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeTrue();
    }


    [Fact]
    public async Task RM14_Reject_Replacement_When_SoftDeleted_Reading_History_Exists()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(
            roomId, UtilityType.Electricity, initialReading: 1000m);
        var reading = await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = old.Id,
            BillingYear = 2026,
            BillingMonth = 10,
            CurrentReading = 1200m,
            ReadingDate = new DateTime(2026, 10, 31)
        });

        // A deleted reading is still financial history. Replacement must not
        // silently fall back to the physical meter's opening index.
        var readings = GetRequiredService<IRepository<MeterReading, Guid>>();
        await readings.DeleteAsync(reading.Id, autoSave: true);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _utilityMeterAppService.ReplaceAsync(
                old.Id, NewReplacement(final: 1210m)));

        error.Code.ShouldBe(
            MeterReplacementErrorCodes.SoftDeletedHistoryRequiresReview);
        (await _utilityMeterAppService.GetAsync(old.Id)).IsActive.ShouldBeTrue();
        (await _utilityMeterAppService.GetReplacementsByRoomAsync(roomId))
            .ShouldBeEmpty();
    }


    // Phase 2G.2B.2 - read-only preview built from actual repositories.
    [Fact]
    public async Task UP01_Preview_Combines_Recorded_Old_Closing_And_New_Usage()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = old.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1050m, ReadingDate = new DateTime(2026, 10, 19)
        });
        var replaced = await _utilityMeterAppService.ReplaceAsync(
            old.Id, NewReplacement(final: 1100m, opening: 15m,
                date: new DateTime(2026, 10, 20)));
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = replaced.NewMeterId, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 20m, ReadingDate = new DateTime(2026, 10, 31)
        });

        var preview = await _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RoomId.ShouldBe(roomId);
        preview.BillingMonth.ShouldBe(10);
        preview.RecordedUsage.ShouldBe(105m);
        preview.Segments.Count.ShouldBe(3);
        preview.Segments.Count(x => x.SourceType == "ReplacementClosing")
            .ShouldBe(1);
        preview.Segments.Single(x => x.SourceType == "ReplacementClosing")
            .SourceId.ShouldBe(replaced.Id);
    }

    [Fact]
    public async Task UP02_Preview_Handles_Multiple_Replacements_In_Same_Billing_Month()
    {
        var roomId = await CreateRoomAsync();
        var first = await CreateMeterAsync(roomId, UtilityType.Water,
            initialReading: 1000m);
        var ab = await _utilityMeterAppService.ReplaceAsync(first.Id,
            NewReplacement(final: 1030m, opening: 10m,
                date: new DateTime(2026, 10, 10)));
        var bc = await _utilityMeterAppService.ReplaceAsync(ab.NewMeterId,
            NewReplacement(final: 25m, opening: 2m,
                date: new DateTime(2026, 10, 20)));
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = bc.NewMeterId, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 7m, ReadingDate = new DateTime(2026, 10, 31)
        });

        var preview = await _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);

        preview.RecordedUsage.ShouldBe(50m);
        preview.Segments.Count.ShouldBe(3);
        preview.Segments.Count(x => x.SourceType == "ReplacementClosing")
            .ShouldBe(2);
    }

    [Fact]
    public async Task UP03_Preview_Does_Not_Carry_Replacement_Into_Next_Billing_Month()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 100m);
        var replacement = await _utilityMeterAppService.ReplaceAsync(
            old.Id, NewReplacement(final: 105m, opening: 0m,
                date: new DateTime(2026, 10, 20)));
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = replacement.NewMeterId,
            BillingYear = 2026, BillingMonth = 11,
            CurrentReading = 12m, ReadingDate = new DateTime(2026, 11, 30)
        });

        var october = await _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);
        var november = await _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 11);

        october.RecordedUsage.ShouldBe(5m);
        november.RecordedUsage.ShouldBe(12m);
        october.Segments.Single().SourceType.ShouldBe("ReplacementClosing");
        november.Segments.Single().SourceType.ShouldBe("MonthlyReading");
    }

    [Fact]
    public async Task UP04_Preview_Rejects_SoftDeleted_Monthly_Reading()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        var reading = await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id,
            BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1010m, ReadingDate = new DateTime(2026, 10, 31)
        });
        var repository = GetRequiredService<IRepository<MeterReading, Guid>>();
        await repository.DeleteAsync(reading.Id, autoSave: true);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
                roomId, UtilityType.Electricity, 2026, 10));
        error.Code.ShouldBe(UtilityUsagePreviewErrorCodes.HistoryRequiresReview);
    }

    [Fact]
    public async Task UP05_Preview_Rejects_Unknown_Room()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
                Guid.NewGuid(), UtilityType.Water, 2026, 10));
        error.Code.ShouldBe(BuildingManagementErrorCodes.RoomNotFound);
    }

    [Fact]
    public async Task UP06_Preview_With_No_Readings_Has_Empty_Segments()
    {
        var roomId = await CreateRoomAsync();
        await CreateMeterAsync(roomId, UtilityType.Water, initialReading: 100m);
        var preview = await _usagePreviewAppService.GetMonthlyUsagePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);
        preview.RecordedUsage.ShouldBe(0m);
        preview.Segments.ShouldBeEmpty();
        // RecordedUsage=0 here does not certify the meter period is complete.
    }


    // Phase 2G.2C.1 - provisional monetary preview, no invoices emitted.
    private async Task<UtilityRateDto> CreateRateForRoomAsync(
        Guid roomId, UtilityType type, decimal price,
        DateTime from, DateTime? to = null)
    {
        var room = await _roomRepository.GetAsync(roomId);
        var floor = await _floorRepository.GetAsync(room.FloorId);
        return await _utilityRateAppService.CreateAsync(new CreateUtilityRateDto
        {
            BuildingId = floor.BuildingId,
            UtilityType = type,
            UnitPrice = price,
            EffectiveFrom = from,
            EffectiveTo = to
        });
    }

    [Fact]
    public async Task PR01_Single_Rate_Whole_Month_Provides_Provisional_Charge()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        // A previous-month anchor proves October is not silently charging
        // consumption accumulated across several missing months.
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id,
            BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 1000m, ReadingDate = new DateTime(2026, 9, 30)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id,
            BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1050m, ReadingDate = new DateTime(2026, 10, 31)
        });
        var rate = await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RateStatus.ShouldBe("SingleRateWholeMonth");
        preview.UtilityRateId.ShouldBe(rate.Id);
        preview.RecordedUsage.ShouldBe(50m);
        preview.UnitPrice.ShouldBe(3500m);
        preview.ProvisionalAmount.ShouldBe(175000m);
        preview.IsFinalInvoice.ShouldBeFalse();
        preview.UsagePreview.Segments.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PR02_Rate_Changes_During_Month_Does_Not_Invent_Daily_Usage()
    {
        var roomId = await CreateRoomAsync();
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3000m, new DateTime(2026, 10, 1), new DateTime(2026, 10, 15));
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 16), null);

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RateStatus.ShouldBe("MultipleRatesInMonth");
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UnitPrice.ShouldBeNull();
        preview.IsFinalInvoice.ShouldBeFalse();
    }

    [Fact]
    public async Task PR03_Missing_Rate_Leaves_Charge_Unavailable()
    {
        var roomId = await CreateRoomAsync();
        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);

        preview.RateStatus.ShouldBe("NoRate");
        preview.ProvisionalAmount.ShouldBeNull();
        preview.RecordedUsage.ShouldBe(0m);
    }

    [Fact]
    public async Task PR04_Partial_Rate_Coverage_Does_Not_Price_Whole_Month()
    {
        var roomId = await CreateRoomAsync();
        await CreateRateForRoomAsync(roomId, UtilityType.Water,
            15000m, new DateTime(2026, 10, 12), null);

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);

        preview.RateStatus.ShouldBe("IncompleteRateCoverage");
        preview.ProvisionalAmount.ShouldBeNull();
    }

    [Fact]
    public async Task PR05_SoftDeleted_Rate_History_Requires_Review()
    {
        var roomId = await CreateRoomAsync();
        var rate = await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1), null);
        await _utilityRateAppService.DeleteAsync(rate.Id);

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RateStatus.ShouldBe("DeletedRateHistoryRequiresReview");
        preview.ProvisionalAmount.ShouldBeNull();
    }

    [Fact]
    public async Task PR06_No_Meter_Is_Not_Assumed_To_Mean_Zero_Bill()
    {
        var roomId = await CreateRoomAsync();
        await CreateRateForRoomAsync(roomId, UtilityType.Water,
            15000m, new DateTime(2026, 1, 1), null);

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);

        preview.RateStatus.ShouldBe("SingleRateWholeMonth");
        preview.RecordedUsage.ShouldBe(0m);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.Segments.ShouldBeEmpty();
        preview.UsagePreview.IsSafeForProvisionalPricing.ShouldBeFalse();
        preview.UsagePreview.CoverageIssues.ShouldContain("NoMeterForRoomAndUtility");
        preview.IsFinalInvoice.ShouldBeFalse();
    }


    [Fact]
    public async Task PR07_Single_Month_Rate_Prices_Usage_Across_Replacement()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = old.Id, BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 1000m, ReadingDate = new DateTime(2026, 9, 30)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = old.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1050m, ReadingDate = new DateTime(2026, 10, 19)
        });
        var replacement = await _utilityMeterAppService.ReplaceAsync(
            old.Id, NewReplacement(final: 1100m, opening: 15m,
                date: new DateTime(2026, 10, 20)));
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = replacement.NewMeterId, BillingYear = 2026,
            BillingMonth = 10, CurrentReading = 20m,
            ReadingDate = new DateTime(2026, 10, 31)
        });
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RateStatus.ShouldBe("SingleRateWholeMonth");
        preview.RecordedUsage.ShouldBe(105m);
        preview.ProvisionalAmount.ShouldBe(367500m);
        preview.UsagePreview.Segments.Count.ShouldBe(3);
        preview.IsFinalInvoice.ShouldBeFalse();
    }


    [Fact]
    public async Task PR08_Missing_Previous_Month_Reading_Does_Not_Quote_MultiMonth_Usage()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        // No September reading: 150 units might cover Jan-Oct, not October.
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1150m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity, 3500m,
            new DateTime(2026, 10, 1));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RecordedUsage.ShouldBe(150m);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.CoverageIssues.ShouldContain("MissingPreviousMonthReading");
        preview.IsFinalInvoice.ShouldBeFalse();
    }

    [Fact]
    public async Task PR09_Reading_Date_Outside_Month_Requires_Cutoff_Policy()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(roomId, UtilityType.Water,
            initialReading: 0m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 10m, ReadingDate = new DateTime(2026, 11, 1)
        });
        await CreateRateForRoomAsync(roomId, UtilityType.Water, 15000m,
            new DateTime(2026, 10, 1));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);

        preview.RecordedUsage.ShouldBe(10m);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.CoverageIssues.ShouldContain("ReadingDateOutsideBillingMonth");
    }

    [Fact]
    public async Task PR10_Active_Meter_Without_Monthly_Reading_Must_Not_Quote_Zero()
    {
        var roomId = await CreateRoomAsync();
        await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity, 3500m,
            new DateTime(2026, 10, 1));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RecordedUsage.ShouldBe(0m);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.CoverageIssues.ShouldContain("ActiveMeterMissingMonthlyReading");
    }


    [Fact]
    public async Task PR11_Inactive_Meter_Now_Was_Active_In_Older_Month_Missing_Reading()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = old.Id, BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 1000m, ReadingDate = new DateTime(2026, 9, 30)
        });
        // Replaced in November; October remains unrecorded.
        // The historical meter must still be checked in October even though
        // its IsActive flag is now false.
        await _utilityMeterAppService.ReplaceAsync(
            old.Id, NewReplacement(final: 1020m, opening: 0m,
                date: new DateTime(2026, 11, 10)));
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Electricity, 2026, 10);

        preview.RecordedUsage.ShouldBe(0m);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.CoverageIssues
            .ShouldContain("InactiveMeterPeriodUnverified");
    }

    [Fact]
    public async Task PR12_Replaced_Before_Month_Does_Not_Require_Old_Meter_Reading()
    {
        var roomId = await CreateRoomAsync();
        var old = await CreateMeterAsync(roomId, UtilityType.Water,
            initialReading: 100m);
        var replacement = await _utilityMeterAppService.ReplaceAsync(
            old.Id, NewReplacement(final: 110m, opening: 0m,
                date: new DateTime(2026, 9, 15)));
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = replacement.NewMeterId,
            BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 5m, ReadingDate = new DateTime(2026, 9, 30)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = replacement.NewMeterId,
            BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 15m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await CreateRateForRoomAsync(roomId, UtilityType.Water,
            15000m, new DateTime(2026, 10, 1));

        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            roomId, UtilityType.Water, 2026, 10);

        preview.RecordedUsage.ShouldBe(10m);
        preview.ProvisionalAmount.ShouldBe(150000m);
        preview.UsagePreview.CoverageIssues.ShouldBeEmpty();
    }


    private async Task<Guid> CreateMoveOutEvidenceFixtureAsync(
        Guid roomId, bool approved)
    {
        var contractId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            // Direct fixture: readiness tests do not exercise the separate
            // contract activation or move-out notice approval workflows.
            var contract = new Contract(
                contractId, $"METER-FINAL-{Guid.NewGuid():N}", roomId,
                new DateTime(2026, 1, 1), new DateTime(2027, 12, 31),
                3_000_000m);
            contract.Activate();
            await _contractRepository.InsertAsync(contract, autoSave: true);

            var request = new MoveOutRequest(
                requestId, contractId, roomId, new DateOnly(2026, 11, 30),
                "Final meter evidence check");
            if (approved)
                request.Approve(new DateTime(2026, 10, 23), "Approved fixture");
            await _moveOutRepository.InsertAsync(request, autoSave: true);
        });
        return requestId;
    }

    [Fact]
    public async Task ME01_Pending_MoveOut_Does_Not_Admit_Final_Meter_Readings()
    {
        var roomId = await CreateRoomAsync();
        var requestId = await CreateMoveOutEvidenceFixtureAsync(
            roomId, approved: false);

        var result = await _finalMeterReadinessAppService.GetAsync(requestId);

        result.HasReadingEvidenceForBothUtilities.ShouldBeFalse();
        result.IsFinalBillApproved.ShouldBeFalse();
        result.Utilities.Count.ShouldBe(2);
        result.Utilities.All(x => x.Status == "MoveOutNotApproved").ShouldBeTrue();
    }

    [Fact]
    public async Task ME02_Both_Final_Readings_Present_Is_Still_Not_Bill_Approval()
    {
        var roomId = await CreateRoomAsync();
        var electricity = await CreateMeterAsync(roomId, UtilityType.Electricity, initialReading: 1000m);
        var water = await CreateMeterAsync(roomId, UtilityType.Water, initialReading: 100m);
        foreach (var meter in new[] { electricity, water })
        {
            await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
            {
                UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
                CurrentReading = meter.InitialReading,
                ReadingDate = new DateTime(2026, 10, 31)
            });
            await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
            {
                UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 11,
                CurrentReading = meter.InitialReading + 20m,
                ReadingDate = new DateTime(2026, 11, 30)
            });
        }
        var requestId = await CreateMoveOutEvidenceFixtureAsync(
            roomId, approved: true);

        var result = await _finalMeterReadinessAppService.GetAsync(requestId);

        result.HasReadingEvidenceForBothUtilities.ShouldBeTrue();
        result.IsFinalBillApproved.ShouldBeFalse();
        result.Utilities.All(x => x.Status == "ReadingEvidencePresent").ShouldBeTrue();
        result.Utilities.All(x => x.MeterReadingId.HasValue).ShouldBeTrue();
    }

    [Fact]
    public async Task ME03_Missing_Water_Final_Read_Blocks_Evidence_Completeness()
    {
        var roomId = await CreateRoomAsync();
        var electricity = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 1000m);
        await CreateMeterAsync(roomId, UtilityType.Water, initialReading: 100m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = electricity.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 1000m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = electricity.Id, BillingYear = 2026, BillingMonth = 11,
            CurrentReading = 1010m, ReadingDate = new DateTime(2026, 11, 30)
        });
        var requestId = await CreateMoveOutEvidenceFixtureAsync(
            roomId, approved: true);

        var result = await _finalMeterReadinessAppService.GetAsync(requestId);

        result.HasReadingEvidenceForBothUtilities.ShouldBeFalse();
        result.IsFinalBillApproved.ShouldBeFalse();
        result.Utilities.Single(x => x.UtilityType == UtilityType.Water)
            .Status.ShouldBe("FinalReadingMissingOrWrongDate");
    }

    [Fact]
    public async Task ME04_Reading_Day_Before_MoveOut_Is_Not_Final_Evidence()
    {
        var roomId = await CreateRoomAsync();
        var meter = await CreateMeterAsync(roomId, UtilityType.Electricity,
            initialReading: 100m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 100m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 11,
            CurrentReading = 110m, ReadingDate = new DateTime(2026, 11, 29)
        });
        var requestId = await CreateMoveOutEvidenceFixtureAsync(
            roomId, approved: true);

        var result = await _finalMeterReadinessAppService.GetAsync(requestId);

        result.HasReadingEvidenceForBothUtilities.ShouldBeFalse();
        result.Utilities.Single(x => x.UtilityType == UtilityType.Electricity)
            .Status.ShouldBe("FinalReadingMissingOrWrongDate");
    }

    [Fact]
    public async Task PR13_Reject_Interim_Month_Read_As_Whole_Month_Charge()
    {
        var room = await CreateRoomAsync();
        var meter = await CreateMeterAsync(room, UtilityType.Electricity, initialReading: 100m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 100m, ReadingDate = new DateTime(2026, 9, 30)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 110m, ReadingDate = new DateTime(2026, 10, 5)
        });
        await CreateRateForRoomAsync(room, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1));
        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            room, UtilityType.Electricity, 2026, 10);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.CoverageIssues.ShouldContain("PeriodEndReadingNotVerified");
    }

    [Fact]
    public async Task PR14_Manual_Removal_Requires_Review_Before_Pricing()
    {
        var room = await CreateRoomAsync();
        var meter = await CreateMeterAsync(room, UtilityType.Water, initialReading: 100m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 100m, ReadingDate = new DateTime(2026, 9, 30)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 110m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await _utilityMeterAppService.DeactivateAsync(meter.Id);
        await CreateRateForRoomAsync(room, UtilityType.Water,
            15000m, new DateTime(2026, 10, 1));
        var preview = await _chargePreviewAppService.GetMonthlyChargePreviewAsync(
            room, UtilityType.Water, 2026, 10);
        preview.ProvisionalAmount.ShouldBeNull();
        preview.UsagePreview.CoverageIssues
            .ShouldContain("ManualMeterDeactivationRequiresReview");
    }


    // Phase 2G.3A.1: no invoices or financial transactions are persisted.
    private async Task<Guid> CreateCandidateContractAsync(
        Guid roomId, DateTime start, bool active = true)
    {
        var id = Guid.NewGuid();
        var contract = new Contract(
            id, $"INV-DRAFT-{Guid.NewGuid():N}", roomId,
            start, new DateTime(2027, 12, 31), 3_000_000m);
        if (active) contract.Activate();
        await WithUnitOfWorkAsync(async () =>
        {
            await _contractRepository.InsertAsync(contract, autoSave: true);
        });
        return id;
    }

    private async Task SeedHealthyOctoberUsageAsync(Guid roomId)
    {
        foreach (var type in new[] { UtilityType.Electricity, UtilityType.Water })
        {
            var opening = type == UtilityType.Electricity ? 1000m : 100m;
            var added = type == UtilityType.Electricity ? 10m : 5m;
            var meter = await CreateMeterAsync(roomId, type, initialReading: opening);
            await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
            {
                UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 9,
                CurrentReading = opening, ReadingDate = new DateTime(2026, 9, 30)
            });
            await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
            {
                UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
                CurrentReading = opening + added,
                ReadingDate = new DateTime(2026, 10, 31)
            });
        }
        await CreateRateForRoomAsync(roomId, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1));
        await CreateRateForRoomAsync(roomId, UtilityType.Water,
            15000m, new DateTime(2026, 10, 1));
    }

    [Fact]
    public async Task ID01_Candidate_Records_Contract_Source_Lines_But_Cannot_Issue()
    {
        var room = await CreateRoomAsync();
        var contractId = await CreateCandidateContractAsync(
            room, new DateTime(2026, 1, 1));
        await SeedHealthyOctoberUsageAsync(room);

        var preview = await _invoiceDraftCandidates.GetMonthlyCandidateAsync(
            contractId, 2026, 10);

        preview.ContractId.ShouldBe(contractId);
        preview.RoomId.ShouldBe(room);
        preview.Blockers.ShouldBeEmpty();
        preview.UtilityLines.Count.ShouldBe(2);
        preview.ProvisionalTotal.ShouldBe(110000m);
        preview.UtilityLines.Single(x => x.UtilityType == UtilityType.Electricity)
            .RecordedQuantity.ShouldBe(10m);
        preview.UtilityLines.Single(x => x.UtilityType == UtilityType.Water)
            .ProvisionalAmount.ShouldBe(75000m);
        preview.UtilityLines.All(x => x.MeterSegments.Count == 1).ShouldBeTrue();
        preview.CanPersistDraft.ShouldBeFalse();
        preview.CanIssue.ShouldBeFalse();
    }

    [Fact]
    public async Task ID02_New_Tenant_MidMonth_Cannot_Inherit_Full_Room_Usage()
    {
        var room = await CreateRoomAsync();
        var contractId = await CreateCandidateContractAsync(
            room, new DateTime(2026, 10, 15));
        await SeedHealthyOctoberUsageAsync(room);

        var preview = await _invoiceDraftCandidates.GetMonthlyCandidateAsync(
            contractId, 2026, 10);

        preview.Blockers.ShouldContain("ContractDoesNotCoverWholeMonth");
        preview.ProvisionalTotal.ShouldBeNull();
        preview.UtilityLines.All(x => x.ProvisionalAmount is null).ShouldBeTrue();
        preview.CanIssue.ShouldBeFalse();
    }

    [Fact]
    public async Task ID03_Draft_Contract_Cannot_Price_An_Active_Tenants_Period()
    {
        var room = await CreateRoomAsync();
        var contractId = await CreateCandidateContractAsync(
            room, new DateTime(2026, 1, 1), active: false);
        await SeedHealthyOctoberUsageAsync(room);

        var preview = await _invoiceDraftCandidates.GetMonthlyCandidateAsync(
            contractId, 2026, 10);

        preview.Blockers.ShouldContain("ContractNotActive");
        preview.ProvisionalTotal.ShouldBeNull();
        preview.CanPersistDraft.ShouldBeFalse();
    }

    [Fact]
    public async Task ID04_Unknown_Contract_Fails_Closed()
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            _invoiceDraftCandidates.GetMonthlyCandidateAsync(
                Guid.NewGuid(), 2026, 10));
        ex.Code.ShouldBe(BuildingManagementErrorCodes.ContractNotFound);
    }


    [Fact]
    public async Task ID05_One_Utility_Missing_Price_Makes_Total_Unavailable()
    {
        var room = await CreateRoomAsync();
        var contractId = await CreateCandidateContractAsync(
            room, new DateTime(2026, 1, 1));
        var meter = await CreateMeterAsync(room, UtilityType.Electricity,
            initialReading: 100m);
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 9,
            CurrentReading = 100m, ReadingDate = new DateTime(2026, 9, 30)
        });
        await _meterReadingAppService.CreateAsync(new CreateMeterReadingDto
        {
            UtilityMeterId = meter.Id, BillingYear = 2026, BillingMonth = 10,
            CurrentReading = 110m, ReadingDate = new DateTime(2026, 10, 31)
        });
        await CreateRateForRoomAsync(room, UtilityType.Electricity,
            3500m, new DateTime(2026, 10, 1));

        var candidate = await _invoiceDraftCandidates.GetMonthlyCandidateAsync(
            contractId, 2026, 10);

        candidate.UtilityLines.Single(x => x.UtilityType == UtilityType.Electricity)
            .ProvisionalAmount.ShouldBe(35000m);
        candidate.UtilityLines.Single(x => x.UtilityType == UtilityType.Water)
            .ProvisionalAmount.ShouldBeNull();
        candidate.Blockers.ShouldContain("Water:RateOrUsageNotReady");
        candidate.ProvisionalTotal.ShouldBeNull();
        candidate.CanIssue.ShouldBeFalse();
    }

    [Fact]
    public async Task ID06_Overlapping_Active_Contracts_Block_Room_Utility_Charges()
    {
        var room = await CreateRoomAsync();
        var originalContract = await CreateCandidateContractAsync(
            room, new DateTime(2026, 1, 1));
        await CreateCandidateContractAsync(
            room, new DateTime(2026, 10, 15));
        await SeedHealthyOctoberUsageAsync(room);

        var candidate = await _invoiceDraftCandidates.GetMonthlyCandidateAsync(
            originalContract, 2026, 10);

        candidate.Blockers.ShouldContain("OverlappingActiveContractInRoom");
        candidate.ProvisionalTotal.ShouldBeNull();
        candidate.UtilityLines.All(x => x.ProvisionalAmount is null).ShouldBeTrue();
        candidate.CanIssue.ShouldBeFalse();
    }

}