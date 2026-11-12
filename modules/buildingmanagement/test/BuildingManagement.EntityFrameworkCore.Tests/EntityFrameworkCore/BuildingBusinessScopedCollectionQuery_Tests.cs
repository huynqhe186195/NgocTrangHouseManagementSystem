using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.EntityFrameworkCore.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Real module repositories on the isolated ABP SQLite test database.
/// These are query-composition tests, NOT authenticated HTTP tests and
/// NOT an authorization check in any currently exposed business endpoint.
/// </summary>
public sealed class BuildingBusinessScopedCollectionQuery_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;

    public BuildingBusinessScopedCollectionQuery_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
    }

    [Fact]
    public async Task LISTSCOPE01_Building_And_Floor_Collections_Exclude_Other_Buildings()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var b = await _buildings.GetQueryableAsync();
            var floors = await _floors.GetQueryableAsync();
            var allowed = new[] { f.A };

            Assert.Equal(new[] { f.A },
                await BuildingBusinessScopedCollectionQueries
                    .Buildings(b, allowed).Select(x => x.Id).ToArrayAsync());
            Assert.Equal(new[] { f.FloorA },
                await BuildingBusinessScopedCollectionQueries
                    .Floors(floors, b, allowed).Select(x => x.Id).ToArrayAsync());
        });
    }

    [Fact]
    public async Task LISTSCOPE02_Room_Filter_Happens_Before_Count_And_Paging()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var q = BuildingBusinessScopedCollectionQueries.Rooms(
                await _rooms.GetQueryableAsync(),
                await _floors.GetQueryableAsync(),
                await _buildings.GetQueryableAsync(),
                new[] { f.A });

            // Unauthorized room sorts ahead of the allowed rooms. If someone
            // pages first, the first page would be wrong and count would leak.
            Assert.Equal(2, await q.CountAsync());
            var page = await q.OrderBy(x => x.RoomNumber)
                .Skip(1).Take(1).Select(x => x.Id).ToArrayAsync();
            Assert.Equal(new[] { f.RoomA2 }, page);
        });
    }

    [Fact]
    public async Task LISTSCOPE03_Contract_Collections_Filter_By_Actual_Room_Floor_Building()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var q = BuildingBusinessScopedCollectionQueries.Contracts(
                await _contracts.GetQueryableAsync(),
                await _rooms.GetQueryableAsync(),
                await _floors.GetQueryableAsync(),
                await _buildings.GetQueryableAsync(),
                new[] { f.A });
            Assert.Equal(2, await q.CountAsync());
            var ids = await q.Select(x => x.Id).ToArrayAsync();
            Assert.Contains(f.ContractA1, ids);
            Assert.Contains(f.ContractA2, ids);
            Assert.DoesNotContain(f.ContractB, ids);
        });
    }

    [Fact]
    public async Task LISTSCOPE04_No_Grant_Produces_Zero_Rows_In_Every_Collection()
    {
        await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var b = await _buildings.GetQueryableAsync();
            var floors = await _floors.GetQueryableAsync();
            var rooms = await _rooms.GetQueryableAsync();
            var contracts = await _contracts.GetQueryableAsync();

            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Buildings(b, null).ToArrayAsync());
            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Floors(floors, b, Array.Empty<Guid>()).ToArrayAsync());
            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Rooms(rooms, floors, b, new[] { Guid.Empty }).ToArrayAsync());
            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Contracts(contracts, rooms, floors, b,
                    new[] { Guid.NewGuid() }).ToArrayAsync());
        });
    }

    [Fact]
    public async Task LISTSCOPE05_Mixed_Valid_And_Forged_Grants_Only_Return_Valid_Scope()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var q = BuildingBusinessScopedCollectionQueries.Rooms(
                await _rooms.GetQueryableAsync(),
                await _floors.GetQueryableAsync(),
                await _buildings.GetQueryableAsync(),
                new[] { Guid.Empty, Guid.NewGuid(), f.A, f.A });
            Assert.Equal(2, await q.CountAsync());
            Assert.DoesNotContain(f.RoomB,
                await q.Select(x => x.Id).ToArrayAsync());
        });
    }

    [Fact]
    public async Task LISTSCOPE06_Deleted_Building_Hides_All_Its_Nested_Collections()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
            await _buildings.DeleteAsync(f.A, autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            var b = await _buildings.GetQueryableAsync();
            var floors = await _floors.GetQueryableAsync();
            var rooms = await _rooms.GetQueryableAsync();
            var contracts = await _contracts.GetQueryableAsync();

            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Buildings(b, new[] { f.A }).ToArrayAsync());
            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Floors(floors, b, new[] { f.A }).ToArrayAsync());
            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Rooms(rooms, floors, b, new[] { f.A }).ToArrayAsync());
            Assert.Empty(await BuildingBusinessScopedCollectionQueries
                .Contracts(contracts, rooms, floors, b,
                    new[] { f.A }).ToArrayAsync());
        });
    }

    [Fact]
    public async Task LISTSCOPE07_SQL_Query_Contains_Server_Side_Joins_And_Filter()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var q = BuildingBusinessScopedCollectionQueries.Contracts(
                await _contracts.GetQueryableAsync(),
                await _rooms.GetQueryableAsync(),
                await _floors.GetQueryableAsync(),
                await _buildings.GetQueryableAsync(),
                new[] { f.A });
            var sql = q.ToQueryString();

            Assert.Contains("JOIN", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, await q.CountAsync());
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
            await _buildings.InsertAsync(new Building(a, "Allowed", "SQLite only"), autoSave: true);
            await _buildings.InsertAsync(new Building(b, "Denied", "SQLite only"), autoSave: true);
            await _floors.InsertAsync(new Floor(floorA, a, 1, "Floor A"), autoSave: true);
            await _floors.InsertAsync(new Floor(floorB, b, 1, "Floor B"), autoSave: true);

            await _rooms.InsertAsync(
                new Room(roomA1, floorA, "201", "Allowed 1", 20m, 2,
                    RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomA2, floorA, "202", "Allowed 2", 21m, 2,
                    RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomB, floorB, "101", "Denied", 20m, 2,
                    RoomStatus.Available), autoSave: true);

            await _contracts.InsertAsync(new Contract(
                contractA1, "SCOPE-A1-" + Guid.NewGuid().ToString("N"),
                roomA1, new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
            await _contracts.InsertAsync(new Contract(
                contractA2, "SCOPE-A2-" + Guid.NewGuid().ToString("N"),
                roomA2, new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
            await _contracts.InsertAsync(new Contract(
                contractB, "SCOPE-B-" + Guid.NewGuid().ToString("N"),
                roomB, new DateTime(2026, 1, 1), null, 1000000m), autoSave: true);
        });
        return new Fixture(a, floorA, roomA2, roomB,
            contractA1, contractA2, contractB);
    }

    private sealed record Fixture(
        Guid A, Guid FloorA, Guid RoomA2, Guid RoomB,
        Guid ContractA1, Guid ContractA2, Guid ContractB);
}
