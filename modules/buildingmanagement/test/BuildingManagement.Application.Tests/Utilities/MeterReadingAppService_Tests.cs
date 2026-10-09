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

public abstract class MeterReadingAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IMeterReadingAppService
        _meterReadingAppService;

    private readonly IUtilityMeterAppService
        _utilityMeterAppService;

    private readonly IRepository<Building, Guid>
        _buildingRepository;

    private readonly IRepository<Floor, Guid>
        _floorRepository;

    private readonly IRepository<Room, Guid>
        _roomRepository;

    protected MeterReadingAppService_Tests()
    {
        _meterReadingAppService =
            GetRequiredService<IMeterReadingAppService>();

        _utilityMeterAppService =
            GetRequiredService<IUtilityMeterAppService>();

        _buildingRepository =
            GetRequiredService<IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<IRepository<Room, Guid>>();
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

    private async Task<UtilityMeterDto> CreateMeterAsync(
        decimal initialReading = 1000m,
        DateTime? installedDate = null)
    {
        var roomId =
            await CreateRoomAsync();

        return await _utilityMeterAppService.CreateAsync(
            new CreateUtilityMeterDto
            {
                RoomId = roomId,
                UtilityType = UtilityType.Electricity,
                MeterCode =
                    $"METER-{Guid.NewGuid():N}"
                        .ToUpperInvariant(),
                InitialReading = initialReading,
                InstalledDate =
                    installedDate
                    ?? new DateTime(2026, 1, 1)
            }
        );
    }

    private Task<MeterReadingDto> CreateReadingAsync(
        Guid utilityMeterId,
        int year,
        int month,
        decimal currentReading,
        DateTime readingDate)
    {
        return _meterReadingAppService.CreateAsync(
            new CreateMeterReadingDto
            {
                UtilityMeterId = utilityMeterId,
                BillingYear = year,
                BillingMonth = month,
                CurrentReading = currentReading,
                ReadingDate = readingDate
            }
        );
    }

    // MR01
    [Fact]
    public async Task
        Should_Use_InitialReading_For_First_Reading()
    {
        var meter =
            await CreateMeterAsync(
                initialReading: 1000.125m
            );

        var result =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1120.500m,
                new DateTime(2026, 10, 31)
            );

        result.PreviousReading.ShouldBe(
            1000.125m
        );

        result.CurrentReading.ShouldBe(
            1120.500m
        );

        result.Usage.ShouldBe(
            120.375m
        );
    }

    // MR02
    [Fact]
    public async Task
        Should_Use_Previous_CurrentReading_For_Next_Reading()
    {
        var meter =
            await CreateMeterAsync();

        var october =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1120m,
                new DateTime(2026, 10, 31)
            );

        var november =
            await CreateReadingAsync(
                meter.Id,
                2026,
                11,
                1255.500m,
                new DateTime(2026, 11, 30)
            );

        november.PreviousReading.ShouldBe(
            october.CurrentReading
        );

        november.Usage.ShouldBe(
            135.500m
        );
    }

    // MR03
    [Fact]
    public async Task
        Should_Throw_When_UtilityMeter_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        ),
                        2026,
                        10,
                        1000m,
                        new DateTime(2026, 10, 31)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityMeterNotFound
        );
    }

    // MR04
    [Fact]
    public async Task
        Should_Throw_When_UtilityMeter_Is_Inactive()
    {
        var meter =
            await CreateMeterAsync();

        await _utilityMeterAppService
            .DeactivateAsync(meter.Id);

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        10,
                        1100m,
                        new DateTime(2026, 10, 31)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityMeterInactive
        );
    }

    // MR05 + MR06
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task
        Should_Throw_When_BillingMonth_Is_Invalid(
            int month)
    {
        var meter =
            await CreateMeterAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        month,
                        1100m,
                        new DateTime(2026, 10, 31)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidBillingMonth
        );
    }

    // MR07 + MR08
    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public async Task
        Should_Throw_When_BillingYear_Is_Invalid(
            int year)
    {
        var meter =
            await CreateMeterAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        year,
                        10,
                        1100m,
                        new DateTime(2026, 10, 31)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidBillingYear
        );
    }

    // MR09
    [Fact]
    public async Task
        Should_Throw_When_Period_Already_Exists()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            10,
            1100m,
            new DateTime(2026, 10, 31)
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        10,
                        1150m,
                        new DateTime(2026, 11, 1)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterReadingAlreadyExists
        );
    }

    // MR10
    [Fact]
    public async Task
        Should_Throw_When_Period_Is_Before_Latest()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            11,
            1200m,
            new DateTime(2026, 11, 30)
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        10,
                        1250m,
                        new DateTime(2026, 12, 1)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterReadingPeriodMustBeAfterLatest
        );
    }

    // MR11
    [Fact]
    public async Task
        Should_Throw_When_CurrentReading_Is_Lower_Than_Previous()
    {
        var meter =
            await CreateMeterAsync(
                initialReading: 1000m
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        10,
                        999.999m,
                        new DateTime(2026, 10, 31)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidCurrentReading
        );
    }

    // MR12
    [Fact]
    public async Task
        Should_Allow_Zero_Usage()
    {
        var meter =
            await CreateMeterAsync(
                initialReading: 1000m
            );

        var result =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1000m,
                new DateTime(2026, 10, 31)
            );

        result.PreviousReading.ShouldBe(1000m);
        result.CurrentReading.ShouldBe(1000m);
        result.Usage.ShouldBe(0m);
    }

    // MR13
    [Fact]
    public async Task
        Should_Throw_When_ReadingDate_Is_Before_InstalledDate()
    {
        var meter =
            await CreateMeterAsync(
                installedDate:
                    new DateTime(2026, 10, 1)
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        10,
                        1100m,
                        new DateTime(2026, 9, 30)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidReadingDate
        );
    }

    // MR14
    [Fact]
    public async Task
        Should_Throw_When_ReadingDate_Is_Not_After_Previous_Reading()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            10,
            1100m,
            new DateTime(2026, 10, 31)
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateReadingAsync(
                        meter.Id,
                        2026,
                        11,
                        1200m,
                        new DateTime(2026, 10, 31)
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidReadingDate
        );
    }

    // MR15
    [Fact]
    public async Task
        Should_Allow_Period_To_Move_To_Next_Year()
    {
        var meter =
            await CreateMeterAsync();

        var december =
            await CreateReadingAsync(
                meter.Id,
                2026,
                12,
                1100m,
                new DateTime(2026, 12, 31)
            );

        var january =
            await CreateReadingAsync(
                meter.Id,
                2027,
                1,
                1200m,
                new DateTime(2027, 1, 31)
            );

        january.PreviousReading.ShouldBe(
            december.CurrentReading
        );

        january.BillingYear.ShouldBe(2027);
        january.BillingMonth.ShouldBe(1);
    }

    // MR16
    [Fact]
    public async Task
        Should_Get_Existing_MeterReading()
    {
        var meter =
            await CreateMeterAsync();

        var created =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1100m,
                new DateTime(2026, 10, 31)
            );

        var result =
            await _meterReadingAppService
                .GetAsync(created.Id);

        result.Id.ShouldBe(created.Id);
        result.UtilityMeterId.ShouldBe(meter.Id);
    }

    // MR17
    [Fact]
    public async Task
        Should_Throw_When_MeterReading_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _meterReadingAppService.GetAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .MeterReadingNotFound
        );
    }

    // MR18
    [Fact]
    public async Task
        Should_Get_MeterReading_List()
    {
        var meter =
            await CreateMeterAsync();

        var first =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1100m,
                new DateTime(2026, 10, 31)
            );

        var second =
            await CreateReadingAsync(
                meter.Id,
                2026,
                11,
                1200m,
                new DateTime(2026, 11, 30)
            );

        var result =
            await _meterReadingAppService
                .GetListAsync();

        result.ShouldContain(
            x => x.Id == first.Id
        );

        result.ShouldContain(
            x => x.Id == second.Id
        );
    }

    // MR19
    [Fact]
    public async Task
        Should_Update_Latest_MeterReading()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            10,
            1100m,
            new DateTime(2026, 10, 31)
        );

        var latest =
            await CreateReadingAsync(
                meter.Id,
                2026,
                11,
                1200m,
                new DateTime(2026, 11, 30)
            );

        var updated =
            await _meterReadingAppService.UpdateAsync(
                latest.Id,
                new UpdateMeterReadingDto
                {
                    CurrentReading = 1250.500m,
                    ReadingDate =
                        new DateTime(2026, 11, 29),
                    Notes = "Corrected reading"
                }
            );

        updated.PreviousReading.ShouldBe(
            1100m
        );

        updated.CurrentReading.ShouldBe(
            1250.500m
        );

        updated.Usage.ShouldBe(
            150.500m
        );

        updated.Notes.ShouldBe(
            "Corrected reading"
        );
    }

    // MR20
    [Fact]
    public async Task
        Should_Allow_Update_Of_Latest_Reading_When_Meter_Is_Inactive()
    {
        var meter =
            await CreateMeterAsync();

        var reading =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1100m,
                new DateTime(2026, 10, 31)
            );

        await _utilityMeterAppService
            .DeactivateAsync(meter.Id);

        var updated =
            await _meterReadingAppService.UpdateAsync(
                reading.Id,
                new UpdateMeterReadingDto
                {
                    CurrentReading = 1110m,
                    ReadingDate =
                        new DateTime(2026, 10, 31),
                    Notes = "Corrected after deactivation"
                }
            );

        updated.CurrentReading.ShouldBe(
            1110m
        );
    }

    // MR21
    [Fact]
    public async Task
        Should_Not_Update_Non_Latest_MeterReading()
    {
        var meter =
            await CreateMeterAsync();

        var first =
            await CreateReadingAsync(
                meter.Id,
                2026,
                10,
                1100m,
                new DateTime(2026, 10, 31)
            );

        await CreateReadingAsync(
            meter.Id,
            2026,
            11,
            1200m,
            new DateTime(2026, 11, 30)
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _meterReadingAppService.UpdateAsync(
                        first.Id,
                        new UpdateMeterReadingDto
                        {
                            CurrentReading = 1150m,
                            ReadingDate =
                                new DateTime(
                                    2026,
                                    10,
                                    31
                                )
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .OnlyLatestMeterReadingCanBeUpdated
        );
    }

    // MR22
    [Fact]
    public async Task
        Should_Throw_When_Updated_CurrentReading_Is_Lower_Than_Previous()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            10,
            1100m,
            new DateTime(2026, 10, 31)
        );

        var latest =
            await CreateReadingAsync(
                meter.Id,
                2026,
                11,
                1200m,
                new DateTime(2026, 11, 30)
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _meterReadingAppService.UpdateAsync(
                        latest.Id,
                        new UpdateMeterReadingDto
                        {
                            CurrentReading = 1099m,
                            ReadingDate =
                                new DateTime(
                                    2026,
                                    11,
                                    30
                                )
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidCurrentReading
        );
    }

    // MR23
    [Fact]
    public async Task
        Should_Throw_When_Updated_ReadingDate_Is_Not_After_Previous_Reading()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            10,
            1100m,
            new DateTime(2026, 10, 31)
        );

        var latest =
            await CreateReadingAsync(
                meter.Id,
                2026,
                11,
                1200m,
                new DateTime(2026, 11, 30)
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _meterReadingAppService.UpdateAsync(
                        latest.Id,
                        new UpdateMeterReadingDto
                        {
                            CurrentReading = 1250m,
                            ReadingDate =
                                new DateTime(
                                    2026,
                                    10,
                                    31
                                )
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidReadingDate
        );
    }
    //MR24 — meter đã có reading không được delete
    [Fact]
    public async Task
    Should_Not_Delete_UtilityMeter_With_Reading_History()
    {
        var meter =
            await CreateMeterAsync();

        await CreateReadingAsync(
            meter.Id,
            2026,
            10,
            1100m,
            new DateTime(2026, 10, 31)
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityMeterAppService
                        .DeleteAsync(meter.Id)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityMeterHasReadingHistory
        );

        var existingMeter =
            await _utilityMeterAppService
                .GetAsync(meter.Id);

        existingMeter.Id.ShouldBe(meter.Id);
    }
}