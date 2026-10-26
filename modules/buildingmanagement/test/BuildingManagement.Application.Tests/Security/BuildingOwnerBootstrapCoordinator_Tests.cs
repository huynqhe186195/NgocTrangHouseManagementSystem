using System;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using Volo.Abp.Authorization;
using Xunit;

namespace BuildingManagement.Security;

/// <summary>
/// Actual BuildingAppService.CreateAsync coordinator tests. No production
/// membership table, database writer, or global authorization bypass.
/// </summary>
public sealed class BuildingOwnerBootstrapCoordinator_Tests
{
    [Fact]
    public async Task BOOT01_Default_Writer_Denies_Without_Creating_Building()
    {
        var fixture = Valid();
        var coordinator = new BuildingOwnerBootstrapCoordinator(
            fixture.Principal, fixture.Account, new DenyAllBuildingOwnerBootstrapWriter());

        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            coordinator.CreateWithCurrentOwnerAsync(Input()));
    }

    [Fact]
    public async Task BOOT02_Anonymous_Bootstrap_Cannot_Call_Account_Or_Writer()
    {
        var fixture = Valid();
        fixture.Principal.Id = null;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Coordinator.CreateWithCurrentOwnerAsync(Input()));
        Assert.Equal(0, fixture.Account.Calls);
        Assert.Equal(0, fixture.Writer.Calls);
    }

    [Fact]
    public async Task BOOT03_Denied_Permission_Does_Not_Call_Writer()
    {
        var fixture = Valid();
        fixture.Principal.Id = null;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Coordinator.CreateWithCurrentOwnerAsync(Input()));
        Assert.Equal(0, fixture.Writer.Calls);
    }

    [Fact]
    public async Task BOOT04_Inactive_Account_Does_Not_Call_Writer()
    {
        var fixture = Valid();
        fixture.Account.Active = false;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Coordinator.CreateWithCurrentOwnerAsync(Input()));
        Assert.Equal(0, fixture.Writer.Calls);
    }

    [Fact]
    public async Task BOOT05_Authorized_Issuer_Is_Always_The_Owner()
    {
        var fixture = Valid();
        var result = await fixture.Coordinator.CreateWithCurrentOwnerAsync(Input());

        Assert.NotNull(result);
        Assert.Equal(fixture.UserId, fixture.Writer.Owner);
        Assert.Equal(1, fixture.Writer.Calls);
        Assert.Equal("First building", result.Name);
    }

    [Fact]
    public async Task BOOT06_No_Second_Independent_Insert_If_Writer_Fails()
    {
        var fixture = Valid();
        fixture.Writer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.CreateWithCurrentOwnerAsync(Input()));
        Assert.Equal(1, fixture.Writer.Calls);
    }

    [Fact]
    public async Task BOOT07_Creating_Another_Building_Always_Rechecks_Principal()
    {
        var fixture = Valid();
        await fixture.Coordinator.CreateWithCurrentOwnerAsync(Input());
        fixture.Principal.Id = null;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            fixture.Coordinator.CreateWithCurrentOwnerAsync(Input()));
        Assert.Equal(2, fixture.Principal.Calls);
        Assert.Equal(1, fixture.Writer.Calls);
    }

    private static CreateBuildingDto Input()
        => new() { Name = "First building", Address = "Test only" };

    private static Fixture Valid()
    {
        var userId = Guid.NewGuid();
        var principal = new Principal { Id = userId };
        var account = new Account { Active = true };
        var writer = new Writer();
        var coordinator = new BuildingOwnerBootstrapCoordinator(
            principal, account, writer);
        return new(userId, principal, account, writer, coordinator);
    }

    private sealed record Fixture(
        Guid UserId, Principal Principal, Account Account, Writer Writer,
        BuildingOwnerBootstrapCoordinator Coordinator);

    private sealed class Principal : IBuildingOwnerBootstrapPrincipalReader
    {
        public Guid? Id { get; set; }
        public int Calls { get; private set; }
        public Task<Guid?> GetAuthorizedIssuerAsync()
        {
            Calls++;
            return Task.FromResult(Id);
        }
    }

    private sealed class Account : IBuildingOperatorActiveAccountReader
    {
        public bool Active { get; set; }
        public int Calls { get; private set; }
        public Task<bool> IsActiveAsync(Guid id)
        {
            Calls++;
            return Task.FromResult(Active);
        }
    }

    private sealed class Writer : IBuildingOwnerBootstrapWriter
    {
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public bool Fail { get; set; }

        public Task<Building> CreateWithOwnerAtomicallyAsync(
            Guid trustedOwnerUserId, string name,
            string address, string? description)
        {
            Calls++;
            Owner = trustedOwnerUserId;
            if (Fail)
                throw new InvalidOperationException("Simulated atomic rollback.");
            return Task.FromResult(new Building(
                Guid.NewGuid(), name, address, description));
        }
    }
}
