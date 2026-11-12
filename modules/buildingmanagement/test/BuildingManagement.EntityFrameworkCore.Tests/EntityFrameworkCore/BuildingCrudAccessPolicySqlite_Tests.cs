using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Real production CRUD authorization policy plus real SQLite-backed ABP
/// resource graph reader. Only the actor/account/grant inputs are simulated.
/// This does NOT rely on the test-only legacy business assertion adapter.
/// No HTTP host, migration, PostgreSQL data or production membership is used.
/// </summary>
public sealed class BuildingCrudAccessPolicySqlite_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;

    public BuildingCrudAccessPolicySqlite_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
    }

    [Fact]
    public async Task CRUDSQL01_Read_And_Manage_Nested_A_But_Never_B()
    {
        var s = await SeedTwoBuildingsAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var policy = Policy(s.A);
            await policy.EnsureBuildingAsync(s.A,
                BuildingBusinessCapability.ReadBuilding);
            await policy.EnsureBuildingAsync(s.A,
                BuildingBusinessCapability.ManageBuilding);
            await policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, s.FloorA,
                BuildingBusinessCapability.ManageFloor);
            await policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, s.RoomA,
                BuildingBusinessCapability.ReadRoom);
            await policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, s.RoomA,
                BuildingBusinessCapability.ManageRoom);

            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                policy.EnsureBuildingAsync(s.B,
                    BuildingBusinessCapability.ReadBuilding));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                policy.EnsureResourceAsync(
                    BuildingBusinessResourceKind.Floor, s.FloorB,
                    BuildingBusinessCapability.ManageFloor));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                policy.EnsureResourceAsync(
                    BuildingBusinessResourceKind.Room, s.RoomB,
                    BuildingBusinessCapability.ManageRoom));
        });
    }

    [Fact]
    public async Task CRUDSQL02_Create_Floor_And_Room_Require_Parent_Building_Scope()
    {
        var s = await SeedTwoBuildingsAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var policy = Policy(s.A);
            await policy.EnsureBuildingAsync(s.A,
                BuildingBusinessCapability.ManageFloor);
            await policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, s.FloorA,
                BuildingBusinessCapability.ManageRoom);

            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                policy.EnsureBuildingAsync(s.B,
                    BuildingBusinessCapability.ManageFloor));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                policy.EnsureResourceAsync(
                    BuildingBusinessResourceKind.Floor, s.FloorB,
                    BuildingBusinessCapability.ManageRoom));
        });
    }

    [Fact]
    public async Task CRUDSQL03_Deleted_Building_Blocks_Previously_Valid_Room()
    {
        var s = await SeedTwoBuildingsAsync();
        await WithUnitOfWorkAsync(async () =>
            await _buildings.DeleteAsync(s.A, autoSave: true));
        await WithUnitOfWorkAsync(async () =>
        {
            var policy = Policy(s.A);
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                policy.EnsureResourceAsync(
                    BuildingBusinessResourceKind.Room, s.RoomA,
                    BuildingBusinessCapability.ReadRoom));
        });
    }

    [Fact]
    public async Task CRUDSQL04_Missing_Bootstrap_Prevents_Orphan_Building()
    {
        var s = await SeedTwoBuildingsAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var policy = Policy(s.A);
            await Assert.ThrowsAsync<AbpAuthorizationException>(
                () => policy.EnsureBuildingCreationAsync());
        });
    }

    private BuildingCrudAccessPolicy Policy(Guid buildingId)
    {
        var id = Guid.NewGuid();
        return new BuildingCrudAccessPolicy(
            new AllowedPrincipal(id), new ActiveAccount(),
            new AssignedGrants(buildingId),
            GetRequiredService<IBuildingBusinessResourceScopeReader>());
    }

    private async Task<Seed> SeedTwoBuildingsAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var floorA = Guid.NewGuid();
        var floorB = Guid.NewGuid();
        var roomA = Guid.NewGuid();
        var roomB = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(a, "Authorized test building", "SQLite A"),
                autoSave: true);
            await _buildings.InsertAsync(
                new Building(b, "Different test building", "SQLite B"),
                autoSave: true);

            await _floors.InsertAsync(new Floor(floorA, a, 1, "Floor A"),
                autoSave: true);
            await _floors.InsertAsync(new Floor(floorB, b, 1, "Floor B"),
                autoSave: true);

            await _rooms.InsertAsync(new Room(roomA, floorA, "101",
                "Room A", 20m, 2, RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(new Room(roomB, floorB, "101",
                "Room B", 20m, 2, RoomStatus.Available), autoSave: true);
        });
        return new Seed(a, b, floorA, floorB, roomA, roomB);
    }

    private sealed record Seed(
        Guid A, Guid B, Guid FloorA, Guid FloorB, Guid RoomA, Guid RoomB);

    private sealed class AllowedPrincipal : IBuildingBusinessPrincipalReader
    {
        private readonly Guid _id;
        public AllowedPrincipal(Guid id) => _id = id;
        public Task<BuildingBusinessPrincipal?> ReadAsync(
            BuildingBusinessCapability capability)
            => Task.FromResult<BuildingBusinessPrincipal?>(
                new BuildingBusinessPrincipal(_id, true));
    }

    private sealed class ActiveAccount : IBuildingOperatorActiveAccountReader
    {
        public Task<bool> IsActiveAsync(Guid id) => Task.FromResult(true);
    }

    private sealed class AssignedGrants : IBuildingOperatorGrantReader
    {
        private readonly Guid _building;
        public AssignedGrants(Guid building) => _building = building;

        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid id)
            => Task.FromResult<IReadOnlyCollection<Guid>>(
                new[] { _building });
    }
}
