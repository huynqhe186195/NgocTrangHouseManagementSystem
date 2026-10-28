using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Floors;
using BuildingManagement.Permissions;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NgocTrangHouseManagementSystem.Security;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Identity;
using Volo.Abp.Identity.EntityFrameworkCore;
using IdentityUser = Volo.Abp.Identity.IdentityUser;
using Volo.Abp.Users;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// Isolated Slice A composition: real ABP IdentityUser entity, inherited
/// ASP.NET Identity UserManager lockout algorithm, production ABP
/// principal/permission readers, SQLite-persisted building grants and
/// resource links. Test-only repository/permission adapters have NO DI
/// registration. This does NOT test the production ABP Identity database,
/// role-permission storage, or locked PostgreSQL write transactions.
/// </summary>
public sealed class BuildingIdentityMembershipComposition_Tests
{
    [Fact]
    public async Task IDMEM01_Active_Account_With_Permission_Only_Reads_Own_Building()
    {
        await using var f = await Fixture.CreateAsync();
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);
        await f.GrantAsync(f.Actor, f.BuildingA);

        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);
        Assert.Equal(new[] { f.BuildingA },
            await f.Policy.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadBuilding));

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingB,
                BuildingBusinessCapability.ReadBuilding));
        Assert.Equal(BuildingManagementPermissions.Buildings.Read,
            f.Permissions.LastPermission);
    }

    [Fact]
    public async Task IDMEM02_Actual_IdentityUser_IsActive_False_Denies_Valid_Membership()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);
        f.Accounts[f.Actor].SetIsActive(false);

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));
        Assert.True(f.AccountsRepo.Calls > 0);
    }

    [Fact]
    public async Task IDMEM03_Actual_UserManager_Lockout_Check_Denies_Stored_Membership()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);
        f.Lockouts.Set(f.Actor, true, DateTimeOffset.UtcNow.AddMinutes(20));

        Assert.True(await f.Manager.IsLockedOutAsync(f.Accounts[f.Actor]));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));

        f.Lockouts.Set(f.Actor, true, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False(await f.Manager.IsLockedOutAsync(f.Accounts[f.Actor]));
        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);
    }

    [Fact]
    public async Task IDMEM04_Missing_Or_Disabled_Lockout_Account_Still_Fails_Closed()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);

        f.Accounts.Remove(f.Actor);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));

        f.Accounts[f.Actor] = NewAccount(f.Actor);
        f.Lockouts.Set(f.Actor, false, DateTimeOffset.UtcNow.AddHours(2));
        // ASP.NET Identity itself considers lockout disabled -> not locked.
        Assert.False(await f.Manager.IsLockedOutAsync(f.Accounts[f.Actor]));
        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);
    }

    [Fact]
    public async Task IDMEM05_Missing_Manage_Permission_Denies_Even_With_Read_And_Grant()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);
        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ManageBuilding));
        Assert.Equal(BuildingManagementPermissions.Buildings.Manage,
            f.Permissions.LastPermission);
    }

    [Fact]
    public async Task IDMEM06_Cross_Building_Floor_And_Room_Guessed_Guids_Denied()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.UnionWith(new[]
        {
            BuildingManagementPermissions.Floors.Read,
            BuildingManagementPermissions.Rooms.Read
        });

        await f.Policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Floor, f.FloorA,
            BuildingBusinessCapability.ReadFloor);
        await f.Policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, f.RoomA,
            BuildingBusinessCapability.ReadRoom);

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, f.FloorB,
                BuildingBusinessCapability.ReadFloor));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, f.RoomB,
                BuildingBusinessCapability.ReadRoom));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, Guid.NewGuid(),
                BuildingBusinessCapability.ReadRoom));
    }

    [Fact]
    public async Task IDMEM07_Grant_Revocation_Immediately_Invalidates_Access()
    {
        await using var f = await Fixture.CreateAsync();
        var grant = await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);
        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);

        await using (var writer = f.Store.Open())
        {
            var row = await writer.Grants.SingleAsync(x => x.Id == grant);
            row.Revoke(f.Other, DateTime.UtcNow);
            await writer.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));
    }

    [Fact]
    public async Task IDMEM08_Server_Principal_And_Operation_Permission_Are_Required()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);

        f.Principal.Authenticated = false;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));

        f.Principal.Authenticated = true;
        f.Principal.User = Guid.NewGuid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));

        f.Principal.User = f.Actor;
        f.Permissions.Allowed.Clear();
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));
    }

    [Fact]
    public async Task IDMEM09_Separate_ABP_Delegation_Permission_Never_Follows_Manage()
    {
        await using var f = await Fixture.CreateAsync();
        await f.GrantAsync(f.Actor, f.BuildingA);
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Manage);
        var issuers = new AbpBuildingOperatorChangeIssuerReader(
            f.CurrentUser, f.PermissionChecker);
        var delegation = new BuildingOperatorChangeDesignGate(
            issuers, f.AccountProbe, f.Scope, f.GrantReader);

        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await delegation.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, f.BuildingA, f.Other));
        f.Permissions.Allowed.Add(
            BuildingManagementPermissions.Buildings.AssignOperators);
        Assert.Equal(BuildingOperatorChangePreflightStatus.EligibleForDesignOnly,
            await delegation.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, f.BuildingA, f.Other));
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await delegation.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, f.BuildingB, f.Other));
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await delegation.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, f.BuildingA, f.Actor));

        f.Accounts[f.Other].SetIsActive(false);
        Assert.Equal(BuildingOperatorChangePreflightStatus.Denied,
            await delegation.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Assign, f.BuildingA, f.Other));

        f.Permissions.Allowed.Add(
            BuildingManagementPermissions.Buildings.RevokeOperators);
        Assert.Equal(BuildingOperatorChangePreflightStatus.EligibleForDesignOnly,
            await delegation.AssessDesignOnlyAsync(
                BuildingOperatorChangeKind.Revoke, f.BuildingA, f.Other));
    }

    [Fact]
    public async Task IDMEM11_ABP_Identity_Entities_Persist_Account_Lockout_And_Revocation()
    {
        await using var f = await Fixture.CreateAsync();
        await using var identities = await IdentityStore.CreateAsync();
        await using (var initial = identities.Open())
        {
            initial.Users.Add(NewAccount(f.Actor));
            await initial.SaveChangesAsync();
        }

        // Test-only IIdentityUserRepository adapter uses the ACTUAL ABP
        // IdentityUser EF mapping and a fresh AsNoTracking query each time.
        // Production Identity DbContext, repository and permissions are not
        // reconfigured or registered by this isolated test.
        f.AccountsRepo.ResolveAsync = async id =>
        {
            await using var db = identities.Open();
            return await db.Users.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == id && !x.IsDeleted);
        };
        f.Lockouts.ReadFromEntity = true;
        f.Permissions.Allowed.Add(BuildingManagementPermissions.Buildings.Read);
        await f.GrantAsync(f.Actor, f.BuildingA);

        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);

        await using (var db = identities.Open())
        {
            var user = await db.Users.SingleAsync(x => x.Id == f.Actor);
            user.SetIsActive(false);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));

        await using (var db = identities.Open())
        {
            var user = await db.Users.SingleAsync(x => x.Id == f.Actor);
            user.SetIsActive(true);
            db.Entry(user).Property(x => x.LockoutEnabled).CurrentValue = true;
            db.Entry(user).Property(x => x.LockoutEnd).CurrentValue =
                DateTimeOffset.UtcNow.AddMinutes(15);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingA,
                BuildingBusinessCapability.ReadBuilding));

        await using (var db = identities.Open())
        {
            var user = await db.Users.SingleAsync(x => x.Id == f.Actor);
            db.Entry(user).Property(x => x.LockoutEnd).CurrentValue =
                DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await f.Policy.EnsureBuildingAsync(f.BuildingA,
            BuildingBusinessCapability.ReadBuilding);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Policy.EnsureBuildingAsync(f.BuildingB,
                BuildingBusinessCapability.ReadBuilding));
    }

    [Fact]
    public async Task IDMEM12_Only_Explicit_Root_And_Permission_Can_Create_First_Owner()
    {
        await using var f = await Fixture.CreateAsync();
        var roots = new BuildingOwnerBootstrapSecurityOptions();
        var issuer = new AbpBuildingOwnerBootstrapPrincipalReader(
            f.CurrentUser, f.PermissionChecker, Options.Create(roots));
        var coordinator = new BuildingOwnerBootstrapCoordinator(
            issuer, f.AccountProbe,
            new EfBuildingOwnerBootstrapAtomicWriter(f.StoreReader));
        var input = new CreateBuildingDto
        {
            Name = "Root bootstrap", Address = "SQLite scratch only"
        };

        // Even BootstrapOwner role permission is insufficient without
        // explicit server-side root designation.
        f.Permissions.Allowed.Add(
            BuildingManagementPermissions.Buildings.BootstrapOwner);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            coordinator.CreateWithCurrentOwnerAsync(input));

        roots.RootUserIds.Add(f.Actor);
        f.Permissions.Allowed.Clear();
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            coordinator.CreateWithCurrentOwnerAsync(input));

        f.Permissions.Allowed.Add(
            BuildingManagementPermissions.Buildings.BootstrapOwner);
        var created = await coordinator.CreateWithCurrentOwnerAsync(input);
        await using (var verify = f.Store.Open())
        {
            var grant = await verify.Grants.SingleAsync(g =>
                g.BuildingId == created.Id);
            Assert.Equal(f.Actor, grant.OperatorUserId);
            Assert.Equal(f.Actor, grant.GrantedByUserId);
            Assert.Equal(3, await verify.Buildings.CountAsync());
        }

        f.Principal.User = f.Other;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            coordinator.CreateWithCurrentOwnerAsync(input));
        f.Principal.User = f.Actor;
        f.Accounts[f.Actor].SetIsActive(false);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            coordinator.CreateWithCurrentOwnerAsync(input));
        await using (var verify = f.Store.Open())
            Assert.Equal(3, await verify.Buildings.CountAsync());
    }

    [Fact]
    public async Task IDMEM10_Production_DI_Does_Not_Use_Identity_Or_Grant_Candidates()
    {
        // The cross-building positives above are ONLY for explicitly
        // instantiated test adapters. Do not replace ABP runtime defaults.
        Assert.False(await new DenyAllBuildingOperatorActiveAccountReader()
            .IsActiveAsync(Guid.NewGuid()));
        Assert.Empty(await new DenyAllBuildingOperatorGrantReader()
            .GetActiveBuildingIdsAsync(Guid.NewGuid()));

        Assert.Null(typeof(AbpIdentityBuildingOperatorAccountProbe)
            .GetCustomAttributes(inherit: true)
            .FirstOrDefault(x => x.GetType().Name.Contains("ExposeServices")));
    }

    private static IdentityUser NewAccount(Guid id)
        => new(id, "isolated-" + id.ToString("N"),
            "isolated-" + id.ToString("N") + "@example.test");

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        public Guid BuildingA { get; } = Guid.NewGuid();
        public Guid BuildingB { get; } = Guid.NewGuid();
        public Guid FloorA { get; } = Guid.NewGuid();
        public Guid FloorB { get; } = Guid.NewGuid();
        public Guid RoomA { get; } = Guid.NewGuid();
        public Guid RoomB { get; } = Guid.NewGuid();
        public Dictionary<Guid, IdentityUser> Accounts { get; } = new();
        public IdentityRepositoryProxy AccountsRepo { get; }
        public PermissionProxy Permissions { get; }
        public CurrentUserProxy Principal { get; }
        public ICurrentUser CurrentUser { get; }
        public IPermissionChecker PermissionChecker { get; }
        public LockoutStore Lockouts { get; } = new();
        public UserManager<IdentityUser> Manager { get; }
        public AbpIdentityBuildingOperatorAccountProbe AccountProbe { get; }
        public Store Store { get; }
        public Db StoreReader { get; }
        public StoredScope Scope { get; }
        public IBuildingOperatorGrantReader GrantReader { get; }
        public BuildingCrudAccessPolicy Policy { get; }

        private Fixture(Store store)
        {
            Store = store;
            StoreReader = store.Open();
            Accounts[Actor] = NewAccount(Actor);
            Accounts[Other] = NewAccount(Other);

            var identityRepo = DispatchProxy.Create<
                IIdentityUserRepository, IdentityRepositoryProxy>();
            AccountsRepo = (IdentityRepositoryProxy)(object)identityRepo;
            AccountsRepo.Accounts = Accounts;

            CurrentUser = DispatchProxy.Create<ICurrentUser, CurrentUserProxy>();
            Principal = (CurrentUserProxy)(object)CurrentUser;
            Principal.User = Actor;
            Principal.Authenticated = true;

            PermissionChecker = DispatchProxy.Create<
                IPermissionChecker, PermissionProxy>();
            Permissions = (PermissionProxy)(object)PermissionChecker;

            Manager = new UserManager<IdentityUser>(
                Lockouts, Options.Create(new IdentityOptions()),
                new PasswordHasher<IdentityUser>(),
                Array.Empty<IUserValidator<IdentityUser>>(),
                Array.Empty<IPasswordValidator<IdentityUser>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(), null!,
                NullLogger<UserManager<IdentityUser>>.Instance);

            AccountProbe = new AbpIdentityBuildingOperatorAccountProbe(
                identityRepo, Manager.IsLockedOutAsync);
            Scope = new StoredScope(StoreReader);
            GrantReader = new StoredGrants(StoreReader);
            Policy = new BuildingCrudAccessPolicy(
                new AbpBuildingBusinessPrincipalReader(
                    CurrentUser, PermissionChecker),
                AccountProbe, GrantReader, Scope);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var store = await Store.CreateAsync();
            var f = new Fixture(store);
            await using var db = store.Open();
            db.Buildings.AddRange(
                new Building(f.BuildingA, "Building A", "A"),
                new Building(f.BuildingB, "Building B", "B"));
            db.Floors.AddRange(
                new Floor(f.FloorA, f.BuildingA, 1, "A1"),
                new Floor(f.FloorB, f.BuildingB, 1, "B1"));
            db.Rooms.AddRange(
                new Room(f.RoomA, f.FloorA, "101", "A room", 25, 2,
                    RoomStatus.Available),
                new Room(f.RoomB, f.FloorB, "101", "B room", 25, 2,
                    RoomStatus.Available));
            await db.SaveChangesAsync();
            return f;
        }

        public async Task<Guid> GrantAsync(Guid user, Guid building)
        {
            var id = Guid.NewGuid();
            await using var writer = Store.Open();
            writer.Grants.Add(BuildingOperatorAccessGrant.Issue(
                id, user, building, Other, DateTime.UtcNow.AddDays(-1), null));
            await writer.SaveChangesAsync();
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            await StoreReader.DisposeAsync();
            Store.Dispose();
        }
    }

    // The fake repository is intentionally explicit: IdentityUser objects
    // are real ABP Domain entities, but lookup is not a live Identity DbContext.
    public class IdentityRepositoryProxy : DispatchProxy
    {
        public Dictionary<Guid, IdentityUser> Accounts { get; set; } = new();
        public Func<Guid, Task<IdentityUser?>>? ResolveAsync { get; set; }
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "FindAsync" && args?.FirstOrDefault() is Guid id)
            {
                Calls++;
                if (ResolveAsync is not null)
                    return ResolveAsync(id);
                return Task.FromResult(
                    Accounts.TryGetValue(id, out var user) ? user : null);
            }
            throw new InvalidOperationException(
                "Unexpected ABP Identity repository method: " + method?.Name);
        }
    }

    public class PermissionProxy : DispatchProxy
    {
        public HashSet<string> Allowed { get; } = new(StringComparer.Ordinal);
        public string? LastPermission { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "IsGrantedAsync")
            {
                LastPermission = args?.OfType<string>().FirstOrDefault();
                return Task.FromResult(LastPermission is not null &&
                    Allowed.Contains(LastPermission));
            }
            throw new InvalidOperationException(
                "Unexpected ABP permission method: " + method?.Name);
        }
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

    private sealed class StoredGrants : IBuildingOperatorGrantReader
    {
        private readonly EfBuildingOperatorGrantCandidateReader _candidate;
        public StoredGrants(Db db) => _candidate =
            new EfBuildingOperatorGrantCandidateReader(db, TimeProvider.System);
        public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid actor)
            => _candidate.GetActiveBuildingIdsAsync(actor);
    }

    private sealed class StoredScope : IBuildingBusinessResourceScopeReader
    {
        private readonly Db _db;
        public StoredScope(Db db) => _db = db;

        public async Task<BuildingBusinessResolvedScope?> ReadAsync(
            BuildingBusinessResourceKind kind, Guid id)
        {
            if (id == Guid.Empty) return null;
            Guid building;
            switch (kind)
            {
                case BuildingBusinessResourceKind.Building:
                    building = id;
                    break;
                case BuildingBusinessResourceKind.Floor:
                    var floor = await _db.Floors.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == id);
                    if (floor is null) return null;
                    building = floor.BuildingId;
                    break;
                case BuildingBusinessResourceKind.Room:
                    var room = await _db.Rooms.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == id);
                    if (room is null) return null;
                    var parent = await _db.Floors.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == room.FloorId);
                    if (parent is null) return null;
                    building = parent.BuildingId;
                    break;
                default: return null;
            }
            if (!await _db.Buildings.AsNoTracking()
                .AnyAsync(x => x.Id == building && !x.IsDeleted))
                return null;
            return new BuildingBusinessResolvedScope(id, building);
        }
    }

    public sealed class LockoutStore : IUserLockoutStore<IdentityUser>
    {
        private readonly Dictionary<Guid, (bool Enabled, DateTimeOffset? End)> _state = new();
        public bool ReadFromEntity { get; set; }
        public void Set(Guid user, bool enabled, DateTimeOffset? end)
            => _state[user] = (enabled, end);
        private (bool Enabled, DateTimeOffset? End) State(IdentityUser u)
            => ReadFromEntity ? (u.LockoutEnabled, u.LockoutEnd) :
                _state.TryGetValue(u.Id, out var s) ? s : (true, null);

        public Task<bool> GetLockoutEnabledAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(State(u).Enabled);
        public Task<DateTimeOffset?> GetLockoutEndDateAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(State(u).End);
        public Task SetLockoutEndDateAsync(IdentityUser u, DateTimeOffset? end, CancellationToken ct)
        {
            var s = State(u); Set(u.Id, s.Enabled, end); return Task.CompletedTask;
        }
        public Task SetLockoutEnabledAsync(IdentityUser u, bool enabled, CancellationToken ct)
        {
            var s = State(u); Set(u.Id, enabled, s.End); return Task.CompletedTask;
        }
        public Task<int> IncrementAccessFailedCountAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(0);
        public Task ResetAccessFailedCountAsync(IdentityUser u, CancellationToken ct)
            => Task.CompletedTask;
        public Task<int> GetAccessFailedCountAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(0);
        public Task<string> GetUserIdAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(u.Id.ToString("D"));
        public Task<string?> GetUserNameAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult<string?>(u.UserName);
        public Task SetUserNameAsync(IdentityUser u, string? name, CancellationToken ct)
            => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult<string?>(u.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(IdentityUser u, string? name, CancellationToken ct)
            => Task.CompletedTask;
        public Task<IdentityUser?> FindByIdAsync(string id, CancellationToken ct)
            => Task.FromResult<IdentityUser?>(null);
        public Task<IdentityUser?> FindByNameAsync(string name, CancellationToken ct)
            => Task.FromResult<IdentityUser?>(null);
        public Task<IdentityResult> CreateAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> UpdateAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(IdentityUser u, CancellationToken ct)
            => Task.FromResult(IdentityResult.Success);
        public void Dispose() { }
    }

    private sealed class IdentityStore : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<IdentityDb> _options;

        private IdentityStore(SqliteConnection connection)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<IdentityDb>()
                .UseSqlite(connection).Options;
        }

        public static async Task<IdentityStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var store = new IdentityStore(connection);
            await using var db = store.Open();
            await db.Database.EnsureCreatedAsync();
            return store;
        }

        public IdentityDb Open() => new(_options);
        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class IdentityDb : DbContext
    {
        public IdentityDb(DbContextOptions<IdentityDb> options) : base(options) { }
        public DbSet<IdentityUser> Users => Set<IdentityUser>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // The SAME ABP Identity mapping invoked by the application's
            // real NgocTrangHouseManagementSystemDbContext.
            builder.ConfigureIdentity();
        }
    }

    private sealed class Store : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<Db> _options;
        private Store(SqliteConnection conn)
        {
            _connection = conn;
            _options = new DbContextOptionsBuilder<Db>()
                .UseSqlite(conn).Options;
        }
        public static async Task<Store> CreateAsync()
        {
            var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync();
            var store = new Store(conn);
            await using var db = store.Open();
            await db.Database.EnsureCreatedAsync();
            return store;
        }
        public Db Open() => new(_options);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class Db : DbContext
    {
        public Db(DbContextOptions<Db> options) : base(options) { }
        public DbSet<Building> Buildings => Set<Building>();
        public DbSet<Floor> Floors => Set<Floor>();
        public DbSet<Room> Rooms => Set<Room>();
        public DbSet<BuildingOperatorAccessGrant> Grants
            => Set<BuildingOperatorAccessGrant>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.ApplyConfiguration(new BuildingConfiguration());
            builder.ApplyConfiguration(new FloorConfiguration());
            builder.ApplyConfiguration(new RoomConfiguration());
            builder.ApplyConfiguration(new BuildingOperatorAccessGrantDesignConfiguration());
        }
    }
}
