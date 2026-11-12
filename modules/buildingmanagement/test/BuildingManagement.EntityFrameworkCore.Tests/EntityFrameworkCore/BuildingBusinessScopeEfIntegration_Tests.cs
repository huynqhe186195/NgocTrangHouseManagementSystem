using System;
using System.Collections.Generic;
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
/// Repository-backed, synthetic SQLite fixtures only. No real PostgreSQL,
/// user assignments, production roles or remote endpoint is involved.
/// </summary>
public sealed class BuildingBusinessScopeEfIntegration_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;

    public BuildingBusinessScopeEfIntegration_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
    }

    [Fact]
    public async Task SCOPEEF01_Repository_Resolves_Four_Resource_Hierarchies()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var reader = GetRequiredService<IBuildingBusinessResourceScopeReader>();
            foreach (var (kind, id) in new[]
            {
                (BuildingBusinessResourceKind.Building, f.Building),
                (BuildingBusinessResourceKind.Floor, f.Floor),
                (BuildingBusinessResourceKind.Room, f.Room),
                (BuildingBusinessResourceKind.Contract, f.Contract)
            })
            {
                var actual = await reader.ReadAsync(kind, id);
                Assert.NotNull(actual);
                Assert.Equal(id, actual.ResourceId);
                Assert.Equal(f.Building, actual.BuildingId);
            }
        });
    }

    [Fact]
    public async Task SCOPEEF02_Unknown_Or_Invalid_Resource_Denied()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var reader = GetRequiredService<IBuildingBusinessResourceScopeReader>();
            Assert.Null(await reader.ReadAsync(BuildingBusinessResourceKind.Room, Guid.Empty));
            Assert.Null(await reader.ReadAsync(BuildingBusinessResourceKind.Contract, Guid.NewGuid()));
            Assert.Null(await reader.ReadAsync((BuildingBusinessResourceKind)999, Guid.NewGuid()));
        });
    }

    [Fact]
    public async Task SCOPEEF03_Deleted_Building_Hides_Related_Resources()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
            await _buildings.DeleteAsync(f.Building, autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            var reader = GetRequiredService<IBuildingBusinessResourceScopeReader>();
            Assert.Null(await reader.ReadAsync(BuildingBusinessResourceKind.Building, f.Building));
            Assert.Null(await reader.ReadAsync(BuildingBusinessResourceKind.Floor, f.Floor));
            Assert.Null(await reader.ReadAsync(BuildingBusinessResourceKind.Room, f.Room));
            Assert.Null(await reader.ReadAsync(BuildingBusinessResourceKind.Contract, f.Contract));
        });
    }

    [Fact]
    public async Task SCOPEEF04_Default_DI_Grants_Deny_Actual_Repository_Graph()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var grants = GetRequiredService<IBuildingOperatorGrantReader>();
            Assert.IsType<DenyAllBuildingOperatorGrantReader>(grants);
            var gate = new BuildingBusinessScopeDesignGate(
                new SimulatedPrincipal(),
                GetRequiredService<IBuildingBusinessResourceScopeReader>(),
                grants);

            Assert.Equal(BuildingBusinessScopeAssessment.Denied,
                await gate.AssessDesignOnlyAsync(
                    BuildingBusinessResourceKind.Contract, f.Contract,
                    BuildingBusinessCapability.ReadContract));
        });
    }

    [Fact]
    public async Task SCOPEEF05_Cross_Building_Grant_Still_Denied()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var reader = GetRequiredService<IBuildingBusinessResourceScopeReader>();
            var gate = new BuildingBusinessScopeDesignGate(
                new SimulatedPrincipal(),
                reader,
                new SimulatedGrants(Guid.NewGuid()));
            Assert.Equal(BuildingBusinessScopeAssessment.Denied,
                await gate.AssessDesignOnlyAsync(
                    BuildingBusinessResourceKind.Room, f.Room,
                    BuildingBusinessCapability.ReadRoom));

            var designOnly = new BuildingBusinessScopeDesignGate(
                new SimulatedPrincipal(),
                reader,
                new SimulatedGrants(f.Building));
            Assert.Equal(BuildingBusinessScopeAssessment.EligibleForDesignOnly,
                await designOnly.AssessDesignOnlyAsync(
                    BuildingBusinessResourceKind.Room, f.Room,
                    BuildingBusinessCapability.ReadRoom));
        });
    }

    private async Task<Fixture> SeedAsync()
    {
        var building = Guid.NewGuid();
        var floor = Guid.NewGuid();
        var room = Guid.NewGuid();
        var contract = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(building, "Business scope SQLite fixture", "Test only"),
                autoSave: true);
            await _floors.InsertAsync(new Floor(floor, building, 1, "Test floor"),
                autoSave: true);
            await _rooms.InsertAsync(
                new Room(room, floor, "101", "Fixture room", 20m, 2,
                    RoomStatus.Available), autoSave: true);
            await _contracts.InsertAsync(
                new Contract(contract,
                    "BSCOPE-" + Guid.NewGuid().ToString("N"), room,
                    new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
                    3_000_000m), autoSave: true);
        });
        return new Fixture(building, floor, room, contract);
    }

    private sealed record Fixture(
        Guid Building, Guid Floor, Guid Room, Guid Contract);

    private sealed class SimulatedPrincipal : IBuildingBusinessPrincipalReader
    {
        public Task<BuildingBusinessPrincipal?> ReadAsync(
            BuildingBusinessCapability capability)
            => Task.FromResult<BuildingBusinessPrincipal?>(
                new BuildingBusinessPrincipal(Guid.NewGuid(), true));
    }

    private sealed class SimulatedGrants : IBuildingOperatorGrantReader
    {
        private readonly Guid _building;
        public SimulatedGrants(Guid building) => _building = building;
        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid operatorId)
            => Task.FromResult<IReadOnlyCollection<Guid>>(new[] { _building });
    }
}
