using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Verifies the entire design-only read chain (simulated trusted actor,
/// permission, active identity and grants -> REAL scoped EF SQL store)
/// against isolated ABP SQLite repositories. This does NOT exercise
/// deployed Identity, operator grant persistence or public HTTP APIs.
/// </summary>
public sealed class BuildingScopedReadSqliteComposition_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;

    public BuildingScopedReadSqliteComposition_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
    }

    [Fact]
    public async Task READSQL01_Granted_User_Sees_Only_Their_Rooms_And_SQL_Count()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var fixture = Context(f.A);
            var result = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 10);

            Assert.Equal(BuildingScopedReadDesignStatus.EligibleForDesignOnly,
                result.Status);
            Assert.Equal(2, result.Page.TotalCount);
            Assert.Equal(new[] { f.RoomA1, f.RoomA2 }.OrderBy(x => x),
                result.Page.ResourceIds.OrderBy(x => x));
            Assert.DoesNotContain(f.RoomB, result.Page.ResourceIds);
            Assert.Equal(BuildingBusinessCapability.ReadRoom,
                fixture.Principal.LastCapability);
        });
    }

    [Fact]
    public async Task READSQL02_Grant_Switches_A_To_B_Without_Leaking_Former_Rows()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var fixture = Context(f.A);
            var a = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Contract, 0, 10);
            Assert.Equal(2, a.Page.TotalCount);
            Assert.DoesNotContain(f.ContractB, a.Page.ResourceIds);

            fixture.Grants.Ids = new[] { f.B };
            var b = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Contract, 0, 10);
            Assert.Equal(BuildingScopedReadDesignStatus.EligibleForDesignOnly,
                b.Status);
            Assert.Equal(1, b.Page.TotalCount);
            Assert.Equal(new[] { f.ContractB }, b.Page.ResourceIds);
            Assert.DoesNotContain(f.ContractA1, b.Page.ResourceIds);
            Assert.DoesNotContain(f.ContractA2, b.Page.ResourceIds);
            Assert.Equal(2, fixture.Grants.Calls);
        });
    }

    [Fact]
    public async Task READSQL03_Disabled_Or_Unpermitted_User_Cannot_Query_EF()
    {
        await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var fixture = Context(Guid.NewGuid());

            fixture.Principal.PermissionGranted = false;
            var denied = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 10);
            AssertDenied(denied);
            Assert.Equal(0, fixture.Grants.Calls);

            fixture.Principal.PermissionGranted = true;
            fixture.Account.Active = false;
            denied = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Contract, 0, 10);
            AssertDenied(denied);
            Assert.Equal(0, fixture.Grants.Calls);
        });
    }

    [Fact]
    public async Task READSQL04_After_Revocation_Next_SQL_Read_Is_Denied()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var fixture = Context(f.A);
            var initial = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 10);
            Assert.Equal(2, initial.Page.TotalCount);

            fixture.Grants.Ids = Array.Empty<Guid>();
            var afterRevocation = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 10);

            AssertDenied(afterRevocation);
            Assert.Equal(2, fixture.Grants.Calls);
        });
    }

    [Fact]
    public async Task READSQL05_True_Scoped_Pagination_Excludes_Forbidden_Room()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var fixture = Context(f.A);
            var first = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 1);
            var second = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 1, 1);

            Assert.Equal(2, first.Page.TotalCount);
            Assert.Equal(2, second.Page.TotalCount);
            Assert.Single(first.Page.ResourceIds);
            Assert.Single(second.Page.ResourceIds);
            Assert.NotEqual(first.Page.ResourceIds[0],
                second.Page.ResourceIds[0]);
            Assert.Equal(new[] { f.RoomA1, f.RoomA2 }.OrderBy(x => x),
                first.Page.ResourceIds.Concat(second.Page.ResourceIds)
                    .OrderBy(x => x));
            Assert.DoesNotContain(f.RoomB, first.Page.ResourceIds);
            Assert.DoesNotContain(f.RoomB, second.Page.ResourceIds);
        });
    }

    [Fact]
    public async Task READSQL06_Soft_Deleted_Building_Produces_No_Nested_Records()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
            await _buildings.DeleteAsync(f.A, autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            var fixture = Context(f.A);
            var result = await fixture.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 10);
            Assert.Equal(BuildingScopedReadDesignStatus.EligibleForDesignOnly,
                result.Status); // only design eligibility; no real rows
            Assert.Equal(0, result.Page.TotalCount);
            Assert.Empty(result.Page.ResourceIds);
        });
    }

    private FixtureContext Context(Guid building)
    {
        var userId = Guid.NewGuid();
        var principal = new FakePrincipal(userId);
        var account = new FakeAccount();
        var grants = new FakeGrants(new[] { building });
        var gate = new BuildingScopedReadDesignGate(principal, account, grants,
            GetRequiredService<IBuildingScopedCollectionStore>());
        return new FixtureContext(principal, account, grants, gate);
    }

    private static void AssertDenied(BuildingScopedReadDesignResult result)
    {
        Assert.Equal(BuildingScopedReadDesignStatus.Denied, result.Status);
        Assert.Equal(0, result.Page.TotalCount);
        Assert.Empty(result.Page.ResourceIds);
    }

    private sealed record FixtureContext(
        FakePrincipal Principal, FakeAccount Account,
        FakeGrants Grants, BuildingScopedReadDesignGate Gate);

    private sealed class FakePrincipal : IBuildingBusinessPrincipalReader
    {
        private readonly Guid _userId;
        public FakePrincipal(Guid userId) => _userId = userId;
        public bool PermissionGranted { get; set; } = true;
        public BuildingBusinessCapability LastCapability { get; private set; }

        public Task<BuildingBusinessPrincipal?> ReadAsync(
            BuildingBusinessCapability capability)
        {
            LastCapability = capability;
            return Task.FromResult<BuildingBusinessPrincipal?>(
                new BuildingBusinessPrincipal(_userId, PermissionGranted));
        }
    }

    private sealed class FakeAccount : IBuildingOperatorActiveAccountReader
    {
        public bool Active { get; set; } = true;
        public Task<bool> IsActiveAsync(Guid userId)
            => Task.FromResult(Active);
    }

    private sealed class FakeGrants : IBuildingOperatorGrantReader
    {
        public IReadOnlyCollection<Guid> Ids { get; set; }
        public int Calls { get; private set; }

        public FakeGrants(IReadOnlyCollection<Guid> ids) => Ids = ids;

        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(
            Guid operatorId)
        {
            Calls++;
            return Task.FromResult(Ids);
        }
    }

    private async Task<Seed> SeedAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var floorA = Guid.NewGuid();
        var floorB = Guid.NewGuid();
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        var contractA1 = Guid.NewGuid();
        var contractA2 = Guid.NewGuid();
        var contractB = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(new Building(a, "Allowed", "SQLite A"),
                autoSave: true);
            await _buildings.InsertAsync(new Building(b, "Forbidden", "SQLite B"),
                autoSave: true);
            await _floors.InsertAsync(new Floor(floorA, a, 1, "Floor A"),
                autoSave: true);
            await _floors.InsertAsync(new Floor(floorB, b, 1, "Floor B"),
                autoSave: true);
            await _rooms.InsertAsync(new Room(a1, floorA, "201", "A1", 20m, 2,
                RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(new Room(a2, floorA, "202", "A2", 20m, 2,
                RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(new Room(b1, floorB, "101", "B1", 20m, 2,
                RoomStatus.Available), autoSave: true);

            await _contracts.InsertAsync(new Contract(contractA1,
                "READSQL-A1-" + Guid.NewGuid().ToString("N"), a1,
                new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
            await _contracts.InsertAsync(new Contract(contractA2,
                "READSQL-A2-" + Guid.NewGuid().ToString("N"), a2,
                new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
            await _contracts.InsertAsync(new Contract(contractB,
                "READSQL-B1-" + Guid.NewGuid().ToString("N"), b1,
                new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
        });

        return new Seed(a, b, a1, a2, b1,
            contractA1, contractA2, contractB);
    }

    private sealed record Seed(Guid A, Guid B, Guid RoomA1, Guid RoomA2,
        Guid RoomB, Guid ContractA1, Guid ContractA2, Guid ContractB);
}
