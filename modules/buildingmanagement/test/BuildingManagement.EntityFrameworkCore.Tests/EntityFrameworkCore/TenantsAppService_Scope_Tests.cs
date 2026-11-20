using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using BuildingManagement.Tenants;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// A tenant belongs to no building, so the roster cannot be scoped by a
/// resource check the way a contract is. It is scoped by the links instead:
/// a person is visible only when at least one of their live contracts sits in
/// a granted building. These tests run that rule against a real SQLite graph.
///
/// Production still DENIES ALL through the real readers; the simulated policy
/// here exists only to make the positive path observable.
/// </summary>
public sealed class TenantsAppService_Scope_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<ContractTenant, Guid> _contractTenants;
    private readonly IRepository<Tenant, Guid> _tenants;

    public TenantsAppService_Scope_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _contractTenants = GetRequiredService<IRepository<ContractTenant, Guid>>();
        _tenants = GetRequiredService<IRepository<Tenant, Guid>>();
    }

    [Fact]
    public async Task TNLIST01_Default_Denies_Before_Reading_Any_Tenant()
    {
        await SeedAsync();
        // The test module's fixture policy grants Room only. Tenant reads —
        // which carry citizen ids and addresses — must fail closed.
        await ShouldFailClosedAsync(async () =>
            await GetRequiredService<ITenantsAppService>().GetListAsync());
    }

    [Fact]
    public async Task TNLIST02_List_Returns_Only_Tenants_Renting_In_The_Granted_Building()
    {
        var f = await SeedAsync();

        var rows = await InScopeAsync(f, service => service.GetListAsync());

        Assert.Contains(rows, row => row.Id == f.AlphaId);
        Assert.Contains(rows, row => row.Id == f.SharedId);
        // Beta rents only in building B.
        Assert.DoesNotContain(rows, row => row.Id == f.BetaId);
    }

    [Fact]
    public async Task TNLIST03_A_Shared_Tenant_Shows_Only_The_Granted_Building_Contracts()
    {
        var f = await SeedAsync();

        var rows = await InScopeAsync(f, service => service.GetListAsync());

        var shared = Assert.Single(rows, row => row.Id == f.SharedId);
        var contract = Assert.Single(shared.Contracts);
        Assert.Equal(f.ContractANumber, contract.ContractNumber);
        Assert.Equal(f.BuildingA, contract.BuildingId);
        // The same person also rents in building B. Printing that contract
        // would disclose another building's occupancy from a list the caller
        // only holds a grant on building A for.
        Assert.DoesNotContain(shared.Contracts, x => x.ContractNumber == "HD-B-001");
    }

    [Fact]
    public async Task TNLIST04_List_Carries_Room_Floor_And_Building_For_Each_Contract()
    {
        var f = await SeedAsync();

        var rows = await InScopeAsync(f, service => service.GetListAsync());

        var alpha = Assert.Single(rows, row => row.Id == f.AlphaId);
        var contract = Assert.Single(alpha.Contracts);
        // Tenant carries no dwelling at all; without these the roster could
        // not print where anybody lives.
        Assert.Equal(f.RoomANumber, contract.RoomNumber);
        Assert.Equal(f.FloorAName, contract.FloorName);
        Assert.Equal(f.BuildingAName, contract.BuildingName);
        Assert.Equal(f.RoomA, contract.RoomId);
    }

    [Fact]
    public async Task TNLIST05_Get_Returns_The_Granted_Building_Tenant()
    {
        var f = await SeedAsync();

        var row = await InScopeAsync(f, service => service.GetAsync(f.AlphaId));

        Assert.Equal(f.AlphaName, row.FullName);
        Assert.Equal("0901234567", row.PhoneNumber);
        Assert.Single(row.Contracts);
    }

    [Fact]
    public async Task TNLIST06_Get_Denies_A_Tenant_Outside_Every_Grant()
    {
        var f = await SeedAsync();

        // Another building's tenant and a nonexistent id must be
        // indistinguishable, so both surface as not-found rather than 403.
        await InScopeAsync(f, service =>
            Assert.ThrowsAsync<EntityNotFoundException>(
                () => service.GetAsync(f.BetaId)));
        await InScopeAsync(f, service =>
            Assert.ThrowsAsync<EntityNotFoundException>(
                () => service.GetAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task TNLIST07_A_Departed_Tenant_Drops_Out_Of_The_Roster()
    {
        var f = await SeedAsync();

        var before = await InScopeAsync(f, service => service.GetListAsync());
        Assert.Contains(before, row => row.Id == f.BetaInAId);

        // Moving somebody out soft-deletes the LINK, not the person. A query
        // that filtered only !tenant.IsDeleted would keep printing them.
        await WithUnitOfWorkAsync(() => _contractTenants.DeleteAsync(f.BetaInALinkId));

        var after = await InScopeAsync(f, service => service.GetListAsync());
        Assert.DoesNotContain(after, row => row.Id == f.BetaInAId);
        // The person still exists and still rents elsewhere; they are simply
        // no longer visible from building A.
        Assert.NotNull(await WithUnitOfWorkAsync(() => _tenants.FindAsync(f.BetaInAId)));
    }

    [Fact]
    public async Task TNLIST08_A_Tenant_In_No_Live_Contract_Is_Not_Listed()
    {
        var f = await SeedAsync();

        await WithUnitOfWorkAsync(() => _tenants.InsertAsync(
            new Tenant(Guid.NewGuid(), "Lê Văn Cô Đơn", "0901234569", null,
                null, null, IdentityType.CitizenId, "001001001009"),
            autoSave: true));

        var rows = await InScopeAsync(f, service => service.GetListAsync());

        Assert.DoesNotContain(rows, row => row.FullName == "Lê Văn Cô Đơn");
    }

    private Task<T> InScopeAsync<T>(Fixture f, Func<ITenantsAppService, Task<T>> call)
        => WithUnitOfWorkAsync(() =>
        {
            var service = new TenantsAppService(
                _tenants, _contracts, _contractTenants, _rooms, _floors,
                _buildings, new SingleBuildingTenantPolicy(f.BuildingA),
                GetRequiredService<Volo.Abp.Linq.IAsyncQueryableExecuter>());
            return call(service);
        });

    private static async Task ShouldFailClosedAsync(Func<Task> call)
        => await Assert.ThrowsAsync<AbpAuthorizationException>(call);

    private async Task<Fixture> SeedAsync()
    {
        var f = new Fixture(
            BuildingA: Guid.NewGuid(), BuildingAName: "Tòa A",
            FloorA: Guid.NewGuid(), FloorAName: "Tầng 1",
            RoomA: Guid.NewGuid(), RoomANumber: "A101",
            ContractA: Guid.NewGuid(), ContractANumber: "HD-A-001",
            BuildingB: Guid.NewGuid(), BuildingBName: "Tòa B",
            FloorB: Guid.NewGuid(),
            RoomB: Guid.NewGuid(),
            ContractB: Guid.NewGuid(),
            AlphaId: Guid.NewGuid(), AlphaName: "Nguyễn Văn An",
            SharedId: Guid.NewGuid(),
            BetaId: Guid.NewGuid(),
            BetaInAId: Guid.NewGuid(), BetaInALinkId: Guid.NewGuid());

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(f.BuildingA, f.BuildingAName, "Test only"),
                autoSave: true);
            await _buildings.InsertAsync(
                new Building(f.BuildingB, f.BuildingBName, "Test only"),
                autoSave: true);
            await _floors.InsertAsync(
                new Floor(f.FloorA, f.BuildingA, 1, f.FloorAName), autoSave: true);
            await _floors.InsertAsync(
                new Floor(f.FloorB, f.BuildingB, 1, "Tầng 1"), autoSave: true);
            await _rooms.InsertAsync(
                new Room(f.RoomA, f.FloorA, f.RoomANumber, "Phòng A101",
                    20m, 2, RoomStatus.Available), autoSave: true);
            await _rooms.InsertAsync(
                new Room(f.RoomB, f.FloorB, "B201", "Phòng B201",
                    25m, 2, RoomStatus.Available), autoSave: true);

            await _contracts.InsertAsync(
                new Contract(f.ContractA, f.ContractANumber, f.RoomA,
                    new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
                    3_000_000m), autoSave: true);
            await _contracts.InsertAsync(
                new Contract(f.ContractB, "HD-B-001", f.RoomB,
                    new DateTime(2026, 2, 1), new DateTime(2027, 1, 31),
                    4_000_000m), autoSave: true);

            await _tenants.InsertAsync(
                new Tenant(f.AlphaId, f.AlphaName, "0901234567", null,
                    null, null, IdentityType.CitizenId, "001001001001"),
                autoSave: true);
            // Rents in both buildings: the roster must show only contract A.
            await _tenants.InsertAsync(
                new Tenant(f.SharedId, "Trần Thị Bình", "0901234568", null,
                    null, null, IdentityType.CitizenId, "001001001002"),
                autoSave: true);
            await _tenants.InsertAsync(
                new Tenant(f.BetaId, "Phạm Văn Cường", "0901234570", null,
                    null, null, IdentityType.CitizenId, "001001001003"),
                autoSave: true);
            // Lives in building A today, moves out during TNLIST07.
            await _tenants.InsertAsync(
                new Tenant(f.BetaInAId, "Đỗ Thị Dung", "0901234571", null,
                    null, null, IdentityType.CitizenId, "001001001004"),
                autoSave: true);

            await _contractTenants.InsertAsync(
                new ContractTenant(Guid.NewGuid(), f.ContractA, f.AlphaId,
                    ContractTenantRole.PrimaryTenant), autoSave: true);
            await _contractTenants.InsertAsync(
                new ContractTenant(Guid.NewGuid(), f.ContractA, f.SharedId,
                    ContractTenantRole.Member), autoSave: true);
            await _contractTenants.InsertAsync(
                new ContractTenant(Guid.NewGuid(), f.ContractB, f.SharedId,
                    ContractTenantRole.PrimaryTenant), autoSave: true);
            await _contractTenants.InsertAsync(
                new ContractTenant(Guid.NewGuid(), f.ContractB, f.BetaId,
                    ContractTenantRole.PrimaryTenant), autoSave: true);
            await _contractTenants.InsertAsync(
                new ContractTenant(f.BetaInALinkId, f.ContractA, f.BetaInAId,
                    ContractTenantRole.Member), autoSave: true);
        });

        return f;
    }

    private sealed record Fixture(
        Guid BuildingA, string BuildingAName,
        Guid FloorA, string FloorAName,
        Guid RoomA, string RoomANumber,
        Guid ContractA, string ContractANumber,
        Guid BuildingB, string BuildingBName,
        Guid FloorB,
        Guid RoomB,
        Guid ContractB,
        Guid AlphaId, string AlphaName,
        Guid SharedId,
        Guid BetaId,
        Guid BetaInAId, Guid BetaInALinkId);

    /// <summary>
    /// Grants exactly one building for ReadTenant. Nothing in production
    /// resolves to this; it exists so the positive path is testable.
    /// </summary>
    private sealed class SingleBuildingTenantPolicy : IBuildingCrudAccessPolicy
    {
        private readonly Guid _building;

        public SingleBuildingTenantPolicy(Guid building) => _building = building;

        public Task<IReadOnlyCollection<Guid>> GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability capability)
        {
            if (capability != BuildingBusinessCapability.ReadTenant)
                throw Denied();
            return Task.FromResult<IReadOnlyCollection<Guid>>(new[] { _building });
        }

        public Task EnsureBuildingAsync(
            Guid buildingId, BuildingBusinessCapability capability)
            => throw Denied();

        // A tenant has no resource-scope entry: the service resolves scope
        // from the links, so any resource check here is a programming error.
        public Task EnsureResourceAsync(
            BuildingBusinessResourceKind kind, Guid resourceId,
            BuildingBusinessCapability capability)
            => throw Denied();

        public Task EnsureBuildingCreationAsync() => throw Denied();

        private static AbpAuthorizationException Denied()
            => new("Tenants test scope grants one building only.");
    }
}
