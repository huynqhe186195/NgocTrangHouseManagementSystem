using BuildingManagement.Buildings;
using Shouldly;
using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Modularity;
using Xunit;

namespace BuildingManagement.Utilities;

public abstract class UtilityRateAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IUtilityRateAppService _utilityRateAppService;

    private readonly IRepository<Building, Guid>
        _buildingRepository;

    protected UtilityRateAppService_Tests()
    {
        _utilityRateAppService =
            GetRequiredService<IUtilityRateAppService>();

        _buildingRepository =
            GetRequiredService<IRepository<Building, Guid>>();
    }

    private async Task<Guid> CreateBuildingAsync()
    {
        var buildingId = Guid.NewGuid();

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
        });

        return buildingId;
    }

    // UR1
    [Fact]
    public async Task Should_Create_Electricity_Rate()
    {
        var buildingId =
            await CreateBuildingAsync();

        var result =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31),
                    Notes = "Electricity 2026"
                }
            );

        result.Id.ShouldNotBe(Guid.Empty);
        result.BuildingId.ShouldBe(buildingId);

        result.UtilityType.ShouldBe(
            UtilityType.Electricity
        );

        result.UnitPrice.ShouldBe(4_000);
    }

    // UR2
    [Fact]
    public async Task Should_Create_Water_Rate()
    {
        var buildingId =
            await CreateBuildingAsync();

        var result =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Water,
                    UnitPrice = 35_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo = null
                }
            );

        result.UtilityType.ShouldBe(
            UtilityType.Water
        );

        result.UnitPrice.ShouldBe(35_000);
        result.EffectiveTo.ShouldBeNull();
    }

    // UR3
    [Fact]
    public async Task Should_Throw_When_Building_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.CreateAsync(
                        new CreateUtilityRateDto
                        {
                            BuildingId = Guid.Parse(
                                "11111111-1111-1111-1111-111111111111"
                            ),
                            UtilityType =
                                UtilityType.Electricity,
                            UnitPrice = 4_000,
                            EffectiveFrom =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .BuildingNotFound
        );
    }

    // UR4
    [Fact]
    public async Task Should_Throw_When_UtilityType_Is_Invalid()
    {
        var buildingId =
            await CreateBuildingAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.CreateAsync(
                        new CreateUtilityRateDto
                        {
                            BuildingId = buildingId,
                            UtilityType =
                                (UtilityType)999,
                            UnitPrice = 4_000,
                            EffectiveFrom =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidUtilityType
        );
    }

    // UR5 + UR6
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Should_Throw_When_UnitPrice_Is_Invalid(
        decimal unitPrice)
    {
        var buildingId =
            await CreateBuildingAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.CreateAsync(
                        new CreateUtilityRateDto
                        {
                            BuildingId = buildingId,
                            UtilityType =
                                UtilityType.Electricity,
                            UnitPrice = unitPrice,
                            EffectiveFrom =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidUtilityUnitPrice
        );
    }

    // UR7
    [Fact]
    public async Task Should_Throw_When_EffectiveTo_Is_Before_EffectiveFrom()
    {
        var buildingId =
            await CreateBuildingAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.CreateAsync(
                        new CreateUtilityRateDto
                        {
                            BuildingId = buildingId,
                            UtilityType =
                                UtilityType.Electricity,
                            UnitPrice = 4_000,
                            EffectiveFrom =
                                new DateTime(2026, 12, 31),
                            EffectiveTo =
                                new DateTime(2026, 1, 1)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidUtilityRatePeriod
        );
    }

    // UR8
    [Fact]
    public async Task Should_Throw_When_Rate_Period_Overlaps()
    {
        var buildingId =
            await CreateBuildingAsync();

        await _utilityRateAppService.CreateAsync(
            new CreateUtilityRateDto
            {
                BuildingId = buildingId,
                UtilityType =
                    UtilityType.Electricity,
                UnitPrice = 4_000,
                EffectiveFrom =
                    new DateTime(2026, 1, 1),
                EffectiveTo =
                    new DateTime(2026, 12, 31)
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.CreateAsync(
                        new CreateUtilityRateDto
                        {
                            BuildingId = buildingId,
                            UtilityType =
                                UtilityType.Electricity,
                            UnitPrice = 4_500,
                            EffectiveFrom =
                                new DateTime(2026, 6, 1),
                            EffectiveTo =
                                new DateTime(2027, 5, 31)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityRatePeriodOverlaps
        );
    }

    // UR9
    [Fact]
    public async Task Should_Allow_Different_UtilityTypes_In_Same_Period()
    {
        var buildingId =
            await CreateBuildingAsync();

        await _utilityRateAppService.CreateAsync(
            new CreateUtilityRateDto
            {
                BuildingId = buildingId,
                UtilityType =
                    UtilityType.Electricity,
                UnitPrice = 4_000,
                EffectiveFrom =
                    new DateTime(2026, 1, 1),
                EffectiveTo =
                    new DateTime(2026, 12, 31)
            }
        );

        var water =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Water,
                    UnitPrice = 35_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31)
                }
            );

        water.UtilityType.ShouldBe(
            UtilityType.Water
        );
    }

    // UR10
    [Fact]
    public async Task Should_Allow_Same_Period_For_Different_Buildings()
    {
        var buildingA =
            await CreateBuildingAsync();

        var buildingB =
            await CreateBuildingAsync();

        await _utilityRateAppService.CreateAsync(
            new CreateUtilityRateDto
            {
                BuildingId = buildingA,
                UtilityType =
                    UtilityType.Electricity,
                UnitPrice = 4_000,
                EffectiveFrom =
                    new DateTime(2026, 1, 1),
                EffectiveTo =
                    new DateTime(2026, 12, 31)
            }
        );

        var second =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingB,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31)
                }
            );

        second.BuildingId.ShouldBe(buildingB);
    }

    // UR11
    [Fact]
    public async Task Should_Allow_Consecutive_Rate_Periods()
    {
        var buildingId =
            await CreateBuildingAsync();

        await _utilityRateAppService.CreateAsync(
            new CreateUtilityRateDto
            {
                BuildingId = buildingId,
                UtilityType =
                    UtilityType.Electricity,
                UnitPrice = 4_000,
                EffectiveFrom =
                    new DateTime(2026, 1, 1),
                EffectiveTo =
                    new DateTime(2026, 12, 31)
            }
        );

        var second =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_500,
                    EffectiveFrom =
                        new DateTime(2027, 1, 1),
                    EffectiveTo = null
                }
            );

        second.UnitPrice.ShouldBe(4_500);
    }

    // UR12
    [Fact]
    public async Task Should_Update_And_Keep_Own_Period()
    {
        var buildingId =
            await CreateBuildingAsync();

        var created =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31)
                }
            );

        var updated =
            await _utilityRateAppService.UpdateAsync(
                created.Id,
                new UpdateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_200,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31),
                    Notes = "Updated"
                }
            );

        updated.Id.ShouldBe(created.Id);
        updated.UnitPrice.ShouldBe(4_200);
        updated.Notes.ShouldBe("Updated");
    }

    // UR13
    [Fact]
    public async Task Should_Throw_When_Update_Causes_Overlap()
    {
        var buildingId =
            await CreateBuildingAsync();

        await _utilityRateAppService.CreateAsync(
            new CreateUtilityRateDto
            {
                BuildingId = buildingId,
                UtilityType =
                    UtilityType.Electricity,
                UnitPrice = 4_000,
                EffectiveFrom =
                    new DateTime(2026, 1, 1),
                EffectiveTo =
                    new DateTime(2026, 6, 30)
            }
        );

        var second =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_500,
                    EffectiveFrom =
                        new DateTime(2026, 7, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31)
                }
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.UpdateAsync(
                        second.Id,
                        new UpdateUtilityRateDto
                        {
                            BuildingId = buildingId,
                            UtilityType =
                                UtilityType.Electricity,
                            UnitPrice = 4_500,

                            // Đẩy đầu kỳ về tháng 6
                            // → chồng với rate đầu tiên.
                            EffectiveFrom =
                                new DateTime(2026, 6, 1),

                            EffectiveTo =
                                new DateTime(2026, 12, 31)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityRatePeriodOverlaps
        );
    }

    // UR14
    [Fact]
    public async Task Should_Get_Existing_UtilityRate()
    {
        var buildingId =
            await CreateBuildingAsync();

        var created =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Water,
                    UnitPrice = 35_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1)
                }
            );

        var result =
            await _utilityRateAppService.GetAsync(
                created.Id
            );

        result.Id.ShouldBe(created.Id);
        result.BuildingId.ShouldBe(buildingId);
        result.UnitPrice.ShouldBe(35_000);
    }

    // UR15
    [Fact]
    public async Task Should_Throw_When_UtilityRate_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.GetAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityRateNotFound
        );
    }

    // UR16
    [Fact]
    public async Task Should_Get_UtilityRate_List()
    {
        var buildingId =
            await CreateBuildingAsync();

        var electricity =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1)
                }
            );

        var water =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Water,
                    UnitPrice = 35_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1)
                }
            );

        var result =
            await _utilityRateAppService.GetListAsync();

        result.ShouldContain(
            x => x.Id == electricity.Id
        );

        result.ShouldContain(
            x => x.Id == water.Id
        );
    }

    // UR17
    [Fact]
    public async Task Should_Soft_Delete_UtilityRate()
    {
        var buildingId =
            await CreateBuildingAsync();

        var created =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31)
                }
            );

        await _utilityRateAppService.DeleteAsync(
            created.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.GetAsync(
                        created.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityRateNotFound
        );
    }

    // UR18
    [Fact]
    public async Task Should_Not_Recreate_Deleted_Rate_With_Same_Period()
    {
        var buildingId =
            await CreateBuildingAsync();

        var created =
            await _utilityRateAppService.CreateAsync(
                new CreateUtilityRateDto
                {
                    BuildingId = buildingId,
                    UtilityType =
                        UtilityType.Electricity,
                    UnitPrice = 4_000,
                    EffectiveFrom =
                        new DateTime(2026, 1, 1),
                    EffectiveTo =
                        new DateTime(2026, 12, 31)
                }
            );

        await _utilityRateAppService.DeleteAsync(
            created.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _utilityRateAppService.CreateAsync(
                        new CreateUtilityRateDto
                        {
                            BuildingId = buildingId,
                            UtilityType =
                                UtilityType.Electricity,
                            UnitPrice = 4_500,
                            EffectiveFrom =
                                new DateTime(2026, 1, 1),
                            EffectiveTo =
                                new DateTime(2026, 12, 31)
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .UtilityRatePeriodOverlaps
        );
    }
}