using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.MoveOuts;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class MoveOutRequestConcurrency_Tests
    : BuildingManagementApplicationTestBase<BuildingManagementPostgreSqlTestModule>
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IContractAppService _contracts;
    private readonly ITenantAppService _tenants;
    private readonly IMoveOutRequestAppService _requests;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;

    public MoveOutRequestConcurrency_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IContractAppService>();
        _tenants = GetRequiredService<ITenantAppService>();
        _requests = GetRequiredService<IMoveOutRequestAppService>();
        _scopeFactory = GetRequiredService<IServiceScopeFactory>();
        _clock = GetRequiredService<IClock>();
    }

    [Fact]
    public async Task MOUT_PG01_Two_Concurrent_Submissions_Only_One_Open_Request()
    {
        var contractId = await MakeActiveContractAsync();
        var date = MoveOutNoticePolicy.EarliestPermittedMoveOutDate(
            MoveOutNoticePolicy.VietnamDate(_clock.Now));

        var first = Task.Run(() => SubmitInScopeAsync(contractId, date));
        var second = Task.Run(() => SubmitInScopeAsync(contractId, date));
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(35));

        outcomes.Count(x => x is null).ShouldBe(1);
        outcomes.Count(x => x is not null).ShouldBe(1);

        var loser = outcomes.Single(x => x is not null)!;
        if (loser is BusinessException businessError)
        {
            businessError.Code.ShouldBe(MoveOutErrorCodes.OpenRequestAlreadyExists);
        }
        else
        {
            // A direct DB unique violation is also a valid transaction loser.
            HasUniqueViolation(loser).ShouldBeTrue(
                "Concurrent losers must fail due to the open-request invariant.");
        }

        var list = await _requests.GetByContractAsync(contractId);
        list.Count(x => x.Status == MoveOutRequestStatus.Pending ||
                        x.Status == MoveOutRequestStatus.Approved).ShouldBe(1);
    }

    private async Task<Exception?> SubmitInScopeAsync(Guid contractId, DateOnly moveOutDate)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMoveOutRequestAppService>();
            await service.CreateAsync(new CreateMoveOutRequestDto
            {
                ContractId = contractId,
                RequestedMoveOutDate = moveOutDate,
                Reason = "Concurrent submit test"
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static bool HasUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException p && p.SqlState == PostgresErrorCodes.UniqueViolation)
                return true;
        return false;
    }

    private async Task<Guid> MakeActiveContractAsync()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(buildingId, $"MOUT-PG-{Guid.NewGuid():N}", "Address"), autoSave: true);
            await _floors.InsertAsync(new Floor(floorId, buildingId, 1, "Floor 1"), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomId, floorId, "M201", "MOUT PG Room", 25, 2, RoomStatus.Available),
                autoSave: true);
        });

        var contract = await _contracts.CreateAsync(new CreateContractDto
        {
            ContractNumber = $"MOUT-PG-C-{Guid.NewGuid():N}",
            RoomId = roomId,
            StartDate = _clock.Now.Date.AddDays(-10),
            EndDate = _clock.Now.Date.AddMonths(6),
            MonthlyRent = 3_000_000m
        });

        var tenantNumber = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        var tenant = await _tenants.CreateAsync(new CreateTenantDto
        {
            FullName = $"MOUT tenant {Guid.NewGuid():N}",
            PhoneNumber = $"09{tenantNumber % 100_000_000UL:D8}",
            IdentityType = IdentityType.CitizenId,
            IdentityNumber = $"079{tenantNumber % 1_000_000_000UL:D9}"
        });
        await _contracts.AddTenantAsync(contract.Id,
            new AddContractTenantDto
            {
                TenantId = tenant.Id,
                Role = ContractTenantRole.PrimaryTenant
            });
        await _contracts.ActivateAsync(contract.Id);
        return contract.Id;
    }
}
