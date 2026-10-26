using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Permissions;
using Xunit;

namespace BuildingManagement.Security;

/// <summary>
/// Read-only authorization design tests: no account or grant DB,
/// no actual role assignment and no mutation entrypoint exists.
/// </summary>
public sealed class BuildingOperatorChangeDesignGate_Tests
{
    [Fact]
    public async Task DELEGATE01_Default_Account_Check_Denies()
    {
        var fixture = Valid();
        var gate = new BuildingOperatorChangeDesignGate(
            fixture.Issuer, new DenyAllBuildingOperatorActiveAccountReader(),
            fixture.Resources, fixture.Grants);
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        Assert.Equal(0, fixture.Resources.Calls);
    }

    [Fact]
    public async Task DELEGATE02_Default_Building_Grant_Check_Denies()
    {
        var fixture = Valid();
        var gate = new BuildingOperatorChangeDesignGate(
            fixture.Issuer, fixture.Accounts, fixture.Resources,
            new DenyAllBuildingOperatorGrantReader());
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
    }

    [Theory]
    [InlineData(BuildingOperatorChangeKind.Assign)]
    [InlineData(BuildingOperatorChangeKind.Revoke)]
    public async Task DELEGATE03_Valid_Simulated_Context_Is_Design_Only(
        BuildingOperatorChangeKind change)
    {
        var fixture = Valid();
        Assert.Equal(BuildingOperatorChangePreflightStatus.EligibleForDesignOnly,
            await fixture.Gate.AssessDesignOnlyAsync(
                change, fixture.Building, fixture.Recipient));
    }

    [Fact]
    public async Task DELEGATE04_Anonymous_Issuer_Denied_Before_User_Check()
    {
        var fixture = Valid();
        fixture.Issuer.Issuer = null;
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        Assert.Equal(0, fixture.Accounts.Calls);
    }

    [Fact]
    public async Task DELEGATE05_Missing_Delegation_Permission_Denied()
    {
        var fixture = Valid();
        fixture.Issuer.Issuer = new(fixture.Operator, false);
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        Assert.Equal(0, fixture.Accounts.Calls);
    }

    [Fact]
    public async Task DELEGATE06_Self_Assignment_Is_Denied()
    {
        var fixture = Valid();
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Operator));
        Assert.Equal(0, fixture.Accounts.Calls);
    }

    [Fact]
    public async Task DELEGATE07_Self_Revoke_Requires_Separate_Ownership_Review()
    {
        var fixture = Valid();
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Revoke, fixture.Building, fixture.Operator));
        Assert.Equal(0, fixture.Accounts.Calls);
    }

    [Fact]
    public async Task DELEGATE08_Inactive_Issuer_Denied()
    {
        var fixture = Valid();
        fixture.Accounts.Inactive.Add(fixture.Operator);
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Revoke, fixture.Building, fixture.Recipient));
        Assert.Equal(0, fixture.Resources.Calls);
    }

    [Fact]
    public async Task DELEGATE09_Inactive_Target_Cannot_Receive_New_Grant()
    {
        var fixture = Valid();
        fixture.Accounts.Inactive.Add(fixture.Recipient);
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        Assert.Equal(0, fixture.Resources.Calls);
    }

    [Fact]
    public async Task DELEGATE10_Inactive_Target_Is_Still_Eligible_For_Revocation_Design()
    {
        var fixture = Valid();
        fixture.Accounts.Inactive.Add(fixture.Recipient);
        Assert.Equal(BuildingOperatorChangePreflightStatus.EligibleForDesignOnly,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Revoke, fixture.Building, fixture.Recipient));
        Assert.Equal(1, fixture.Accounts.Calls); // only issuer checked
    }

    [Fact]
    public async Task DELEGATE11_No_Issuer_Building_Grant_Denied()
    {
        var fixture = Valid();
        fixture.Grants.Ids.Clear();
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
    }

    [Fact]
    public async Task DELEGATE12_Cross_Building_Grant_Denied()
    {
        var fixture = Valid();
        fixture.Grants.Ids.Clear();
        fixture.Grants.Ids.Add(Guid.NewGuid());
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Revoke, fixture.Building, fixture.Recipient));
    }

    [Fact]
    public async Task DELEGATE13_Unknown_Or_Mismatched_Building_Denied()
    {
        var fixture = Valid();
        fixture.Resources.Result = null;
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        Assert.Equal(0, fixture.Grants.Calls);

        fixture.Resources.Result = new(
            fixture.Building, Guid.NewGuid());
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
    }

    [Fact]
    public async Task DELEGATE14_Invalid_Inputs_Reject_Before_Principal()
    {
        var fixture = Valid();
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                (BuildingOperatorChangeKind)999, fixture.Building, fixture.Recipient));
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, Guid.Empty, fixture.Recipient));
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, Guid.Empty));
        Assert.Equal(0, fixture.Issuer.Calls);
    }

    [Fact]
    public async Task DELEGATE15_Revocation_Of_Issuer_Building_Grant_Is_Observed()
    {
        var fixture = Valid();
        Assert.Equal(BuildingOperatorChangePreflightStatus.EligibleForDesignOnly,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        fixture.Grants.Ids.Clear();
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await fixture.Gate.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, fixture.Building, fixture.Recipient));
        Assert.Equal(2, fixture.Grants.Calls);
    }

    [Fact]
    public void DELEGATE16_Grant_Revoke_Capabilities_Are_Distinct_From_Manage()
    {
        Assert.NotEqual(BuildingManagementPermissions.Buildings.AssignOperators,
            BuildingManagementPermissions.Buildings.RevokeOperators);
        Assert.NotEqual(BuildingManagementPermissions.Buildings.AssignOperators,
            BuildingManagementPermissions.Buildings.Manage);
        Assert.NotEqual(BuildingManagementPermissions.Buildings.RevokeOperators,
            BuildingManagementPermissions.Buildings.Manage);
        var names = BuildingManagementPermissions.GetAll();
        Assert.Contains(BuildingManagementPermissions.Buildings.AssignOperators, names);
        Assert.Contains(BuildingManagementPermissions.Buildings.RevokeOperators, names);
    }

    private static Fixture Valid()
    {
        var actor = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var building = Guid.NewGuid();
        var issuer = new FakeIssuer { Issuer = new(actor, true) };
        var accounts = new FakeAccounts();
        var resources = new FakeResources {
            Result = new BuildingBusinessResolvedScope(building, building)
        };
        var grants = new FakeGrants { Ids = new HashSet<Guid> { building } };
        return new Fixture(actor, recipient, building, issuer, accounts, resources,
            grants, new BuildingOperatorChangeDesignGate(
                issuer, accounts, resources, grants));
    }

    private sealed record Fixture(
        Guid Operator, Guid Recipient, Guid Building,
        FakeIssuer Issuer, FakeAccounts Accounts,
        FakeResources Resources, FakeGrants Grants,
        BuildingOperatorChangeDesignGate Gate);

    private sealed class FakeIssuer : IBuildingOperatorChangeIssuerReader
    {
        public BuildingOperatorChangeIssuer? Issuer { get; set; }
        public int Calls { get; private set; }
        public Task<BuildingOperatorChangeIssuer?> ReadAsync(
            BuildingOperatorChangeKind change)
        {
            Calls++;
            return Task.FromResult(Issuer);
        }
    }

    private sealed class FakeAccounts : IBuildingOperatorActiveAccountReader
    {
        public HashSet<Guid> Inactive { get; } = new();
        public int Calls { get; private set; }
        public Task<bool> IsActiveAsync(Guid id)
        {
            Calls++;
            return Task.FromResult(!Inactive.Contains(id));
        }
    }

    private sealed class FakeResources : IBuildingBusinessResourceScopeReader
    {
        public BuildingBusinessResolvedScope? Result { get; set; }
        public int Calls { get; private set; }
        public Task<BuildingBusinessResolvedScope?> ReadAsync(
            BuildingBusinessResourceKind kind, Guid resourceId)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeGrants : IBuildingOperatorGrantReader
    {
        public HashSet<Guid> Ids { get; set; } = new();
        public int Calls { get; private set; }
        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid operatorId)
        {
            Calls++;
            return Task.FromResult<IReadOnlyCollection<Guid>>(Ids.ToArray());
        }
    }
}
