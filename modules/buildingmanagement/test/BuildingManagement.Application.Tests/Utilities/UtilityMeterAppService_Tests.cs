using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Shouldly;
using System;
using System.Threading.Tasks;
using Volo.Abp;
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
}