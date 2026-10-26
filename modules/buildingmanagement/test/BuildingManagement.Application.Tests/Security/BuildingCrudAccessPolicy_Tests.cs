using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Authorization;
using Xunit;

namespace BuildingManagement.Security;

/// <summary>
/// Tests the REAL policy used by Building, Floor and Room AppServices.
/// The production identity and membership dependencies continue to DENY ALL;
/// simulated allowances here do not grant a role or mutate a live database.
/// </summary>
public sealed class BuildingCrudAccessPolicy_Tests
{
    [Fact]
    public async Task CRUDSEC01_Production_Account_Default_Blocks_Authenticated_Permission()
    {
        var f = Valid();
        var policy = new BuildingCrudAccessPolicy(
            f.Principal, new DenyAllBuildingOperatorActiveAccountReader(),
            f.Grants, f.Resources);

        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => policy.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadBuilding));
        Assert.Equal(0, f.Grants.Calls);
    }

    [Fact]
    public async Task CRUDSEC02_Production_Grant_Default_Blocks_Valid_Account()
    {
        var f = Valid();
        var policy = new BuildingCrudAccessPolicy(
            f.Principal, f.Account, new DenyAllBuildingOperatorGrantReader(),
            f.Resources);

        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => policy.EnsureBuildingAsync(
                f.BuildingA, BuildingBusinessCapability.ReadBuilding));
        Assert.Equal(0, f.Resources.Calls);
    }

    [Fact]
    public async Task CRUDSEC03_Anonymous_Caller_Denied_Before_Account_Grant_Query()
    {
        var f = Valid();
        f.Principal.Actor = null;
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureBuildingAsync(
                f.BuildingA, BuildingBusinessCapability.ReadBuilding));
        Assert.Equal(0, f.Account.Calls);
        Assert.Equal(0, f.Grants.Calls);
        Assert.Equal(0, f.Resources.Calls);
    }

    [Fact]
    public async Task CRUDSEC04_Insufficient_Operation_Permission_Cannot_Read()
    {
        var f = Valid();
        f.Principal.Allowed = false;
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, f.ResourceA,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(0, f.Account.Calls);
        Assert.Equal(0, f.Resources.Calls);
    }

    [Fact]
    public async Task CRUDSEC05_Inactive_Account_Cannot_Read_Or_Modify()
    {
        var f = Valid();
        f.Account.Active = false;
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadBuilding));
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, f.ResourceA,
                BuildingBusinessCapability.ManageFloor));
        Assert.Equal(0, f.Grants.Calls);
    }

    [Theory]
    [InlineData(BuildingBusinessResourceKind.Building, BuildingBusinessCapability.ReadBuilding)]
    [InlineData(BuildingBusinessResourceKind.Building, BuildingBusinessCapability.ManageBuilding)]
    [InlineData(BuildingBusinessResourceKind.Floor, BuildingBusinessCapability.ReadFloor)]
    [InlineData(BuildingBusinessResourceKind.Floor, BuildingBusinessCapability.ManageFloor)]
    [InlineData(BuildingBusinessResourceKind.Room, BuildingBusinessCapability.ReadRoom)]
    [InlineData(BuildingBusinessResourceKind.Room, BuildingBusinessCapability.ManageRoom)]
    public async Task CRUDSEC06_Valid_Scoped_Actor_Can_Pass_Resource_Check(
        BuildingBusinessResourceKind kind,
        BuildingBusinessCapability permission)
    {
        var f = Valid();
        await f.Policy.EnsureResourceAsync(kind, f.ResourceA, permission);
        Assert.Equal(1, f.Resources.Calls);
        Assert.Equal(permission, f.Principal.LastPermission);
    }

    [Fact]
    public async Task CRUDSEC07_Cross_Building_Direct_Resource_Is_Rejected()
    {
        var f = Valid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, f.ResourceB,
                BuildingBusinessCapability.ManageRoom));
    }

    [Fact]
    public async Task CRUDSEC08_Unknown_Resource_Denied_For_Signed_In_User()
    {
        var f = Valid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, Guid.NewGuid(),
                BuildingBusinessCapability.ReadFloor));
    }

    [Fact]
    public async Task CRUDSEC09_Revocation_Is_Observed_On_Next_Action()
    {
        var f = Valid();
        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);
        f.Grants.Buildings.Clear();

        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));
        Assert.Equal(2, f.Grants.Calls);
    }

    [Fact]
    public async Task CRUDSEC10_Read_Grant_Does_Not_Imply_Manage_Grant()
    {
        var f = Valid();
        f.Principal.ManageDenied = true;
        await f.Policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, f.ResourceA,
            BuildingBusinessCapability.ReadRoom);
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, f.ResourceA,
                BuildingBusinessCapability.ManageRoom));
    }

    [Fact]
    public async Task CRUDSEC11_First_Building_Without_Bootstrap_Always_Denied()
    {
        var f = Valid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureBuildingCreationAsync());
        Assert.Equal(0, f.Principal.Calls);
    }

    [Fact]
    public async Task CRUDSEC12_Invalid_Inputs_Rejected_Before_Database()
    {
        var f = Valid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, Guid.Empty,
                BuildingBusinessCapability.ReadRoom));
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                (BuildingBusinessResourceKind)999, f.ResourceA,
                BuildingBusinessCapability.ReadRoom));
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.GetAuthorizedBuildingIdsAsync(
                (BuildingBusinessCapability)999));
        Assert.Equal(0, f.Principal.Calls);
    }

    [Fact]
    public async Task CRUDSEC13_Only_Server_Supplied_Building_Ids_Reach_Lists()
    {
        var f = Valid();
        f.Grants.Buildings.Add(Guid.Empty);
        f.Grants.Buildings.Add(f.BuildingA);

        var result = await f.Policy.GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability.ReadRoom);
        Assert.Equal(new[] { f.BuildingA }, result);
        Assert.DoesNotContain(Guid.Empty, result);
    }

    [Fact]
    public async Task CRUDSEC14_Empty_Or_Null_Grants_Deny_With_No_Resource_Lookup()
    {
        var f = Valid();
        f.Grants.Buildings.Clear();
        await Assert.ThrowsAsync<AbpAuthorizationException>(
            () => f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, f.ResourceA,
                BuildingBusinessCapability.ManageFloor));
        Assert.Equal(0, f.Resources.Calls);
    }

    private static Fixture Valid()
    {
        var operatorId = Guid.NewGuid();
        var buildingA = Guid.NewGuid();
        var buildingB = Guid.NewGuid();
        var resourceA = Guid.NewGuid();
        var resourceB = Guid.NewGuid();
        var principal = new FakePrincipal { Actor = operatorId };
        var account = new FakeAccount();
        var grants = new FakeGrants { Buildings = new HashSet<Guid>{buildingA} };
        var resources = new FakeResources();
        resources.Links[buildingA] = buildingA;
        resources.Links[resourceA] = buildingA;
        resources.Links[resourceB] = buildingB;
        return new(operatorId, buildingA, buildingB, resourceA, resourceB,
            principal, account, grants, resources,
            new BuildingCrudAccessPolicy(principal, account, grants, resources));
    }

    private sealed record Fixture(
        Guid OperatorId, Guid BuildingA, Guid BuildingB,
        Guid ResourceA, Guid ResourceB,
        FakePrincipal Principal, FakeAccount Account, FakeGrants Grants,
        FakeResources Resources, BuildingCrudAccessPolicy Policy);

    private sealed class FakePrincipal : IBuildingBusinessPrincipalReader
    {
        public Guid? Actor { get; set; }
        public bool Allowed { get; set; } = true;
        public bool ManageDenied { get; set; }
        public int Calls { get; private set; }
        public BuildingBusinessCapability LastPermission { get; private set; }

        public Task<BuildingBusinessPrincipal?> ReadAsync(
            BuildingBusinessCapability capability)
        {
            Calls++;
            LastPermission = capability;
            var isManage = capability is BuildingBusinessCapability.ManageBuilding
                or BuildingBusinessCapability.ManageFloor
                or BuildingBusinessCapability.ManageRoom;
            return Task.FromResult<BuildingBusinessPrincipal?>(
                Actor.HasValue
                    ? new BuildingBusinessPrincipal(Actor.Value,
                        Allowed && !(ManageDenied && isManage))
                    : null);
        }
    }

    private sealed class FakeAccount : IBuildingOperatorActiveAccountReader
    {
        public bool Active { get; set; } = true;
        public int Calls { get; private set; }
        public Task<bool> IsActiveAsync(Guid userId)
        {
            Calls++;
            return Task.FromResult(Active);
        }
    }

    private sealed class FakeGrants : IBuildingOperatorGrantReader
    {
        public HashSet<Guid> Buildings { get; set; } = new();
        public int Calls { get; private set; }
        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid userId)
        {
            Calls++;
            return Task.FromResult<IReadOnlyCollection<Guid>>(
                Buildings.ToArray());
        }
    }

    private sealed class FakeResources : IBuildingBusinessResourceScopeReader
    {
        public Dictionary<Guid, Guid> Links { get; } = new();
        public int Calls { get; private set; }
        public Task<BuildingBusinessResolvedScope?> ReadAsync(
            BuildingBusinessResourceKind kind, Guid resourceId)
        {
            Calls++;
            return Task.FromResult<BuildingBusinessResolvedScope?>(
                Links.TryGetValue(resourceId, out var buildingId)
                ? new BuildingBusinessResolvedScope(resourceId, buildingId)
                : null);
        }
    }
}
