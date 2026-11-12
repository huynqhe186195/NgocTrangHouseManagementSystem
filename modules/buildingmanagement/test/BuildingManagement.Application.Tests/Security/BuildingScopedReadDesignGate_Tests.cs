using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BuildingManagement.Security;

/// <summary>
/// No production account/grant store, HTTP endpoint or business mutations.
/// Positive outcomes are deliberately DESIGN ONLY and use test doubles.
/// </summary>
public sealed class BuildingScopedReadDesignGate_Tests
{
    [Fact]
    public async Task READGATE01_Real_Default_DenyAll_Account_Stops_Before_Store()
    {
        var f = Valid();
        var gate = new BuildingScopedReadDesignGate(
            f.Principal, new DenyAllBuildingOperatorActiveAccountReader(),
            f.Grants, f.Store);

        AssertDenied(await gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, 0, 10));
        Assert.Equal(0, f.Grants.Calls);
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task READGATE02_Real_Default_DenyAll_Grants_Stops_Before_Store()
    {
        var f = Valid();
        var gate = new BuildingScopedReadDesignGate(
            f.Principal, f.Account, new DenyAllBuildingOperatorGrantReader(),
            f.Store);
        AssertDenied(await gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Building, 0, 10));
        Assert.Equal(0, f.Store.Calls);
    }

    [Theory]
    [InlineData(BuildingScopedCollectionKind.Building, BuildingBusinessCapability.ReadBuilding)]
    [InlineData(BuildingScopedCollectionKind.Floor, BuildingBusinessCapability.ReadFloor)]
    [InlineData(BuildingScopedCollectionKind.Room, BuildingBusinessCapability.ReadRoom)]
    [InlineData(BuildingScopedCollectionKind.Contract, BuildingBusinessCapability.ReadContract)]
    public async Task READGATE03_Only_Correct_Read_Permission_Reaches_Filtered_Store(
        BuildingScopedCollectionKind kind, BuildingBusinessCapability expected)
    {
        var f = Valid();
        var result = await f.Gate.AssessPageDesignOnlyAsync(kind, 1, 3);
        Assert.Equal(BuildingScopedReadDesignStatus.EligibleForDesignOnly,
            result.Status);
        Assert.Equal(1, f.Store.Calls);
        Assert.Equal(kind, f.Store.Kind);
        Assert.Equal(expected, f.Principal.Capability);
        Assert.Equal(1, f.Store.Skip);
        Assert.Equal(3, f.Store.Take);
        Assert.Equal(new[] { f.Building }, f.Store.TrustedBuildings);
    }

    [Fact]
    public async Task READGATE04_Anonymous_Stops_Before_Account_And_Database()
    {
        var f = Valid();
        f.Principal.Principal = null;
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, 0, 5));
        Assert.Equal(0, f.Account.Calls);
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task READGATE05_Missing_Operation_Permission_Stops_Before_Database()
    {
        var f = Valid();
        f.Principal.Principal = new(f.Operator, false);
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Contract, 0, 5));
        Assert.Equal(0, f.Account.Calls);
        Assert.Equal(0, f.Grants.Calls);
    }

    [Fact]
    public async Task READGATE06_Inactive_Identity_Stops_Before_Grant_Query()
    {
        var f = Valid();
        f.Account.IsActive = false;
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Floor, 0, 5));
        Assert.Equal(0, f.Grants.Calls);
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task READGATE07_Revoked_All_Building_Grants_Stops_Store_On_Reread()
    {
        var f = Valid();
        Assert.Equal(BuildingScopedReadDesignStatus.EligibleForDesignOnly,
            (await f.Gate.AssessPageDesignOnlyAsync(
                BuildingScopedCollectionKind.Room, 0, 10)).Status);

        f.Grants.Ids = Array.Empty<Guid>();
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, 0, 10));

        Assert.Equal(2, f.Grants.Calls);
        Assert.Equal(1, f.Store.Calls);
    }

    [Fact]
    public async Task READGATE08_Unknown_Collection_And_Invalid_Page_Are_Denied_Immediately()
    {
        var f = Valid();
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            (BuildingScopedCollectionKind)999, 0, 10));
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, -1, 10));
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, 0, 0));
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, 0, 101));
        Assert.Equal(0, f.Principal.Calls);
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task READGATE09_Empty_Or_Unknown_Building_Ids_Cannot_Activate_Store()
    {
        var f = Valid();
        f.Grants.Ids = new[] { Guid.Empty };
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Building, 0, 10));

        f.Grants.Ids = null;
        AssertDenied(await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Building, 0, 10));
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task READGATE10_Trusted_Building_Ids_Are_Deduplicated_Before_Store()
    {
        var f = Valid();
        f.Grants.Ids = new[] { f.Building, f.Building, Guid.Empty };
        var result = await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Room, 0, 10);
        Assert.Equal(BuildingScopedReadDesignStatus.EligibleForDesignOnly,
            result.Status);
        Assert.Equal(new[] { f.Building }, f.Store.TrustedBuildings);
    }

    [Fact]
    public async Task READGATE11_Page_Values_Do_Not_Create_Extra_Data()
    {
        var f = Valid();
        f.Store.Page = new BuildingScopedListPage(2, new[] { Guid.NewGuid() });
        var result = await f.Gate.AssessPageDesignOnlyAsync(
            BuildingScopedCollectionKind.Contract, 1, 1);
        Assert.Equal(2, result.Page.TotalCount);
        Assert.Single(result.Page.ResourceIds);
    }

    private static void AssertDenied(BuildingScopedReadDesignResult result)
    {
        Assert.Equal(BuildingScopedReadDesignStatus.Denied, result.Status);
        Assert.Equal(0, result.Page.TotalCount);
        Assert.Empty(result.Page.ResourceIds);
    }

    private static Fixture Valid()
    {
        var actor = Guid.NewGuid();
        var building = Guid.NewGuid();
        var principal = new FakePrincipal {
            Principal = new BuildingBusinessPrincipal(actor, true)
        };
        var account = new FakeAccount { IsActive = true };
        var grants = new FakeGrants { Ids = new[] { building } };
        var store = new FakeStore {
            Page = new BuildingScopedListPage(1, new[] { building })
        };
        return new Fixture(actor, building, principal, account, grants, store,
            new BuildingScopedReadDesignGate(principal, account, grants, store));
    }

    private sealed record Fixture(
        Guid Operator, Guid Building,
        FakePrincipal Principal, FakeAccount Account,
        FakeGrants Grants, FakeStore Store,
        BuildingScopedReadDesignGate Gate);

    private sealed class FakePrincipal : IBuildingBusinessPrincipalReader
    {
        public BuildingBusinessPrincipal? Principal { get; set; }
        public int Calls { get; private set; }
        public BuildingBusinessCapability Capability { get; private set; }

        public Task<BuildingBusinessPrincipal?> ReadAsync(
            BuildingBusinessCapability capability)
        {
            Calls++;
            Capability = capability;
            return Task.FromResult(Principal);
        }
    }

    private sealed class FakeAccount : IBuildingOperatorActiveAccountReader
    {
        public bool IsActive { get; set; }
        public int Calls { get; private set; }
        public Task<bool> IsActiveAsync(Guid userId)
        {
            Calls++;
            return Task.FromResult(IsActive);
        }
    }

    private sealed class FakeGrants : IBuildingOperatorGrantReader
    {
        public IReadOnlyCollection<Guid>? Ids { get; set; }
        public int Calls { get; private set; }
        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid userId)
        {
            Calls++;
            return Task.FromResult(Ids!);
        }
    }

    private sealed class FakeStore : IBuildingScopedCollectionStore
    {
        public BuildingScopedListPage Page { get; set; } =
            new(0, Array.Empty<Guid>());
        public int Calls { get; private set; }
        public BuildingScopedCollectionKind Kind { get; private set; }
        public int Skip { get; private set; }
        public int Take { get; private set; }
        public Guid[] TrustedBuildings { get; private set; } = Array.Empty<Guid>();

        public Task<BuildingScopedListPage> ReadPageAsync(
            BuildingScopedCollectionKind kind,
            IReadOnlyCollection<Guid> trustedBuildingIds,
            int skipCount,
            int maxResultCount,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Kind = kind;
            Skip = skipCount;
            Take = maxResultCount;
            TrustedBuildings = trustedBuildingIds.ToArray();
            return Task.FromResult(Page);
        }
    }
}
