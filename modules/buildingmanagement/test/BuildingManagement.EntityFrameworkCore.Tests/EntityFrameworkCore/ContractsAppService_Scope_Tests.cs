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
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// The admin contract list is the one surface that hands a caller contracts
/// from every building at once. These tests run it against a real SQLite graph
/// with a test-only policy that grants exactly one building, so the filter is
/// proven to be the policy's answer and not "return everything".
///
/// Production still DENIES ALL through the real readers; the simulated policy
/// here exists only to make the positive path observable.
/// </summary>
public sealed class ContractsAppService_Scope_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<ContractTenant, Guid> _contractTenants;
    private readonly IRepository<Tenant, Guid> _tenants;

    public ContractsAppService_Scope_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _contractTenants = GetRequiredService<IRepository<ContractTenant, Guid>>();
        _tenants = GetRequiredService<IRepository<Tenant, Guid>>();
    }

    [Fact]
    public async Task CTLIST01_Default_Denies_Before_Reading_Any_Contract()
    {
        await SeedAsync();
        // The test module's own fixture policy grants Room only. Contract
        // reads must still fail closed, with no contract ever selected.
        await ShouldFailClosedAsync(async () =>
            await GetRequiredService<IContractsAppService>().GetListAsync());
    }

    [Fact]
    public async Task CTLIST02_List_Returns_Only_The_Granted_Building()
    {
        var f = await SeedAsync();

        var rows = await InScopeAsync(f,
            service => service.GetListAsync());

        // Building B holds two contracts; neither may surface.
        Assert.All(rows, row => Assert.Equal(f.BuildingA, row.BuildingId));
        Assert.Contains(rows, row => row.ContractNumber == f.ContractANumber);
        Assert.DoesNotContain(rows, row => row.ContractNumber == "HD-B-001");
        Assert.DoesNotContain(rows, row => row.ContractNumber == "HD-B-002");
    }

    [Fact]
    public async Task CTLIST03_List_Carries_Room_Floor_Building_And_Tenants()
    {
        var f = await SeedAsync();

        var rows = await InScopeAsync(f,
            service => service.GetListAsync());

        var row = Assert.Single(rows);
        // A screen cannot print any of these without the DTO carrying them:
        // ContractDto has ids only, and the entity knows no building at all.
        Assert.Equal(f.RoomANumber, row.RoomNumber);
        Assert.Equal(f.FloorAName, row.FloorName);
        Assert.Equal(f.BuildingAName, row.BuildingName);
        // Primary first, member second — the order LoadTenantsAsync promises.
        Assert.Equal(2, row.Tenants.Count);
        var tenant = row.Tenants[0];
        Assert.Equal(f.TenantName, tenant.FullName);
        Assert.Equal(ContractTenantRole.PrimaryTenant, tenant.Role);
        Assert.Equal(ContractTenantRole.Member, row.Tenants[1].Role);
    }

    [Fact]
    public async Task CTLIST04_Get_Denies_A_Contract_In_Another_Building()
    {
        var f = await SeedAsync();

        await ShouldFailClosedAsync(async () =>
            await InScopeAsync(f, service => service.GetAsync(f.ContractB)));
    }

    [Fact]
    public async Task CTLIST05_Get_Returns_The_Granted_Building_Contract()
    {
        var f = await SeedAsync();

        var row = await InScopeAsync(f,
            service => service.GetAsync(f.ContractA));

        Assert.Equal(f.ContractANumber, row.ContractNumber);
        Assert.Equal(f.BuildingA, row.BuildingId);
        Assert.Equal(3_000_000m, row.MonthlyRent);
        Assert.Equal(f.TenantName, row.Tenants[0].FullName);
    }

    [Fact]
    public async Task CTLIST06_Get_On_A_Contract_Outside_Every_Grant_Fails_Closed()
    {
        var f = await SeedAsync();
        var unknown = Guid.NewGuid();

        // An unknown id must not distinguish itself from another building's
        // id: both are denied by the policy before any lookup.
        await ShouldFailClosedAsync(async () =>
            await InScopeAsync(f, service => service.GetAsync(unknown)));
    }

    [Fact]
    public async Task CTLIST07_A_Removed_Tenant_Stops_Appearing_On_The_Contract()
    {
        var f = await SeedAsync();

        var before = await InScopeAsync(f,
            service => service.GetAsync(f.ContractA));
        Assert.Contains(before.Tenants, x => x.TenantId == f.MemberTenantId);

        // Moving a tenant out soft-deletes the LINK, not the tenant, so a query
        // that filters only the tenant would keep printing a departed name.
        await WithUnitOfWorkAsync(() => _contractTenants.DeleteAsync(f.MemberLinkId));

        var after = await InScopeAsync(f,
            service => service.GetAsync(f.ContractA));
        var remaining = Assert.Single(after.Tenants);
        Assert.Equal(f.TenantId, remaining.TenantId);
        Assert.Equal(ContractTenantRole.PrimaryTenant, remaining.Role);
    }

    /// <summary>
    /// Builds the service with a policy that grants one building, backed by the
    /// real EF scope reader so contract-to-building resolution is the shipped
    /// one rather than a re-implementation.
    /// </summary>
    private Task<T> InScopeAsync<T>(Fixture f, Func<IContractsAppService, Task<T>> call)
        => WithUnitOfWorkAsync(async () =>
        {
            var reader = GetRequiredService<IBuildingBusinessResourceScopeReader>();
            var policy = new SingleBuildingPolicy(f.BuildingA, reader);
            var service = new ContractsAppService(
                _contracts, _contractTenants, _rooms, _floors,
                _buildings, _tenants, policy,
                GetRequiredService<Volo.Abp.Linq.IAsyncQueryableExecuter>());
            return await call(service);
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
            TenantId: Guid.NewGuid(), TenantName: "Nguyễn Văn An",
            MemberTenantId: Guid.NewGuid(), MemberLinkId: Guid.NewGuid());

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
            await _contracts.InsertAsync(
                new Contract(Guid.NewGuid(), "HD-B-002", f.RoomB,
                    new DateTime(2025, 1, 1), new DateTime(2025, 12, 31),
                    3_500_000m), autoSave: true);

            await _tenants.InsertAsync(
                new Tenant(f.TenantId, f.TenantName, "0901234567", null,
                    null, null, IdentityType.CitizenId, "001001001001"),
                autoSave: true);
            await _tenants.InsertAsync(
                new Tenant(f.MemberTenantId, "Trần Thị Bình", "0901234568", null,
                    null, null, IdentityType.CitizenId, "001001001002"),
                autoSave: true);
            await _contractTenants.InsertAsync(
                new ContractTenant(Guid.NewGuid(), f.ContractA, f.TenantId,
                    ContractTenantRole.PrimaryTenant), autoSave: true);
            await _contractTenants.InsertAsync(
                new ContractTenant(f.MemberLinkId, f.ContractA, f.MemberTenantId,
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
        Guid TenantId, string TenantName,
        Guid MemberTenantId, Guid MemberLinkId);

    /// <summary>
    /// Grants exactly one building for ReadContract. Nothing in production
    /// resolves to this; it exists so the positive path is testable without
    /// inventing an operator membership deployment.
    /// </summary>
    private sealed class SingleBuildingPolicy : IBuildingCrudAccessPolicy
    {
        private readonly Guid _building;
        private readonly IBuildingBusinessResourceScopeReader _reader;

        public SingleBuildingPolicy(
            Guid building, IBuildingBusinessResourceScopeReader reader)
        {
            _building = building;
            _reader = reader;
        }

        public Task<IReadOnlyCollection<Guid>> GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability capability)
        {
            if (capability != BuildingBusinessCapability.ReadContract)
                throw Denied();
            return Task.FromResult<IReadOnlyCollection<Guid>>(new[] { _building });
        }

        public Task EnsureBuildingAsync(
            Guid buildingId, BuildingBusinessCapability capability)
            => throw Denied();

        public async Task EnsureResourceAsync(
            BuildingBusinessResourceKind kind, Guid resourceId,
            BuildingBusinessCapability capability)
        {
            if (kind != BuildingBusinessResourceKind.Contract ||
                capability != BuildingBusinessCapability.ReadContract)
                throw Denied();

            var resolved = await _reader.ReadAsync(kind, resourceId);
            if (resolved is null || resolved.ResourceId != resourceId ||
                resolved.BuildingId != _building)
                throw Denied();
        }

        public Task EnsureBuildingCreationAsync() => throw Denied();

        private static AbpAuthorizationException Denied()
            => new("Contracts test scope grants one building only.");
    }
}
