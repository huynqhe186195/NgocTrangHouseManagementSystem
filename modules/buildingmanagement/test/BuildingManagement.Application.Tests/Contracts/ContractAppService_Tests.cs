using BuildingManagement.Buildings;
using BuildingManagement.Floors;
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

namespace BuildingManagement.Contracts;

public abstract class ContractAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly IContractAppService _contractAppService;

    private readonly IRepository<Building, Guid> _buildingRepository;
    private readonly IRepository<Floor, Guid> _floorRepository;
    private readonly IRepository<Room, Guid> _roomRepository;
    private readonly ITenantAppService _tenantAppService;
    private readonly IRoomAppService _roomAppService;
    private readonly IContractRenewalHoldAppService _renewalHoldAppService;
    private readonly IRepository<ContractRenewalHold, Guid> _renewalHoldRepository;
    private readonly IClock _clock;
    private readonly IRoomReservationAppService _roomReservationAppService;
    private readonly IRepository<RoomReservation, Guid> _roomReservationRepository;
    protected ContractAppService_Tests()
    {
        _contractAppService =
            GetRequiredService<IContractAppService>();

        _buildingRepository =
            GetRequiredService<IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<IRepository<Room, Guid>>();

        _tenantAppService =
            GetRequiredService<ITenantAppService>();

        _roomAppService =
            GetRequiredService<IRoomAppService>();

        _renewalHoldAppService =
            GetRequiredService<IContractRenewalHoldAppService>();

        _renewalHoldRepository =
            GetRequiredService<
                IRepository<ContractRenewalHold, Guid>>();

        _clock =
            GetRequiredService<IClock>();

        _roomReservationAppService = GetRequiredService<IRoomReservationAppService>();

        _roomReservationRepository =
            GetRequiredService<IRepository<RoomReservation, Guid>>();
    }

    private Task<ContractRenewalHoldDto>
    CreateRenewalHoldAsync(Guid contractId)
    {
        return _renewalHoldAppService.CreateAsync(
            new CreateContractRenewalHoldDto
            {
                CurrentContractId = contractId,
                Notes = "Renewal test hold"
            }
        );
    }

    private async Task<ContractDto> CreateDraftContractAsync()
    {
        var roomId = await CreateRoomAsync();

        return await _contractAppService.CreateAsync(
            new CreateContractDto
            {
                ContractNumber = NewContractNumber(),
                RoomId = roomId,
                StartDate = new DateTime(2026, 11, 1),
                EndDate = new DateTime(2027, 10, 31),
                MonthlyRent = 3_000_000,
                DepositAmount = 3_000_000
            }
        );
    }

    private async Task<ContractDto>
    CreateActiveContractForRoomAsync(Guid roomId)
    {
        var contract =
            await CreateDraftContractForRoomAsync(roomId);

        await AddPrimaryTenantAsync(
            contract.Id
        );

        return await _contractAppService
            .ActivateAsync(contract.Id);
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

    private static string NewContractNumber()
    {
        return $"HD-TEST-{Guid.NewGuid():N}";
    }

    // C1
    [Fact]
    public async Task Should_Create_Contract()
    {
        var roomId = await CreateRoomAsync();

        var result =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = NewContractNumber(),
                    RoomId = roomId,
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2027, 10, 31),
                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000,
                    Notes = "Test contract"
                }
            );

        result.Id.ShouldNotBe(Guid.Empty);
        result.RoomId.ShouldBe(roomId);
        result.Status.ShouldBe(
            ContractStatus.Draft
        );
    }

    // C2
    [Fact]
    public async Task Should_Throw_When_Room_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.CreateAsync(
                    new CreateContractDto
                    {
                        ContractNumber =
                            NewContractNumber(),

                        RoomId =
                            Guid.Parse(
                                "11111111-1111-1111-1111-111111111111"
                            ),

                        StartDate =
                            new DateTime(2026, 11, 1),

                        EndDate =
                            new DateTime(2027, 10, 31),

                        MonthlyRent = 3_000_000,
                        DepositAmount = 3_000_000
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.RoomNotFound
        );
    }

    // C3
    [Fact]
    public async Task Should_Throw_When_EndDate_Is_Before_StartDate()
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.CreateAsync(
                    new CreateContractDto
                    {
                        ContractNumber =
                            NewContractNumber(),

                        RoomId = roomId,

                        StartDate =
                            new DateTime(2026, 11, 1),

                        EndDate =
                            new DateTime(2026, 10, 1),

                        MonthlyRent = 3_000_000,
                        DepositAmount = 3_000_000
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidContractPeriod
        );
    }

    // C4
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Should_Throw_When_MonthlyRent_Is_Invalid(
        decimal monthlyRent)
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.CreateAsync(
                    new CreateContractDto
                    {
                        ContractNumber =
                            NewContractNumber(),

                        RoomId = roomId,

                        StartDate =
                            new DateTime(2026, 11, 1),

                        EndDate =
                            new DateTime(2027, 10, 31),

                        MonthlyRent = monthlyRent,
                        DepositAmount = 3_000_000
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidMonthlyRent
        );
    }

    // C5
    [Fact]
    public async Task Should_Throw_When_DepositAmount_Is_Negative()
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.CreateAsync(
                    new CreateContractDto
                    {
                        ContractNumber =
                            NewContractNumber(),

                        RoomId = roomId,

                        StartDate =
                            new DateTime(2026, 11, 1),

                        EndDate =
                            new DateTime(2027, 10, 31),

                        MonthlyRent = 3_000_000,
                        DepositAmount = -1
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidDepositAmount
        );
    }

    // C6
    [Fact]
    public async Task Should_Throw_When_ContractNumber_Already_Exists()
    {
        var roomId = await CreateRoomAsync();

        var contractNumber =
            $"HD-DUP-{Guid.NewGuid():N}";

        await _contractAppService.CreateAsync(
            new CreateContractDto
            {
                ContractNumber =
                    contractNumber.ToUpperInvariant(),

                RoomId = roomId,

                StartDate =
                    new DateTime(2026, 11, 1),

                EndDate =
                    new DateTime(2027, 10, 31),

                MonthlyRent = 3_000_000,
                DepositAmount = 3_000_000
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.CreateAsync(
                    new CreateContractDto
                    {
                        ContractNumber =
                            contractNumber
                                .ToLowerInvariant(),

                        RoomId = roomId,

                        StartDate =
                            new DateTime(2026, 12, 1),

                        EndDate =
                            new DateTime(2027, 12, 1),

                        MonthlyRent = 3_000_000,
                        DepositAmount = 3_000_000
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractNumberAlreadyExists
        );
    }

    // C7
    [Fact]
    public async Task Should_Get_Existing_Contract()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId = roomId,

                    StartDate =
                        new DateTime(2026, 11, 1),

                    EndDate =
                        new DateTime(2027, 10, 31),

                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        var result =
            await _contractAppService.GetAsync(
                created.Id
            );

        result.Id.ShouldBe(created.Id);
        result.RoomId.ShouldBe(roomId);
        result.Status.ShouldBe(
            ContractStatus.Draft
        );
    }

    // C8
    [Fact]
    public async Task Should_Throw_When_Contract_Does_Not_Exist()
    {
        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.GetAsync(
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

    // C9
    [Fact]
    public async Task Should_Update_Contract()
    {
        var originalRoomId = await CreateRoomAsync();
        var newRoomId = await CreateRoomAsync();

        var created =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = NewContractNumber(),
                    RoomId = originalRoomId,
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2027, 10, 31),
                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        var updated =
            await _contractAppService.UpdateAsync(
                created.Id,
                new UpdateContractDto
                {
                    ContractNumber = "HD-UPDATED-001",
                    RoomId = newRoomId,
                    StartDate = new DateTime(2026, 12, 1),
                    EndDate = new DateTime(2027, 11, 30),
                    MonthlyRent = 3_500_000,
                    DepositAmount = 3_500_000,
                    Notes = "Updated contract"
                }
            );

        updated.Id.ShouldBe(created.Id);

        updated.ContractNumber.ShouldBe(
            "HD-UPDATED-001"
        );

        updated.RoomId.ShouldBe(newRoomId);

        updated.MonthlyRent.ShouldBe(
            3_500_000
        );

        updated.DepositAmount.ShouldBe(
            3_500_000
        );

        updated.Status.ShouldBe(
            ContractStatus.Draft
        );
    }

    [Fact]
    public async Task Should_Update_And_Keep_Own_ContractNumber()
    {
        var roomId = await CreateRoomAsync();

        var created =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = "HD-KEEP-001",
                    RoomId = roomId,
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2027, 10, 31),
                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        var updated =
            await _contractAppService.UpdateAsync(
                created.Id,
                new UpdateContractDto
                {
                    ContractNumber = "HD-KEEP-001",
                    RoomId = roomId,
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2027, 10, 31),
                    MonthlyRent = 3_200_000,
                    DepositAmount = 3_000_000
                }
            );

        updated.ContractNumber.ShouldBe(
            "HD-KEEP-001"
        );

        updated.MonthlyRent.ShouldBe(
            3_200_000
        );
    }

    [Fact]
    public async Task Should_Throw_When_Update_Uses_Another_ContractNumber()
    {
        var roomId = await CreateRoomAsync();

        var first =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = "HD-DUP-A",
                    RoomId = roomId,
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2027, 10, 31),
                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        var second =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = "HD-DUP-B",
                    RoomId = roomId,
                    StartDate = new DateTime(2027, 11, 1),
                    EndDate = new DateTime(2028, 10, 31),
                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.UpdateAsync(
                    second.Id,
                    new UpdateContractDto
                    {
                        ContractNumber = first.ContractNumber,
                        RoomId = roomId,
                        StartDate = new DateTime(2027, 11, 1),
                        EndDate = new DateTime(2028, 10, 31),
                        MonthlyRent = 3_000_000,
                        DepositAmount = 3_000_000
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractNumberAlreadyExists
        );
    }

    [Fact]
    public async Task Should_Throw_When_Updating_Nonexistent_Contract()
    {
        var roomId = await CreateRoomAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _contractAppService.UpdateAsync(
                    Guid.Parse(
                        "11111111-1111-1111-1111-111111111111"
                    ),
                    new UpdateContractDto
                    {
                        ContractNumber = "HD-NOT-FOUND",
                        RoomId = roomId,
                        StartDate = new DateTime(2026, 11, 1),
                        EndDate = new DateTime(2027, 10, 31),
                        MonthlyRent = 3_000_000,
                        DepositAmount = 3_000_000
                    }
                )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractNotFound
        );
    }

    [Fact]
    public async Task Should_Get_Contract_List()
    {
        var roomId = await CreateRoomAsync();

        var first =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = NewContractNumber(),
                    RoomId = roomId,
                    StartDate = new DateTime(2026, 11, 1),
                    EndDate = new DateTime(2027, 10, 31),
                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        var second =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber = NewContractNumber(),
                    RoomId = roomId,
                    StartDate = new DateTime(2027, 11, 1),
                    EndDate = new DateTime(2028, 10, 31),
                    MonthlyRent = 3_500_000,
                    DepositAmount = 3_500_000
                }
            );

        var result =
            await _contractAppService.GetListAsync();

        result.ShouldContain(
            x => x.Id == first.Id
        );

        result.ShouldContain(
            x => x.Id == second.Id
        );
    }
    //========================CT====================================
    [Fact]
    public async Task Should_Add_Primary_Tenant_To_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenant =
            await CreateTenantAsync();

        var result =
            await _contractAppService.AddTenantAsync(
                contract.Id,
                new AddContractTenantDto
                {
                    TenantId = tenant.Id,
                    Role =
                        ContractTenantRole.PrimaryTenant
                }
            );

        result.ContractId.ShouldBe(contract.Id);
        result.TenantId.ShouldBe(tenant.Id);

        result.Role.ShouldBe(
            ContractTenantRole.PrimaryTenant
        );
    }

    [Fact]
    public async Task Should_Add_Member_To_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenant =
            await CreateTenantAsync();

        var result =
            await _contractAppService.AddTenantAsync(
                contract.Id,
                new AddContractTenantDto
                {
                    TenantId = tenant.Id,
                    Role = ContractTenantRole.Member
                }
            );

        result.ContractId.ShouldBe(contract.Id);
        result.TenantId.ShouldBe(tenant.Id);

        result.Role.ShouldBe(
            ContractTenantRole.Member
        );
    }

    [Fact]
    public async Task Should_Throw_When_Adding_Nonexistent_Tenant()
    {
        var contract =
            await CreateDraftContractAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.AddTenantAsync(
                        contract.Id,
                        new AddContractTenantDto
                        {
                            TenantId = Guid.Parse(
                                "11111111-1111-1111-1111-111111111111"
                            ),
                            Role =
                                ContractTenantRole.Member
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes.TenantNotFound
        );
    }

    [Fact]
    public async Task Should_Throw_When_Tenant_Already_In_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenant =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenant.Id,
                Role = ContractTenantRole.Member
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.AddTenantAsync(
                        contract.Id,
                        new AddContractTenantDto
                        {
                            TenantId = tenant.Id,
                            Role =
                                ContractTenantRole.Member
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantAlreadyInContract
        );
    }

    [Fact]
    public async Task Should_Throw_When_Contract_Already_Has_Primary_Tenant()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenantA =
            await CreateTenantAsync();

        var tenantB =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenantA.Id,
                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.AddTenantAsync(
                        contract.Id,
                        new AddContractTenantDto
                        {
                            TenantId = tenantB.Id,
                            Role =
                                ContractTenantRole.PrimaryTenant
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractAlreadyHasPrimaryTenant
        );
    }

    [Fact]
    public async Task Should_Get_Contract_Tenants()
    {
        var contract =
            await CreateDraftContractAsync();

        var primary =
            await CreateTenantAsync();

        var member =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = primary.Id,
                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = member.Id,
                Role =
                    ContractTenantRole.Member
            }
        );

        var result =
            await _contractAppService.GetTenantsAsync(
                contract.Id
            );

        result.ShouldContain(
            x =>
                x.TenantId == primary.Id
                &&
                x.Role ==
                    ContractTenantRole.PrimaryTenant
        );

        result.ShouldContain(
            x =>
                x.TenantId == member.Id
                &&
                x.Role ==
                    ContractTenantRole.Member
        );
    }

    [Fact]
    public async Task Should_Update_Tenant_Role()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenantA =
            await CreateTenantAsync();

        var tenantB =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenantA.Id,
                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenantB.Id,
                Role =
                    ContractTenantRole.Member
            }
        );

        // A: Primary → Member
        await _contractAppService.UpdateTenantRoleAsync(
            contract.Id,
            tenantA.Id,
            new UpdateContractTenantRoleDto
            {
                Role =
                    ContractTenantRole.Member
            }
        );

        // B: Member → Primary
        var result =
            await _contractAppService.UpdateTenantRoleAsync(
                contract.Id,
                tenantB.Id,
                new UpdateContractTenantRoleDto
                {
                    Role =
                        ContractTenantRole.PrimaryTenant
                }
            );

        result.Role.ShouldBe(
            ContractTenantRole.PrimaryTenant
        );
    }

    [Fact]
    public async Task Should_Remove_Tenant_From_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenant =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenant.Id,
                Role =
                    ContractTenantRole.Member
            }
        );

        await _contractAppService.RemoveTenantAsync(
            contract.Id,
            tenant.Id
        );

        var tenants =
            await _contractAppService.GetTenantsAsync(
                contract.Id
            );

        tenants.ShouldNotContain(
            x => x.TenantId == tenant.Id
        );
    }

    [Fact]
    public async Task Should_Restore_Removed_ContractTenant_When_Adding_Again()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenant =
            await CreateTenantAsync();

        var firstRelation =
            await _contractAppService.AddTenantAsync(
                contract.Id,
                new AddContractTenantDto
                {
                    TenantId = tenant.Id,
                    Role =
                        ContractTenantRole.Member
                }
            );

        await _contractAppService.RemoveTenantAsync(
            contract.Id,
            tenant.Id
        );

        var restoredRelation =
            await _contractAppService.AddTenantAsync(
                contract.Id,
                new AddContractTenantDto
                {
                    TenantId = tenant.Id,
                    Role =
                        ContractTenantRole.PrimaryTenant
                }
            );

        restoredRelation.Id.ShouldBe(
            firstRelation.Id
        );

        restoredRelation.Role.ShouldBe(
            ContractTenantRole.PrimaryTenant
        );
    }

    //========================CL======================
    private async Task<ContractDto>
    CreateDraftContractForRoomAsync(Guid roomId)
    {
        return await _contractAppService.CreateAsync(
            new CreateContractDto
            {
                ContractNumber = NewContractNumber(),
                RoomId = roomId,
                StartDate = new DateTime(2026, 11, 1),
                EndDate = new DateTime(2027, 10, 31),
                MonthlyRent = 3_000_000,
                DepositAmount = 3_000_000
            }
        );
    }

    private async Task AddPrimaryTenantAsync(Guid contractId)
    {
        var tenant = await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contractId,
            new AddContractTenantDto
            {
                TenantId = tenant.Id,
                Role = ContractTenantRole.PrimaryTenant
            }
        );
    }

    private async Task SetRoomStatusAsync(
        Guid roomId,
        RoomStatus status)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var room =
                await _roomRepository.GetAsync(roomId);

            room.ChangeStatus(status);

            await _roomRepository.UpdateAsync(
                room,
                autoSave: true
            );
        });
    }

    private async Task<RoomStatus>
        GetRoomStatusAsync(Guid roomId)
    {
        var status = RoomStatus.Available;

        await WithUnitOfWorkAsync(async () =>
        {
            var room =
                await _roomRepository.GetAsync(roomId);

            status = room.Status;
        });

        return status;
    }

    [Fact]
    public async Task Should_Activate_Contract()
    {
        var roomId = await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(
            contract.Id
        );

        var result =
            await _contractAppService.ActivateAsync(
                contract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Active
        );

        var roomStatus =
            await GetRoomStatusAsync(roomId);

        roomStatus.ShouldBe(
            RoomStatus.Occupied
        );
    }

    [Fact]
    public async Task Should_Throw_When_Activating_Contract_Without_Tenant()
    {
        var contract =
            await CreateDraftContractAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.ActivateAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRequiresTenant
        );
    }

    [Fact]
    public async Task Should_Throw_When_Activating_Contract_Without_Primary_Tenant()
    {
        var contract =
            await CreateDraftContractAsync();

        var tenant =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenant.Id,
                Role = ContractTenantRole.Member
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.ActivateAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRequiresPrimaryTenant
        );
    }

    [Fact]
    public async Task Should_Throw_When_Room_Already_Has_Active_Contract()
    {
        var roomId = await CreateRoomAsync();

        var first =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(first.Id);

        await _contractAppService.ActivateAsync(
            first.Id
        );

        var second =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(second.Id);

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.ActivateAsync(
                        second.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomAlreadyHasActiveContract
        );
    }

    [Fact]
    public async Task Should_Throw_When_Activating_Contract_For_Maintenance_Room()
    {
        var roomId = await CreateRoomAsync();

        await SetRoomStatusAsync(
            roomId,
            RoomStatus.Maintenance
        );

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(contract.Id);

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.ActivateAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomNotAvailableForRent
        );
    }

    [Fact]
    public async Task Should_Throw_When_Activating_Contract_For_Inactive_Room()
    {
        var roomId = await CreateRoomAsync();

        await SetRoomStatusAsync(
            roomId,
            RoomStatus.Inactive
        );

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(contract.Id);

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.ActivateAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomNotAvailableForRent
        );
    }

    [Fact]
    public async Task Should_End_Active_Contract()
    {
        var roomId = await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(contract.Id);

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var result =
            await _contractAppService.EndAsync(
                contract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Ended
        );

        var roomStatus =
            await GetRoomStatusAsync(roomId);

        roomStatus.ShouldBe(
            RoomStatus.Available
        );
    }

    [Fact]
    public async Task Should_Throw_When_Ending_Draft_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.EndAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCannotBeEnded
        );
    }

    [Fact]
    public async Task Should_Cancel_Draft_Contract()
    {
        var roomId = await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        var result =
            await _contractAppService.CancelAsync(
                contract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Cancelled
        );

        var roomStatus =
            await GetRoomStatusAsync(roomId);

        roomStatus.ShouldBe(
            RoomStatus.Available
        );
    }

    [Fact]
    public async Task Should_Throw_When_Cancelling_Active_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        await AddPrimaryTenantAsync(contract.Id);

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.CancelAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCannotBeCancelled
        );
    }

    [Fact]
    public async Task Should_Not_Allow_Updating_Active_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        await AddPrimaryTenantAsync(contract.Id);

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.UpdateAsync(
                        contract.Id,
                        new UpdateContractDto
                        {
                            ContractNumber =
                                contract.ContractNumber,

                            RoomId = contract.RoomId,

                            StartDate =
                                contract.StartDate,

                            EndDate =
                                contract.EndDate,

                            MonthlyRent = 4_000_000,

                            DepositAmount =
                                contract.DepositAmount
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCanOnlyBeModifiedWhenDraft
        );
    }

    [Fact]
    public async Task Should_Not_Allow_Modifying_Tenants_Of_Active_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var primary =
            await CreateTenantAsync();

        var member =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = primary.Id,
                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = member.Id,
                Role =
                    ContractTenantRole.Member
            }
        );

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var newTenant =
            await CreateTenantAsync();

        var addException =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.AddTenantAsync(
                        contract.Id,
                        new AddContractTenantDto
                        {
                            TenantId = newTenant.Id,
                            Role =
                                ContractTenantRole.Member
                        }
                    )
            );

        addException.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCanOnlyBeModifiedWhenDraft
        );

        var updateException =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.UpdateTenantRoleAsync(
                        contract.Id,
                        member.Id,
                        new UpdateContractTenantRoleDto
                        {
                            Role =
                                ContractTenantRole.PrimaryTenant
                        }
                    )
            );

        updateException.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCanOnlyBeModifiedWhenDraft
        );

        var removeException =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RemoveTenantAsync(
                        contract.Id,
                        member.Id
                    )
            );

        removeException.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCanOnlyBeModifiedWhenDraft
        );
    }

    //=======================DR========================
    // DR1: Room có Active Contract không được xóa
    [Fact]
    public async Task Should_Not_Delete_Room_With_Active_Contract()
    {
        var roomId = await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(roomId);

        await AddPrimaryTenantAsync(contract.Id);

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _roomAppService.DeleteAsync(roomId)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomHasActiveContract
        );

        var roomStatus =
            await GetRoomStatusAsync(roomId);

        roomStatus.ShouldBe(
            RoomStatus.Occupied
        );
    }

    //DR2 — Room có lịch sử Contract thì chuyển Inactive
    [Fact]
    public async Task Should_Set_Room_Inactive_When_It_Has_Contract_History()
    {
        var roomId = await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(roomId);

        await AddPrimaryTenantAsync(contract.Id);

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        await _contractAppService.EndAsync(
            contract.Id
        );

        var beforeDelete =
            await GetRoomStatusAsync(roomId);

        beforeDelete.ShouldBe(
            RoomStatus.Available
        );

        await _roomAppService.DeleteAsync(roomId);

        var afterDelete =
            await GetRoomStatusAsync(roomId);

        afterDelete.ShouldBe(
            RoomStatus.Inactive
        );
    }

    //DR3 — Room chưa từng có Contract được soft - delete
    [Fact]
    public async Task Should_Soft_Delete_Room_Without_Contract_History()
    {
        var roomId =
            await CreateRoomAsync();

        await _roomAppService.DeleteAsync(
            roomId
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () => _roomAppService.GetAsync(roomId)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomNotFound
        );
    }

    //DT1 — Tenant có Active Contract không được xóa
    [Fact]
    public async Task Should_Not_Delete_Tenant_With_Active_Contract()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(roomId);

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

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _tenantAppService.DeleteAsync(
                        tenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantHasActiveContract
        );

        var existingTenant =
            await _tenantAppService.GetAsync(
                tenant.Id
            );

        existingTenant.Id.ShouldBe(
            tenant.Id
        );
    }

    //DT2 — Tenant có lịch sử Contract không được xóa
    [Fact]
    public async Task Should_Not_Delete_Tenant_With_Contract_History()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(roomId);

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

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        await _contractAppService.EndAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _tenantAppService.DeleteAsync(
                        tenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantHasContractHistory
        );

        var existingTenant =
            await _tenantAppService.GetAsync(
                tenant.Id
            );

        existingTenant.Id.ShouldBe(
            tenant.Id
        );
    }

    //DT3 — Tenant chưa từng có Contract được soft-delete
    [Fact]
    public async Task Should_Soft_Delete_Tenant_Without_Contract_History()
    {
        var tenant =
            await CreateTenantAsync();

        await _tenantAppService.DeleteAsync(
            tenant.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _tenantAppService.GetAsync(
                        tenant.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantNotFound
        );
    }

    // C42
    [Fact]
    public async Task Should_Sign_Draft_Contract()
    {
        var roomId = await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(
            contract.Id
        );

        var result =
            await _contractAppService.SignAsync(
                contract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Signed
        );

        var roomStatus =
            await GetRoomStatusAsync(roomId);

        roomStatus.ShouldBe(
            RoomStatus.Available
        );
    }

    // C43
    [Fact]
    public async Task
        Should_Throw_When_Signing_Contract_Without_Tenant()
    {
        var contract =
            await CreateDraftContractAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.SignAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRequiresTenant
        );
    }

    // C44
    [Fact]
    public async Task
        Should_Throw_When_Signing_Contract_Without_Primary_Tenant()
    {
        var contract =
            await CreateDraftContractAsync();

        var member =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            contract.Id,
            new AddContractTenantDto
            {
                TenantId = member.Id,
                Role = ContractTenantRole.Member
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.SignAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRequiresPrimaryTenant
        );
    }

    // C45
    [Fact]
    public async Task
        Should_Not_Sign_Contract_Twice()
    {
        var contract =
            await CreateDraftContractAsync();

        await AddPrimaryTenantAsync(
            contract.Id
        );

        await _contractAppService.SignAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.SignAsync(
                        contract.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCannotBeSigned
        );
    }

    // C46
    [Fact]
    public async Task
        Should_Activate_Signed_Contract()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        await AddPrimaryTenantAsync(
            contract.Id
        );

        await _contractAppService.SignAsync(
            contract.Id
        );

        var result =
            await _contractAppService.ActivateAsync(
                contract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Active
        );

        var roomStatus =
            await GetRoomStatusAsync(roomId);

        roomStatus.ShouldBe(
            RoomStatus.Occupied
        );
    }

    // C47
    [Fact]
    public async Task
        Should_Cancel_Signed_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        await AddPrimaryTenantAsync(
            contract.Id
        );

        await _contractAppService.SignAsync(
            contract.Id
        );

        var result =
            await _contractAppService.CancelAsync(
                contract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Cancelled
        );
    }

    [Fact]
    public async Task Should_Renew_Active_Contract_And_Copy_Tenants()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateDraftContractForRoomAsync(
                roomId
            );

        var primary =
            await CreateTenantAsync();

        var member =
            await CreateTenantAsync();

        await _contractAppService.AddTenantAsync(
            currentContract.Id,
            new AddContractTenantDto
            {
                TenantId = primary.Id,
                Role =
                    ContractTenantRole.PrimaryTenant
            }
        );

        await _contractAppService.AddTenantAsync(
            currentContract.Id,
            new AddContractTenantDto
            {
                TenantId = member.Id,
                Role =
                    ContractTenantRole.Member
            }
        );

        await _contractAppService.ActivateAsync(
            currentContract.Id
        );

        // Bắt buộc phải có Renewal Hold trước khi Renew
        var renewalHold =
            await CreateRenewalHoldAsync(
                currentContract.Id
            );

        var renewed =
            await _contractAppService.RenewAsync(
                currentContract.Id,
                new RenewContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    StartDate =
                        new DateTime(2027, 11, 1),

                    EndDate =
                        new DateTime(2028, 10, 31),

                    MonthlyRent = 3_500_000,

                    DepositAmount = 3_500_000,

                    Notes = "Renewed contract"
                }
            );

        // Contract mới phải ở trạng thái Signed
        renewed.Status.ShouldBe(
            ContractStatus.Signed
        );

        // Vẫn phải thuộc cùng Room
        renewed.RoomId.ShouldBe(
            currentContract.RoomId
        );

        // Contract mới phải liên kết về Contract cũ
        renewed.RenewedFromContractId.ShouldBe(
            currentContract.Id
        );

        // Contract mới phải là record khác
        renewed.Id.ShouldNotBe(
            currentContract.Id
        );

        // Tenant phải được copy sang Contract mới
        var renewedTenants =
            await _contractAppService
                .GetTenantsAsync(
                    renewed.Id
                );

        renewedTenants.ShouldContain(
            x =>
                x.TenantId == primary.Id
                &&
                x.Role ==
                    ContractTenantRole.PrimaryTenant
        );

        renewedTenants.ShouldContain(
            x =>
                x.TenantId == member.Id
                &&
                x.Role ==
                    ContractTenantRole.Member
        );

        renewedTenants.Count.ShouldBe(2);

        // Renewal Hold phải tự động được hoàn thành
        var completedHold =
            await _renewalHoldAppService.GetAsync(
                renewalHold.Id
            );

        completedHold.Status.ShouldBe(
            ContractRenewalHoldStatus.Completed
        );

        // Hold phải trỏ tới đúng Contract mới
        completedHold.CompletedContractId.ShouldBe(
            renewed.Id
        );
    }

    // C49
    [Fact]
    public async Task
        Should_Not_Renew_Draft_Contract()
    {
        var contract =
            await CreateDraftContractAsync();

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        contract.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_000_000,
                            DepositAmount = 3_000_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCannotBeRenewed
        );
    }

    // C50
    [Fact]
    public async Task
        Should_Not_Renew_Contract_Without_EndDate()
    {
        var roomId =
            await CreateRoomAsync();

        var contract =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId = roomId,

                    StartDate =
                        new DateTime(2026, 11, 1),

                    EndDate = null,

                    MonthlyRent = 3_000_000,
                    DepositAmount = 3_000_000
                }
            );

        await AddPrimaryTenantAsync(
            contract.Id
        );

        await _contractAppService.ActivateAsync(
            contract.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        contract.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_000_000,
                            DepositAmount = 3_000_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractCannotBeRenewed
        );
    }

    // C51
    [Fact]
    public async Task
        Should_Throw_When_Renewal_StartDate_Is_Not_After_Current_EndDate()
    {
        var roomId =
            await CreateRoomAsync();

        var current =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        current.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    10,
                                    31
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_500_000,
                            DepositAmount = 3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidRenewalContractPeriod
        );
    }

    // C52
    [Fact]
    public async Task
        Should_Not_Renew_Same_Contract_Twice()
    {
        var roomId =
            await CreateRoomAsync();

        var current =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        await CreateRenewalHoldAsync(current.Id);

        await _contractAppService.RenewAsync(
            current.Id,
            new RenewContractDto
            {
                ContractNumber =
                    NewContractNumber(),

                StartDate =
                    new DateTime(2027, 11, 1),

                EndDate =
                    new DateTime(2028, 10, 31),

                MonthlyRent = 3_500_000,
                DepositAmount = 3_500_000
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        current.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_500_000,
                            DepositAmount = 3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractAlreadyRenewed
        );
    }

    // C53
    [Fact]
    public async Task
        Should_Not_Sign_Overlapping_Committed_Contract()
    {
        var roomId =
            await CreateRoomAsync();

        await CreateActiveContractForRoomAsync(
            roomId
        );

        var second =
            await _contractAppService.CreateAsync(
                new CreateContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    RoomId = roomId,

                    StartDate =
                        new DateTime(2027, 1, 1),

                    EndDate =
                        new DateTime(2027, 12, 31),

                    MonthlyRent = 3_500_000,
                    DepositAmount = 3_500_000
                }
            );

        await AddPrimaryTenantAsync(
            second.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.SignAsync(
                        second.Id
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomHasOverlappingCommittedContract
        );
    }

    // C54
    [Fact]
    public async Task
        Should_Not_Renew_Without_Renewal_Hold()
    {
        var roomId =
            await CreateRoomAsync();

        var current =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        current.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_500_000,
                            DepositAmount = 3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldRequired
        );
    }

    // C55
    [Fact]
    public async Task
        Should_Not_Renew_After_Renewal_Hold_Is_Cancelled()
    {
        var roomId =
            await CreateRoomAsync();

        var current =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var hold =
            await CreateRenewalHoldAsync(
                current.Id
            );

        await _renewalHoldAppService.CancelAsync(
            hold.Id
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        current.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_500_000,
                            DepositAmount = 3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldNotActive
        );
    }

    // C56
    [Fact]
    public async Task Should_Not_Renew_After_Renewal_Hold_Expires()
    {
        var roomId =
            await CreateRoomAsync();

        var current =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var expiredHold =
            new ContractRenewalHold(
                Guid.NewGuid(),
                current.Id,
                current.RoomId,
                _clock.Now.AddHours(-25),
                "Expired renewal hold"
            );

        await WithUnitOfWorkAsync(
            async () =>
            {
                await _renewalHoldRepository.InsertAsync(
                    expiredHold,
                    autoSave: true
                );
            }
        );

        var exception =
            await Assert.ThrowsAsync<BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        current.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent = 3_500_000,
                            DepositAmount = 3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .ContractRenewalHoldExpired
        );
    }

    private async Task<RoomReservationDto> CreatePendingReservationForRoomAsync(
        Guid roomId,
        DateTime expectedMoveInDate)
    {
        var tenant =
            await CreateTenantAsync();

        return await _roomReservationAppService
            .CreateAsync(
                new CreateRoomReservationDto
                {
                    ReservationNumber =
                        $"RSV-CONTRACT-{Guid.NewGuid():N}",

                    RoomId = roomId,

                    TenantId = tenant.Id,

                    ExpectedMoveInDate =
                        expectedMoveInDate,

                    QuotedMonthlyRent =
                        3_000_000,

                    RequiredDepositAmount =
                        3_000_000,

                    Notes =
                        "Contract renewal conflict test"
                }
            );
    }

    private async Task MarkReservationAsReservedAsync(
        Guid reservationId)
    {
        await WithUnitOfWorkAsync(
            async () =>
            {
                var reservation =
                    await _roomReservationRepository
                        .GetAsync(
                            reservationId
                        );

                reservation.MarkReserved(
                    _clock.Now,
                    _clock.Now.Date.AddDays(30)
                );

                await _roomReservationRepository
                    .UpdateAsync(
                        reservation,
                        autoSave: true
                    );
            }
        );
    }

    //C57 — B chưa cọc, A renew đè lên → B bị hủy
    [Fact]
    public async Task
        Should_Cancel_Conflicting_Pending_Reservation_When_Contract_Is_Renewed()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var pendingReservation =
            await CreatePendingReservationForRoomAsync(
                roomId,
                new DateTime(2027, 11, 1)
            );

        await CreateRenewalHoldAsync(
            currentContract.Id
        );

        var renewed =
            await _contractAppService.RenewAsync(
                currentContract.Id,
                new RenewContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    StartDate =
                        new DateTime(2027, 11, 1),

                    EndDate =
                        new DateTime(2028, 10, 31),

                    MonthlyRent =
                        3_500_000,

                    DepositAmount =
                        3_500_000
                }
            );

        renewed.Status.ShouldBe(
            ContractStatus.Signed
        );

        var reservationAfterRenewal =
            await _roomReservationAppService
                .GetAsync(
                    pendingReservation.Id
                );

        reservationAfterRenewal.Status.ShouldBe(
            RoomReservationStatus.Cancelled
        );

        reservationAfterRenewal
            .CancellationReason
            .ShouldBe(
                ReservationCancellationReason
                    .SupersededByRenewal
            );

        reservationAfterRenewal
            .CancelledAt
            .ShouldNotBeNull();
    }

    //C58 — B vào sau khi renewal kết thúc → không hủy B
    // C58
    [Fact]
    public async Task
        Should_Keep_Pending_Reservation_When_Move_In_Is_After_Renewal_End_Date()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var pendingReservation =
            await CreatePendingReservationForRoomAsync(
                roomId,
                new DateTime(2028, 11, 1)
            );

        await CreateRenewalHoldAsync(
            currentContract.Id
        );

        await _contractAppService.RenewAsync(
            currentContract.Id,
            new RenewContractDto
            {
                ContractNumber =
                    NewContractNumber(),

                StartDate =
                    new DateTime(2027, 11, 1),

                EndDate =
                    new DateTime(2028, 10, 31),

                MonthlyRent =
                    3_500_000,

                DepositAmount =
                    3_500_000
            }
        );

        var reservationAfterRenewal =
            await _roomReservationAppService
                .GetAsync(
                    pendingReservation.Id
                );

        reservationAfterRenewal.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );

        reservationAfterRenewal
            .CancellationReason
            .ShouldBeNull();

        reservationAfterRenewal
            .CancelledAt
            .ShouldBeNull();
    }

    //C59 — B đã cọc và bị overlap → A không được renew
    // C59
    [Fact]
    public async Task
        Should_Not_Renew_When_Conflicting_Reservation_Is_Already_Reserved()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var reservation =
            await CreatePendingReservationForRoomAsync(
                roomId,
                new DateTime(2027, 11, 1)
            );

        await MarkReservationAsReservedAsync(
            reservation.Id
        );

        var renewalHold =
            await CreateRenewalHoldAsync(
                currentContract.Id
            );

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _contractAppService.RenewAsync(
                        currentContract.Id,
                        new RenewContractDto
                        {
                            ContractNumber =
                                NewContractNumber(),

                            StartDate =
                                new DateTime(
                                    2027,
                                    11,
                                    1
                                ),

                            EndDate =
                                new DateTime(
                                    2028,
                                    10,
                                    31
                                ),

                            MonthlyRent =
                                3_500_000,

                            DepositAmount =
                                3_500_000
                        }
                    )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .RoomAlreadyReserved
        );

        var reservationAfterFailure =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterFailure.Status.ShouldBe(
            RoomReservationStatus.Reserved
        );

        var holdAfterFailure =
            await _renewalHoldAppService
                .GetAsync(
                    renewalHold.Id
                );

        holdAfterFailure.Status.ShouldBe(
            ContractRenewalHoldStatus.Active
        );

        holdAfterFailure
            .CompletedContractId
            .ShouldBeNull();
    }

    //C60 — B đã cọc nhưng ngày vào sau A hết renewal → A vẫn được renew
    // C60
    [Fact]
    public async Task
        Should_Allow_Renewal_When_Reserved_Move_In_Is_After_Renewal_End_Date()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var reservation =
            await CreatePendingReservationForRoomAsync(
                roomId,
                new DateTime(2028, 11, 1)
            );

        await MarkReservationAsReservedAsync(
            reservation.Id
        );

        var renewalHold =
            await CreateRenewalHoldAsync(
                currentContract.Id
            );

        var renewed =
            await _contractAppService.RenewAsync(
                currentContract.Id,
                new RenewContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    StartDate =
                        new DateTime(
                            2027,
                            11,
                            1
                        ),

                    EndDate =
                        new DateTime(
                            2028,
                            10,
                            31
                        ),

                    MonthlyRent =
                        3_500_000,

                    DepositAmount =
                        3_500_000
                }
            );

        renewed.Status.ShouldBe(
            ContractStatus.Signed
        );

        var reservationAfterRenewal =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterRenewal.Status.ShouldBe(
            RoomReservationStatus.Reserved
        );

        reservationAfterRenewal
            .CancellationReason
            .ShouldBeNull();

        var completedHold =
            await _renewalHoldAppService
                .GetAsync(
                    renewalHold.Id
                );

        completedHold.Status.ShouldBe(
            ContractRenewalHoldStatus.Completed
        );

        completedHold.CompletedContractId.ShouldBe(
            renewed.Id
        );
    }

    //C61 — B đã cọc thì khi A rời phòng phải thành Reserved
    // C61
    [Fact]
    public async Task
        Should_Set_Room_Reserved_When_Active_Contract_Ends_With_Reserved_Reservation()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var reservation =
            await CreatePendingReservationForRoomAsync(
                roomId,
                new DateTime(2027, 11, 1)
            );

        await MarkReservationAsReservedAsync(
            reservation.Id
        );

        var result =
            await _contractAppService.EndAsync(
                currentContract.Id
            );

        result.Status.ShouldBe(
            ContractStatus.Ended
        );

        var roomStatus =
            await GetRoomStatusAsync(
                roomId
            );

        roomStatus.ShouldBe(
            RoomStatus.Reserved
        );

        var reservationAfterEnd =
            await _roomReservationAppService
                .GetAsync(
                    reservation.Id
                );

        reservationAfterEnd.Status.ShouldBe(
            RoomReservationStatus.Reserved
        );
    }

    //C62 — B chỉ PendingPayment thì phòng phải Available
    // C62
    [Fact]
    public async Task
        Should_Set_Room_Available_When_Active_Contract_Ends_With_Only_Pending_Reservation()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        var pendingReservation =
            await CreatePendingReservationForRoomAsync(
                roomId,
                new DateTime(2027, 11, 1)
            );

        await _contractAppService.EndAsync(
            currentContract.Id
        );

        var roomStatus =
            await GetRoomStatusAsync(
                roomId
            );

        roomStatus.ShouldBe(
            RoomStatus.Available
        );

        var reservationAfterEnd =
            await _roomReservationAppService
                .GetAsync(
                    pendingReservation.Id
                );

        reservationAfterEnd.Status.ShouldBe(
            RoomReservationStatus.PendingPayment
        );
    }

    //C63 — A đã ký renewal thì khi Contract cũ end, phòng cũng phải Reserved
    // C63
    [Fact]
    public async Task
        Should_Set_Room_Reserved_When_Active_Contract_Ends_With_Signed_Renewal()
    {
        var roomId =
            await CreateRoomAsync();

        var currentContract =
            await CreateActiveContractForRoomAsync(
                roomId
            );

        await CreateRenewalHoldAsync(
            currentContract.Id
        );

        var renewed =
            await _contractAppService.RenewAsync(
                currentContract.Id,
                new RenewContractDto
                {
                    ContractNumber =
                        NewContractNumber(),

                    StartDate =
                        new DateTime(
                            2027,
                            11,
                            1
                        ),

                    EndDate =
                        new DateTime(
                            2028,
                            10,
                            31
                        ),

                    MonthlyRent =
                        3_500_000,

                    DepositAmount =
                        3_500_000
                }
            );

        renewed.Status.ShouldBe(
            ContractStatus.Signed
        );

        await _contractAppService.EndAsync(
            currentContract.Id
        );

        var roomStatus =
            await GetRoomStatusAsync(
                roomId
            );

        roomStatus.ShouldBe(
            RoomStatus.Reserved
        );

        var renewedAfterEnd =
            await _contractAppService.GetAsync(
                renewed.Id
            );

        renewedAfterEnd.Status.ShouldBe(
            ContractStatus.Signed
        );
    }
}