using BuildingManagement.Buildings;
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

namespace BuildingManagement.Contracts;

public abstract class ContractRenewalHoldAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IContractRenewalHoldAppService
        _renewalHoldAppService;

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

    private readonly IRepository<ContractRenewalHold, Guid>
        _renewalHoldRepository;

    private readonly IClock _clock;

    protected ContractRenewalHoldAppService_Tests()
    {
        _renewalHoldAppService =
            GetRequiredService<
                IContractRenewalHoldAppService>();

        _contractAppService =
            GetRequiredService<IContractAppService>();

        _tenantAppService =
            GetRequiredService<ITenantAppService>();

        _buildingRepository =
            GetRequiredService<
                IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<
                IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<
                IRepository<Room, Guid>>();

        _renewalHoldRepository =
            GetRequiredService<
                IRepository<ContractRenewalHold, Guid>>();

        _clock =
            GetRequiredService<IClock>();
    }

    private static string NewContractNumber()
    {
        return $"HD-HOLD-{Guid.NewGuid():N}";
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

    private async Task<TenantDto> CreateTenantAsync()
    {
        return await _tenantAppService.CreateAsync(
            new CreateTenantDto
            {
                FullName =
                    $"Test Tenant {Guid.NewGuid():N}"
            }
        );
    }

    private async Task<ContractDto>
        CreateDraftContractAsync(
            bool hasEndDate = true)
    {
        var roomId =
            await CreateRoomAsync();

        return await _contractAppService.CreateAsync(
            new CreateContractDto
            {
                ContractNumber =
                    NewContractNumber(),

                RoomId = roomId,

                StartDate =
                    new DateTime(2026, 1, 1),

                EndDate =
                    hasEndDate
                        ? new DateTime(
                            2026,
                            12,
                            31
                        )
                        : null,

                MonthlyRent = 3_000_000,

                DepositAmount = 3_000_000
            }
        );
    }

    private async Task<ContractDto>
        CreateActiveContractAsync(
            bool hasEndDate = true)
    {
        var contract =
            await CreateDraftContractAsync(
                hasEndDate
            );

        var tenant =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenant.Id,
                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        return await _contractAppService
            .ActivateAsync(contract.Id);
    }

    private Task<ContractRenewalHoldDto>
        CreateHoldAsync(Guid contractId)
    {
        return _renewalHoldAppService.CreateAsync(
            new CreateContractRenewalHoldDto
            {
                CurrentContractId = contractId,
                Notes = "Customer wants to stay"
            }
        );
    }

    // RH01
    [Fact]
    public async Task
        Should_Create_24_Hour_Renewal_Hold()
    {
        var contract =
            await CreateActiveContractAsync();

        var result =
            await CreateHoldAsync(
                contract.Id
            );

        result.CurrentContractId.ShouldBe(
            contract.Id
        );

        result.RoomId.ShouldBe(
            contract.RoomId
        );

        result.Status.ShouldBe(
            ContractRenewalHoldStatus.Active
        );

        (
            result.ExpiresAt -
            result.RequestedAt
        ).ShouldBe(
            TimeSpan.FromHours(24)
        );
    }

    // RH02
    [Fact]
    public async Task
        Should_Throw_When_Contract_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateHoldAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractNotFound
        );
    }

    // RH03
    [Fact]
    public async Task
        Should_Not_Create_Hold_For_Draft_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateHoldAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldCannotBeCreated
        );
    }

    // RH04
    [Fact]
    public async Task
        Should_Not_Create_Hold_For_Contract_Without_EndDate()
    {
        var contract =
            await CreateActiveContractAsync(
                hasEndDate: false
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateHoldAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldCannotBeCreated
        );
    }

    // RH05
    [Fact]
    public async Task
        Should_Allow_Only_One_Hold_Per_Contract()
    {
        var contract =
            await CreateActiveContractAsync();

        var hold =
            await CreateHoldAsync(
                contract.Id
            );

        await _renewalHoldAppService.CancelAsync(
            hold.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateHoldAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldAlreadyExists
        );
    }

    // RH06
    [Fact]
    public async Task
        Should_Not_Create_Hold_When_Contract_Already_Renewed()
    {
        var contract =
            await CreateActiveContractAsync();

        await CreateHoldAsync(
            contract.Id
        );

        await _contractAppService.RenewAsync(
            contract.Id,
            new RenewContractDto
            {
                ContractNumber =
                    NewContractNumber(),

                StartDate =
                    new DateTime(2027, 1, 1),

                EndDate =
                    new DateTime(2027, 12, 31),

                MonthlyRent = 3_500_000,
                DepositAmount = 3_500_000
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    CreateHoldAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractAlreadyRenewed
        );
    }

    // RH07
    [Fact]
    public async Task
        Should_Get_Existing_Renewal_Hold()
    {
        var contract =
            await CreateActiveContractAsync();

        var created =
            await CreateHoldAsync(
                contract.Id
            );

        var result =
            await _renewalHoldAppService.GetAsync(
                created.Id
            );

        result.Id.ShouldBe(
            created.Id
        );

        result.CurrentContractId.ShouldBe(
            contract.Id
        );
    }

    // RH08
    [Fact]
    public async Task
        Should_Throw_When_Renewal_Hold_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _renewalHoldAppService.GetAsync(
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"
                        )
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldNotFound
        );
    }

    // RH09
    [Fact]
    public async Task
        Should_Cancel_Active_Renewal_Hold()
    {
        var contract =
            await CreateActiveContractAsync();

        var hold =
            await CreateHoldAsync(
                contract.Id
            );

        var result =
            await _renewalHoldAppService.CancelAsync(
                hold.Id
            );

        result.Status.ShouldBe(
            ContractRenewalHoldStatus.Cancelled
        );
    }

    // RH10
    [Fact]
    public async Task
        Should_Not_Cancel_Renewal_Hold_Twice()
    {
        var contract =
            await CreateActiveContractAsync();

        var hold =
            await CreateHoldAsync(
                contract.Id
            );

        await _renewalHoldAppService.CancelAsync(
            hold.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _renewalHoldAppService
                        .CancelAsync(hold.Id)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldCannotBeCancelled
        );
    }

    // RH11
    [Fact]
    public async Task
        Should_Auto_Expire_Hold_When_Getting_It_After_24_Hours()
    {
        var contract =
            await CreateActiveContractAsync();

        var oldHold =
            new ContractRenewalHold(
                Guid.NewGuid(),
                contract.Id,
                contract.RoomId,
                _clock.Now.AddHours(-25),
                "Expired test"
            );

        await WithUnitOfWorkAsync(
            async () =>
            {
                await _renewalHoldRepository
                    .InsertAsync(
                        oldHold,
                        autoSave: true
                    );
            }
        );

        var result =
            await _renewalHoldAppService.GetAsync(
                oldHold.Id
            );

        result.Status.ShouldBe(
            ContractRenewalHoldStatus.Expired
        );
    }

    // RH12
    [Fact]
    public async Task
        Should_Not_Cancel_Hold_After_24_Hours()
    {
        var contract =
            await CreateActiveContractAsync();

        var oldHold =
            new ContractRenewalHold(
                Guid.NewGuid(),
                contract.Id,
                contract.RoomId,
                _clock.Now.AddHours(-25),
                "Expired test"
            );

        await WithUnitOfWorkAsync(
            async () =>
            {
                await _renewalHoldRepository
                    .InsertAsync(
                        oldHold,
                        autoSave: true
                    );
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _renewalHoldAppService
                        .CancelAsync(oldHold.Id)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldExpired
        );
    }

    // RH13
    [Fact]
    public async Task
        Should_Get_List_And_Refresh_Expired_Holds()
    {
        var activeContract =
            await CreateActiveContractAsync();

        var expiredContract =
            await CreateActiveContractAsync();

        var activeHold =
            await CreateHoldAsync(
                activeContract.Id
            );

        var oldHold =
            new ContractRenewalHold(
                Guid.NewGuid(),
                expiredContract.Id,
                expiredContract.RoomId,
                _clock.Now.AddHours(-25),
                "Expired hold"
            );

        await WithUnitOfWorkAsync(
            async () =>
            {
                await _renewalHoldRepository
                    .InsertAsync(
                        oldHold,
                        autoSave: true
                    );
            }
        );

        var result =
            await _renewalHoldAppService
                .GetListAsync();

        result.ShouldContain(
            x =>
                x.Id == activeHold.Id
                &&
                x.Status ==
                    ContractRenewalHoldStatus.Active
        );

        result.ShouldContain(
            x =>
                x.Id == oldHold.Id
                &&
                x.Status ==
                    ContractRenewalHoldStatus.Expired
        );
    }
}