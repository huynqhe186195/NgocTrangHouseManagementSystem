using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Volo.Abp.Authorization;
using Volo.Abp.Users;
using Xunit;

namespace BuildingManagement.OperatorMemberships;

/// <summary>
/// Application-boundary tests for the read-only operator membership roster.
/// These tests use only server-side fakes; they do not grant any live user,
/// register a production provider or touch a database.
/// </summary>
public sealed class OperatorMembershipsAppService_Tests
{
    private static readonly DateTime Now =
        new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task OPLIST01_Missing_Read_Authorization_Denies_Before_Store()
    {
        var fixture = Fixture.Create();
        fixture.Policy.Deny = true;

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync());

        Assert.Equal(1, fixture.Policy.Calls);
        Assert.Equal(0, fixture.Store.Calls);
    }

    [Fact]
    public async Task OPLIST02_Actor_Sees_Only_Server_Granted_Building_A()
    {
        var buildingA = Guid.NewGuid();
        var buildingB = Guid.NewGuid();
        var row = Row(buildingA, "Tòa A");
        var fixture = Fixture.Create(buildingA);
        fixture.Policy.Authorized = new[] { buildingA };
        fixture.Store.Page = new(1, new[] { row });

        var result = await fixture.Service.GetListAsync();

        Assert.Single(result.Items);
        Assert.Equal(buildingA, result.Items[0].BuildingId);
        Assert.DoesNotContain(buildingB, fixture.Store.TrustedBuildingIds);
        Assert.Equal(new[] { buildingA }, fixture.Store.TrustedBuildingIds);
    }

    [Fact]
    public async Task OPLIST03_Actor_With_Two_Buildings_Sees_Exactly_A_And_B()
    {
        var buildingA = Guid.NewGuid();
        var buildingB = Guid.NewGuid();
        var fixture = Fixture.Create(buildingA);
        fixture.Policy.Authorized = new[] { buildingB, buildingA };
        fixture.Store.Page = new(2, new[]
        {
            Row(buildingA, "Tòa A"),
            Row(buildingB, "Tòa B")
        });

        var result = await fixture.Service.GetListAsync();

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { buildingB, buildingA },
            fixture.Store.TrustedBuildingIds);
        Assert.Equal(new[] { buildingA, buildingB },
            result.Items.Select(x => x.BuildingId));
    }

    [Fact]
    public async Task OPLIST04_Expired_Future_And_Revoked_Rows_Map_To_Explicit_Statuses()
    {
        var building = Guid.NewGuid();
        var fixture = Fixture.Create(building);
        fixture.Store.Page = new(4, new[]
        {
            Row(building, "Tòa A", grantedAt: Now.AddHours(-2), expiresAt: Now.AddHours(1)),
            Row(building, "Tòa A", grantedAt: Now.AddHours(-2), expiresAt: Now),
            Row(building, "Tòa A", grantedAt: Now.AddHours(1)),
            Row(building, "Tòa A", grantedAt: Now.AddHours(-2), revokedAt: Now.AddMinutes(-1))
        });

        var result = await fixture.Service.GetListAsync();

        Assert.Equal(new[]
        {
            OperatorMembershipStatus.Active,
            OperatorMembershipStatus.Expired,
            OperatorMembershipStatus.Scheduled,
            OperatorMembershipStatus.Revoked
        }, result.Items.Select(x => x.Status));
    }

    [Fact]
    public async Task OPLIST05_Guessed_Building_Filter_Outside_Scope_Is_Denied()
    {
        var allowed = Guid.NewGuid();
        var guessed = Guid.NewGuid();
        var fixture = Fixture.Create(allowed);
        fixture.Policy.Authorized = new[] { allowed };

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync(guessed));

        Assert.Equal(0, fixture.Store.Calls);
    }

    [Fact]
    public async Task OPLIST06_Trusted_Filter_Is_Passed_To_Store_With_Bounded_Paging()
    {
        var building = Guid.NewGuid();
        var fixture = Fixture.Create(building);

        await fixture.Service.GetListAsync(building, skipCount: 50, maxResultCount: 100);

        Assert.Equal(building, fixture.Store.TrustedBuildingFilter);
        Assert.Equal(50, fixture.Store.SkipCount);
        Assert.Equal(100, fixture.Store.MaxResultCount);
        Assert.Equal(BuildingBusinessCapability.ReadOperators,
            fixture.Policy.LastCapability);
    }

    [Fact]
    public async Task OPLIST07_Anonymous_Or_Inactive_Actor_Is_Denied()
    {
        var fixture = Fixture.Create();
        fixture.User.Authenticated = false;

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync());
        Assert.Equal(0, fixture.Store.Calls);

        fixture = Fixture.Create();
        fixture.Policy.Deny = true; // models inactive/locked account denial
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync());
        Assert.Equal(0, fixture.Store.Calls);
    }

    [Fact]
    public async Task OPLIST08_Response_Contains_No_Identity_Profile_Or_Client_Claims()
    {
        var building = Guid.NewGuid();
        var fixture = Fixture.Create(building);
        fixture.Store.Page = new(1, new[] { Row(building, "Tòa A") });

        var result = await fixture.Service.GetListAsync();
        var dto = Assert.Single(result.Items);
        var names = typeof(OperatorMembershipSummaryDto)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("UserName", names);
        Assert.DoesNotContain("Email", names);
        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("Claims", names);
        Assert.NotEqual(Guid.Empty, dto.OperatorUserId);
    }

    [Fact]
    public void OPLIST09_Contract_Is_Read_Only_And_Has_No_Assign_Or_Revoke_Method()
    {
        var methods = typeof(IOperatorMembershipsAppService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(IOperatorMembershipsAppService.GetListAsync) }, methods);
        Assert.DoesNotContain(methods, name =>
            name.Contains("Assign", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Revoke", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OPLIST10_Invalid_Page_Is_Rejected_Before_Authorization_Store()
    {
        var fixture = Fixture.Create();

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync(skipCount: -1));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync(maxResultCount: 101));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Service.GetListAsync(Guid.Empty));

        Assert.Equal(0, fixture.Policy.Calls);
        Assert.Equal(0, fixture.Store.Calls);
    }

    private static OperatorMembershipReadModel Row(
        Guid buildingId,
        string buildingName,
        DateTime? grantedAt = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            buildingId,
            buildingName,
            Guid.NewGuid(),
            grantedAt ?? Now,
            expiresAt,
            revokedAt,
            1);

    private sealed class Fixture
    {
        public Guid Actor { get; }
        public FakePolicy Policy { get; }
        public FakeStore Store { get; }
        public CurrentUserProxy User { get; }
        public OperatorMembershipsAppService Service { get; }

        private Fixture(Guid actor, FakePolicy policy, FakeStore store,
            CurrentUserProxy user, ICurrentUser currentUser)
        {
            Actor = actor;
            Policy = policy;
            Store = store;
            User = user;
            Service = new OperatorMembershipsAppService(
                policy, store, currentUser, new FixedClock(Now));
        }

        public static Fixture Create(Guid? authorizedBuilding = null)
        {
            var actor = Guid.NewGuid();
            var policy = new FakePolicy
            {
                Authorized = authorizedBuilding.HasValue
                    ? new[] { authorizedBuilding.Value }
                    : new[] { Guid.NewGuid() }
            };
            var store = new FakeStore();
            var user = DispatchProxy.Create<ICurrentUser, CurrentUserProxy>();
            var proxy = (CurrentUserProxy)(object)user;
            proxy.User = actor;
            proxy.Authenticated = true;
            return new Fixture(actor, policy, store, proxy, user);
        }
    }

    private sealed class FakePolicy : IBuildingCrudAccessPolicy
    {
        public IReadOnlyCollection<Guid> Authorized { get; set; } = Array.Empty<Guid>();
        public bool Deny { get; set; }
        public int Calls { get; private set; }
        public BuildingBusinessCapability LastCapability { get; private set; }

        public Task<IReadOnlyCollection<Guid>> GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability capability)
        {
            Calls++;
            LastCapability = capability;
            if (Deny || capability != BuildingBusinessCapability.ReadOperators)
                throw new AbpAuthorizationException("Denied test policy.");
            return Task.FromResult(Authorized);
        }

        public Task EnsureBuildingAsync(Guid buildingId,
            BuildingBusinessCapability capability) => throw NotUsed();

        public Task EnsureResourceAsync(BuildingBusinessResourceKind kind,
            Guid resourceId, BuildingBusinessCapability capability)
            => throw NotUsed();

        public Task EnsureBuildingCreationAsync() => throw NotUsed();

        private static AbpAuthorizationException NotUsed()
            => new("Not used by operator membership tests.");
    }

    private sealed class FakeStore : IOperatorMembershipReadStore
    {
        public OperatorMembershipReadPage Page { get; set; } =
            new(0, Array.Empty<OperatorMembershipReadModel>());
        public int Calls { get; private set; }
        public Guid TrustedOperatorId { get; private set; }
        public IReadOnlyCollection<Guid> TrustedBuildingIds { get; private set; } =
            Array.Empty<Guid>();
        public Guid? TrustedBuildingFilter { get; private set; }
        public int SkipCount { get; private set; }
        public int MaxResultCount { get; private set; }

        public Task<OperatorMembershipReadPage> ReadPageAsync(
            Guid trustedOperatorId,
            IReadOnlyCollection<Guid> trustedBuildingIds,
            Guid? trustedBuildingFilter,
            DateTime trustedNowUtc,
            int skipCount,
            int maxResultCount)
        {
            Calls++;
            TrustedOperatorId = trustedOperatorId;
            TrustedBuildingIds = trustedBuildingIds;
            TrustedBuildingFilter = trustedBuildingFilter;
            SkipCount = skipCount;
            MaxResultCount = maxResultCount;
            Assert.Equal(Now, trustedNowUtc);
            return Task.FromResult(Page);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTime now) => _now = new(now);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    public class CurrentUserProxy : DispatchProxy
    {
        public Guid? User { get; set; }
        public bool Authenticated { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name switch
            {
                "get_IsAuthenticated" => Authenticated,
                "get_Id" => User,
                _ => throw new InvalidOperationException(
                    "Unexpected ICurrentUser method: " + method?.Name)
            };
    }
}
