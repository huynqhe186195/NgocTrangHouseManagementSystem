using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BuildingManagement.Application.Mobile;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.RentalAccounts;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore.RentalAccounts;

/// <summary>
/// SQLite-backed scope tests for the first RentalAccount mobile read slice.
/// IdentityUser rows are intentionally represented only by authenticated subject
/// ids; no Identity database or production migration is used.
/// </summary>
public sealed class RentalAccountMobileScopeSqlite_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Tenant, Guid> _tenants;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<ContractTenant, Guid> _contractTenants;
    private readonly IRepository<RentalAccount, Guid> _accounts;
    private readonly IRepository<RentalAccountUser, Guid> _accountUsers;

    public RentalAccountMobileScopeSqlite_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _tenants = GetRequiredService<IRepository<Tenant, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _contractTenants = GetRequiredService<IRepository<ContractTenant, Guid>>();
        _accounts = GetRequiredService<IRepository<RentalAccount, Guid>>();
        _accountUsers = GetRequiredService<IRepository<RentalAccountUser, Guid>>();
    }

    [Fact]
    public async Task RACCOUNT01_User_A_Sees_Only_Account_A_Graph()
    {
        var fixture = await SeedAsync();
        var service = CreateService(out var currentUser);
        await WithUnitOfWorkAsync(async () =>
        {
            currentUser.Set(fixture.UserA);

            var result = await service.GetMeAsync();

            Assert.Equal(fixture.AccountA, result.Account.Id);
            Assert.Equal(fixture.ContractA, result.ActiveContract?.Id);
            Assert.Equal(fixture.RoomA, result.ActiveContract?.Room.Id);
            Assert.Equal(fixture.BuildingA, result.ActiveContract?.Room.BuildingId);
            Assert.Contains(result.Members, x => x.TenantId == fixture.TenantA);
            Assert.DoesNotContain(result.Members, x => x.TenantId == fixture.TenantB);
            Assert.DoesNotContain(result.Members, x => x.DisplayName == "Tenant B private PII");
        });
    }

    [Fact]
    public async Task RACCOUNT02_User_B_Cannot_Read_Account_A()
    {
        var fixture = await SeedAsync();
        var service = CreateService(out var currentUser);
        await WithUnitOfWorkAsync(async () =>
        {
            currentUser.Set(fixture.UserB);

            var result = await service.GetMeAsync();

            Assert.Equal(fixture.AccountB, result.Account.Id);
            Assert.NotEqual(fixture.AccountA, result.Account.Id);
            Assert.Equal(fixture.ContractB, result.ActiveContract?.Id);
            Assert.DoesNotContain(result.Members, x => x.TenantId == fixture.TenantA);
            Assert.DoesNotContain(result.Members,
                x => x.DisplayName == "Tenant A private PII");
        });
    }

    [Fact]
    public async Task RACCOUNT03_Two_Identity_Users_Can_Read_One_Account()
    {
        var fixture = await SeedAsync();
        var service = CreateService(out var currentUser);
        await WithUnitOfWorkAsync(async () =>
        {
            currentUser.Set(fixture.UserA);
            var primary = await service.GetMeAsync();
            currentUser.Set(fixture.UserARepresentative);
            var representative = await service.GetMeAsync();

            Assert.Equal(fixture.AccountA, primary.Account.Id);
            Assert.Equal(fixture.AccountA, representative.Account.Id);
            Assert.Equal(RentalAccountAccessRole.PrimaryTenant, primary.CurrentUser.AccessRole);
            Assert.Equal(RentalAccountAccessRole.AuthorizedRepresentative,
                representative.CurrentUser.AccessRole);
        });
    }

    [Fact]
    public async Task RACCOUNT04_Revoked_Link_Is_Denied_Before_PII_Read()
    {
        var fixture = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var link = await _accountUsers.GetAsync(fixture.AccountUserA);
            link.Revoke(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
            await _accountUsers.UpdateAsync(link, autoSave: true);
        });

        var service = CreateService(out var currentUser);
        await WithUnitOfWorkAsync(async () =>
        {
            currentUser.Set(fixture.UserA);

            await Assert.ThrowsAsync<AbpAuthorizationException>(
                () => service.GetMeAsync());
        });
    }

    [Fact]
    public async Task RACCOUNT05_Account_Without_Active_Contract_Returns_No_Lease_Data()
    {
        var fixture = await SeedAsync(includeActiveContract: false);
        var service = CreateService(out var currentUser);
        await WithUnitOfWorkAsync(async () =>
        {
            currentUser.Set(fixture.UserA);

            var result = await service.GetMeAsync();

            Assert.Equal(fixture.AccountA, result.Account.Id);
            Assert.Null(result.ActiveContract);
            Assert.Empty(result.Members);
        });
    }

    [Fact]
    public async Task RACCOUNT06_Spoofed_Client_Ids_Do_Not_Affect_Server_Scope()
    {
        var fixture = await SeedAsync();
        var service = CreateService(out var currentUser);
        await WithUnitOfWorkAsync(async () =>
        {
            currentUser.Set(fixture.UserA);

            // The service has no id input. These unrelated ids model query/body/header
            // values supplied by a client and prove they cannot become authority.
            var forgedAccountId = fixture.AccountB;
            var forgedRoomId = fixture.RoomB;
            var forgedTenantId = fixture.TenantB;
            var result = await service.GetMeAsync();

            Assert.NotEqual(forgedAccountId, result.Account.Id);
            Assert.NotEqual(forgedRoomId, result.ActiveContract?.Room.Id);
            Assert.DoesNotContain(result.Members, x => x.TenantId == forgedTenantId);
        });
    }

    private MobileTenantAppService CreateService(out CurrentUserProxy currentUser)
    {
        var user = DispatchProxy.Create<ICurrentUser, CurrentUserProxy>();
        currentUser = (CurrentUserProxy)(object)user;
        return new MobileTenantAppService(
            _accountUsers,
            _accounts,
            _contracts,
            _rooms,
            _floors,
            _buildings,
            _contractTenants,
            _tenants,
            user);
    }

    private async Task<Fixture> SeedAsync(bool includeActiveContract = true)
    {
        var fixture = Fixture.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(new Building(fixture.BuildingA,
                "Building A", "Address A"));
            await _buildings.InsertAsync(new Building(fixture.BuildingB,
                "Building B", "Address B"));
            await _floors.InsertAsync(new Floor(fixture.FloorA, fixture.BuildingA,
                1, "Floor A"));
            await _floors.InsertAsync(new Floor(fixture.FloorB, fixture.BuildingB,
                2, "Floor B"));
            await _rooms.InsertAsync(new Room(fixture.RoomA, fixture.FloorA,
                "A-101", "Room A", null, 2, RoomStatus.Occupied));
            await _rooms.InsertAsync(new Room(fixture.RoomB, fixture.FloorB,
                "B-202", "Room B", null, 2, RoomStatus.Occupied));

            await _tenants.InsertAsync(new Tenant(fixture.TenantA,
                "Tenant A", "0900000001", "a@example.test", null, null,
                IdentityType.CitizenId, "111111111111"));
            await _tenants.InsertAsync(new Tenant(fixture.TenantB,
                "Tenant B private PII", "0900000002", "b@example.test", null, null,
                IdentityType.CitizenId, "222222222222"));

            await _accounts.InsertAsync(new RentalAccount(fixture.AccountA,
                "ACC-A", "Account A"));
            await _accounts.InsertAsync(new RentalAccount(fixture.AccountB,
                "ACC-B", "Account B"));
            await _accountUsers.InsertAsync(new RentalAccountUser(
                fixture.AccountUserA, fixture.AccountA, fixture.UserA, fixture.TenantA,
                RentalAccountAccessRole.PrimaryTenant));
            await _accountUsers.InsertAsync(new RentalAccountUser(
                fixture.AccountUserARepresentative, fixture.AccountA,
                fixture.UserARepresentative, null,
                RentalAccountAccessRole.AuthorizedRepresentative));
            await _accountUsers.InsertAsync(new RentalAccountUser(
                fixture.AccountUserB, fixture.AccountB, fixture.UserB, fixture.TenantB,
                RentalAccountAccessRole.PrimaryTenant));

            if (!includeActiveContract)
            {
                return;
            }

            var contractA = new Contract(fixture.ContractA, "CON-A", fixture.RoomA,
                new DateTime(2026, 1, 1), null, 5_000_000m);
            contractA.Activate();
            contractA.AssignRentalAccount(fixture.AccountA);
            await _contracts.InsertAsync(contractA);
            await _contractTenants.InsertAsync(new ContractTenant(
                fixture.ContractTenantA, fixture.ContractA, fixture.TenantA,
                ContractTenantRole.PrimaryTenant));

            var contractB = new Contract(fixture.ContractB, "CON-B", fixture.RoomB,
                new DateTime(2026, 1, 1), null, 6_000_000m);
            contractB.Activate();
            contractB.AssignRentalAccount(fixture.AccountB);
            await _contracts.InsertAsync(contractB);
            await _contractTenants.InsertAsync(new ContractTenant(
                fixture.ContractTenantB, fixture.ContractB, fixture.TenantB,
                ContractTenantRole.PrimaryTenant));
        });

        return fixture;
    }

    private sealed class Fixture
    {
        public Guid UserA { get; } = Guid.NewGuid();
        public Guid UserARepresentative { get; } = Guid.NewGuid();
        public Guid UserB { get; } = Guid.NewGuid();
        public Guid AccountA { get; } = Guid.NewGuid();
        public Guid AccountB { get; } = Guid.NewGuid();
        public Guid AccountUserA { get; } = Guid.NewGuid();
        public Guid AccountUserARepresentative { get; } = Guid.NewGuid();
        public Guid AccountUserB { get; } = Guid.NewGuid();
        public Guid BuildingA { get; } = Guid.NewGuid();
        public Guid BuildingB { get; } = Guid.NewGuid();
        public Guid FloorA { get; } = Guid.NewGuid();
        public Guid FloorB { get; } = Guid.NewGuid();
        public Guid RoomA { get; } = Guid.NewGuid();
        public Guid RoomB { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public Guid ContractA { get; } = Guid.NewGuid();
        public Guid ContractB { get; } = Guid.NewGuid();
        public Guid ContractTenantA { get; } = Guid.NewGuid();
        public Guid ContractTenantB { get; } = Guid.NewGuid();

        public static Fixture Create() => new();
    }

    public class CurrentUserProxy : DispatchProxy
    {
        public Guid? User { get; private set; }

        public void Set(Guid id) => User = id;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name switch
            {
                "get_IsAuthenticated" => User.HasValue,
                "get_Id" => User,
                _ => throw new InvalidOperationException(
                    "Unexpected ICurrentUser method: " + method?.Name)
            };
    }
}
