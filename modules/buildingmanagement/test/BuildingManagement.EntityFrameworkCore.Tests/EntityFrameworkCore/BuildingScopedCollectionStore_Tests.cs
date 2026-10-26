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
/// Internal EF scoped collection store exercised on isolated SQLite ABP
/// fixtures only. Does NOT exercise real ABP identity, real operator
/// membership, public CRUD endpoints, or PostgreSQL.
/// </summary>
public sealed class BuildingScopedCollectionStore_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;

    public BuildingScopedCollectionStore_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
    }

    [Fact]
    public async Task STOREPAGE01_All_Four_Types_Return_Only_Matching_Building()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var store = GetRequiredService<IBuildingScopedCollectionStore>();

            foreach (var (kind, expectedIds) in new[]
            {
                (BuildingScopedCollectionKind.Building, new[] { f.A }),
                (BuildingScopedCollectionKind.Floor, new[] { f.FloorA }),
                (BuildingScopedCollectionKind.Room, new[] { f.RoomA1, f.RoomA2 }),
                (BuildingScopedCollectionKind.Contract, new[] { f.ContractA1, f.ContractA2 })
            })
            {
                var page = await store.ReadPageAsync(kind, new[] { f.A }, 0, 10);
                Assert.Equal(expectedIds.Length, page.TotalCount);
                Assert.Equal(
                    expectedIds.OrderBy(id => id).ToArray(),
                    page.ResourceIds.OrderBy(id => id).ToArray());
            }
        });
    }

    [Fact]
    public async Task STOREPAGE02_Empty_Grant_Returns_Zero_Count_And_No_Ids()
    {
        await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var store = GetRequiredService<IBuildingScopedCollectionStore>();
            foreach (var kind in new[]
            {
                BuildingScopedCollectionKind.Building,
                BuildingScopedCollectionKind.Floor,
                BuildingScopedCollectionKind.Room,
                BuildingScopedCollectionKind.Contract
            })
            {
                var result = await store.ReadPageAsync(
                    kind, Array.Empty<Guid>(), 0, 10);
                Assert.Equal(0, result.TotalCount);
                Assert.Empty(result.ResourceIds);
            }
        });
    }

    [Fact]
    public async Task STOREPAGE03_SQL_Scopes_Before_Count_And_Pagination()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var store = GetRequiredService<IBuildingScopedCollectionStore>();
            var first = await store.ReadPageAsync(
                BuildingScopedCollectionKind.Room, new[] { f.A }, 0, 1);
            var second = await store.ReadPageAsync(
                BuildingScopedCollectionKind.Room, new[] { f.A }, 1, 1);

            Assert.Equal(2, first.TotalCount);
            Assert.Equal(2, second.TotalCount);
            Assert.Single(first.ResourceIds);
            Assert.Single(second.ResourceIds);
            Assert.NotEqual(first.ResourceIds[0], second.ResourceIds[0]);
            // SQLite and .NET may order GUID byte representations
            // differently; verify stable non-overlapping SQL pages,
            // not an in-memory ordering that could make this test flaky.
            Assert.Equal(
                new[] { f.RoomA1, f.RoomA2 }.OrderBy(id => id).ToArray(),
                first.ResourceIds.Concat(second.ResourceIds)
                    .OrderBy(id => id).ToArray());
            Assert.DoesNotContain(f.RoomB, first.ResourceIds);
            Assert.DoesNotContain(f.RoomB, second.ResourceIds);
        });
    }

    [Fact]
    public async Task STOREPAGE04_Invalid_Paging_Or_Collection_Refused()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var store = GetRequiredService<IBuildingScopedCollectionStore>();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => store.ReadPageAsync(
                    BuildingScopedCollectionKind.Room, Array.Empty<Guid>(), -1, 1));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => store.ReadPageAsync(
                    BuildingScopedCollectionKind.Room, Array.Empty<Guid>(), 0, 101));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => store.ReadPageAsync(
                    (BuildingScopedCollectionKind)999, Array.Empty<Guid>(), 0, 10));
        });
    }

    [Fact]
    public async Task STOREPAGE05_Deleted_Building_Hides_Its_Existing_Contract()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
            await _buildings.DeleteAsync(f.A, autoSave: true));
        await WithUnitOfWorkAsync(async () =>
        {
            var store = GetRequiredService<IBuildingScopedCollectionStore>();
            var result = await store.ReadPageAsync(
                BuildingScopedCollectionKind.Contract, new[] { f.A }, 0, 10);
            Assert.Equal(0, result.TotalCount);
            Assert.Empty(result.ResourceIds);
        });
    }

    private async Task<Fixture> SeedAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var floorA = Guid.NewGuid();
        var floorB = Guid.NewGuid();
        var roomA1 = Guid.NewGuid();
        var roomA2 = Guid.NewGuid();
        var roomB = Guid.NewGuid();
        var contractA1 = Guid.NewGuid();
        var contractA2 = Guid.NewGuid();
        var contractB = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(a, "Authorized", "Test A"), autoSave: true);
            await _buildings.InsertAsync(
                new Building(b, "Unauthorized", "Test B"), autoSave: true);
            await _floors.InsertAsync(
                new Floor(floorA, a, 1, "Floor A"), autoSave: true);
            await _floors.InsertAsync(
                new Floor(floorB, b, 1, "Floor B"), autoSave: true);

            await _rooms.InsertAsync(
                new Room(roomA1, floorA, "101", "Room A1", 20m, 2,
                    RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomA2, floorA, "102", "Room A2", 20m, 2,
                    RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomB, floorB, "101", "Room B", 20m, 2,
                    RoomStatus.Available), autoSave: true);

            await _contracts.InsertAsync(new Contract(contractA1,
                "TEST-A1-" + Guid.NewGuid().ToString("N"), roomA1,
                new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
            await _contracts.InsertAsync(new Contract(contractA2,
                "TEST-A2-" + Guid.NewGuid().ToString("N"), roomA2,
                new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
            await _contracts.InsertAsync(new Contract(contractB,
                "TEST-B-" + Guid.NewGuid().ToString("N"), roomB,
                new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
        });
        return new Fixture(a, floorA, roomA1, roomA2, roomB,
            contractA1, contractA2);
    }

    private sealed record Fixture(Guid A, Guid FloorA,
        Guid RoomA1, Guid RoomA2, Guid RoomB,
        Guid ContractA1, Guid ContractA2);
}
