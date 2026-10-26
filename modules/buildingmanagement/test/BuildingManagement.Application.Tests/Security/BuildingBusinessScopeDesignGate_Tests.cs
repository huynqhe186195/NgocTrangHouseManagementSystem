using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Permissions;
using Xunit;

namespace BuildingManagement.Security;

/// <summary>
/// No actual operator grants, identity database, migration or CRUD write.
/// In production the configured grant provider continues to deny everything.
/// </summary>
public sealed class BuildingBusinessScopeDesignGate_Tests
{
    [Fact]
    public async Task SCOPE01_Production_Default_Denies_With_Permission()
    {
        var s = Valid();
        var gate = new BuildingBusinessScopeDesignGate(
            s.Actor, s.Resource, new DenyAllBuildingOperatorGrantReader());

        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
    }

    [Theory]
    [InlineData(BuildingBusinessResourceKind.Building,
        BuildingBusinessCapability.ReadBuilding)]
    [InlineData(BuildingBusinessResourceKind.Building,
        BuildingBusinessCapability.ManageBuilding)]
    [InlineData(BuildingBusinessResourceKind.Floor,
        BuildingBusinessCapability.ReadFloor)]
    [InlineData(BuildingBusinessResourceKind.Floor,
        BuildingBusinessCapability.ManageFloor)]
    [InlineData(BuildingBusinessResourceKind.Room,
        BuildingBusinessCapability.ReadRoom)]
    [InlineData(BuildingBusinessResourceKind.Room,
        BuildingBusinessCapability.ManageRoom)]
    [InlineData(BuildingBusinessResourceKind.Contract,
        BuildingBusinessCapability.ReadContract)]
    [InlineData(BuildingBusinessResourceKind.Contract,
        BuildingBusinessCapability.ManageContract)]
    public async Task SCOPE02_Same_Server_Building_Can_Only_Be_Design_Eligible(
        BuildingBusinessResourceKind kind, BuildingBusinessCapability capability)
    {
        var s = Valid();
        Assert.Equal(BuildingBusinessScopeAssessment.EligibleForDesignOnly,
            await s.Gate.AssessDesignOnlyAsync(kind, s.ResourceId, capability));
    }

    [Fact]
    public async Task SCOPE03_Anonymous_Is_Denied_Before_Resource_Query()
    {
        var s = Valid();
        s.Actor.Principal = null;
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(0, s.Resource.Calls);
        Assert.Equal(0, s.Grants.Calls);
    }

    [Fact]
    public async Task SCOPE04_Ungranted_Permission_Denied_Before_Resource_Query()
    {
        var s = Valid();
        s.Actor.Principal = new(s.OperatorId, false);
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Contract, s.ResourceId,
                BuildingBusinessCapability.ReadContract));
        Assert.Equal(0, s.Resource.Calls);
    }

    [Fact]
    public async Task SCOPE05_Invalid_Resource_Capability_Pair_Denied_Before_Principal()
    {
        var s = Valid();
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ManageContract));
        Assert.Equal(0, s.Actor.Calls);
    }

    [Fact]
    public async Task SCOPE06_Unknown_Operation_Denied_Before_Principal()
    {
        var s = Valid();
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                (BuildingBusinessCapability)999));
        Assert.Equal(0, s.Actor.Calls);
    }

    [Fact]
    public async Task SCOPE07_Empty_Or_Forged_Ids_Denied()
    {
        var s = Valid();
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, Guid.Empty,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(0, s.Actor.Calls);

        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, Guid.NewGuid(),
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(0, s.Grants.Calls);
    }

    [Fact]
    public async Task SCOPE08_Missing_Server_Graph_Denied_No_Grants_Read()
    {
        var s = Valid();
        s.Resource.Scope = null;
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(0, s.Grants.Calls);
    }

    [Fact]
    public async Task SCOPE09_Cross_Building_Grant_Denied()
    {
        var s = Valid();
        s.Grants.BuildingIds = new[] { Guid.NewGuid() };
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
    }

    [Fact]
    public async Task SCOPE10_Revocation_Requires_New_Assessment()
    {
        var s = Valid();
        Assert.Equal(BuildingBusinessScopeAssessment.EligibleForDesignOnly,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));

        s.Grants.BuildingIds = Array.Empty<Guid>();
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(2, s.Grants.Calls);
    }

    [Fact]
    public async Task SCOPE11_Read_Grant_Does_Not_Imply_Manage()
    {
        var s = Valid();
        s.Actor.ManageDenied = true;
        Assert.Equal(BuildingBusinessScopeAssessment.EligibleForDesignOnly,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ManageRoom));
    }

    [Fact]
    public async Task SCOPE12_Forged_Server_Result_Cannot_Change_Resource_Id()
    {
        var s = Valid();
        s.Resource.Scope = new(Guid.NewGuid(), s.BuildingId);
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
        Assert.Equal(0, s.Grants.Calls);
    }

    [Fact]
    public async Task SCOPE13_Null_Grants_Are_Denied()
    {
        var s = Valid();
        s.Grants.BuildingIds = null;
        Assert.Equal(BuildingBusinessScopeAssessment.Denied,
            await s.Gate.AssessDesignOnlyAsync(
                BuildingBusinessResourceKind.Room, s.ResourceId,
                BuildingBusinessCapability.ReadRoom));
    }

    [Fact]
    public void SCOPE14_Separate_ABP_Permission_Names_Are_Defined()
    {
        var declared = BuildingManagementPermissions.GetAll();
        var expected = new[]
        {
            BuildingManagementPermissions.Buildings.Read,
            BuildingManagementPermissions.Buildings.Manage,
            BuildingManagementPermissions.Floors.Read,
            BuildingManagementPermissions.Floors.Manage,
            BuildingManagementPermissions.Rooms.Read,
            BuildingManagementPermissions.Rooms.Manage,
            BuildingManagementPermissions.Contracts.Read,
            BuildingManagementPermissions.Contracts.Manage
        };
        foreach (var permission in expected)
            Assert.Contains(permission, declared);
        Assert.Equal(expected.Length, expected.Distinct().Count());
    }

    private static TestScope Valid()
    {
        var operatorId = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var building = Guid.NewGuid();
        var actor = new FakePrincipal { Principal = new(operatorId, true) };
        var graph = new FakeResource
        {
            Scope = new BuildingBusinessResolvedScope(resource, building)
        };
        var grants = new FakeGrant { BuildingIds = new[] { building } };
        return new TestScope(operatorId, resource, building, actor, graph,
            grants, new BuildingBusinessScopeDesignGate(actor, graph, grants));
    }

    private sealed record TestScope(
        Guid OperatorId, Guid ResourceId, Guid BuildingId,
        FakePrincipal Actor, FakeResource Resource, FakeGrant Grants,
        BuildingBusinessScopeDesignGate Gate);

    private sealed class FakePrincipal : IBuildingBusinessPrincipalReader
    {
        public BuildingBusinessPrincipal? Principal { get; set; }
        public bool ManageDenied { get; set; }
        public int Calls { get; private set; }

        public Task<BuildingBusinessPrincipal?> ReadAsync(
            BuildingBusinessCapability capability)
        {
            Calls++;
            var isManage = capability is
                BuildingBusinessCapability.ManageBuilding or
                BuildingBusinessCapability.ManageFloor or
                BuildingBusinessCapability.ManageRoom or
                BuildingBusinessCapability.ManageContract;
            var result = ManageDenied && isManage && Principal is not null
                ? Principal with { PermissionGranted = false }
                : Principal;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeResource : IBuildingBusinessResourceScopeReader
    {
        public BuildingBusinessResolvedScope? Scope { get; set; }
        public int Calls { get; private set; }

        public Task<BuildingBusinessResolvedScope?> ReadAsync(
            BuildingBusinessResourceKind kind, Guid resourceId)
        {
            Calls++;
            return Task.FromResult(
                Scope);
        }
    }

    private sealed class FakeGrant : IBuildingOperatorGrantReader
    {
        public IReadOnlyCollection<Guid>? BuildingIds { get; set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(
            Guid operatorId)
        {
            Calls++;
            return Task.FromResult<IReadOnlyCollection<Guid>>(
                BuildingIds!);
        }
    }
}
