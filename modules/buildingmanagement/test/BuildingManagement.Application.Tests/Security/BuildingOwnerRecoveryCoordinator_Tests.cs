using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Volo.Abp.Authorization;
using Xunit;

namespace BuildingManagement.Security;

/// <summary>
/// Only simulated server-side approvals. Production supplies DENY-ALL
/// approval, founder and writer adapters; this fixture never exposes
/// a recovery HTTP route or trusts client-supplied approver IDs.
/// </summary>
public sealed class BuildingOwnerRecoveryCoordinator_Tests
{
    [Fact]
    public async Task RECOVERY01_Default_Approval_And_Writer_Are_DenyAll()
    {
        var f = Valid();
        var noApproval = new BuildingOwnerRecoveryCoordinator(
            f.Root, f.Accounts, new DenyAllBuildingOwnerRecoveryApprovalReader(),
            f.Founder, f.Writer, f.Options, f.RecoveryOptions);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            noApproval.RecoverAsync(f.Building, f.Recipient));
        Assert.Equal(0, f.Writer.Calls);

        var noWriter = new BuildingOwnerRecoveryCoordinator(
            f.Root, f.Accounts, f.Approvals, f.Founder,
            new DenyAllBuildingOwnerRecoveryWriter(), f.Options, f.RecoveryOptions);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            noWriter.RecoverAsync(f.Building, f.Recipient));
    }

    [Fact]
    public async Task RECOVERY02_Independent_Approved_Roots_Can_Add_New_Owner()
    {
        var f = Valid();
        var result = await f.Coordinator.RecoverAsync(f.Building, f.Recipient);
        Assert.NotEqual(Guid.Empty, result);
        Assert.Equal(1, f.Writer.Calls);
        Assert.Equal(f.Owner, f.Writer.FoundingOwner);
        Assert.Equal(f.Recipient, f.Writer.NewOwner);
        Assert.Equal(f.Approvals.Proof!.EvidenceId, f.Writer.Evidence);
    }

    [Fact]
    public async Task RECOVERY03_Active_Founder_Or_Invalid_Founding_Record_Denies()
    {
        var f = Valid();
        f.Accounts.Disabled.Remove(f.Owner);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        f.Accounts.Disabled.Add(f.Owner);
        f.Founder.Id = null;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        Assert.Equal(0, f.Writer.Calls);
    }

    [Fact]
    public async Task RECOVERY04_Duplicate_Approvers_Missing_Evidence_Or_Unknown_Root_Deny()
    {
        var f = Valid();
        var one = f.Approvals.Proof!;
        f.Approvals.Proof = one with { SecondRootId = one.FirstRootId };
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        f.Approvals.Proof = one with { EvidenceId = Guid.Empty };
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        f.Approvals.Proof = one with { SecondRootId = Guid.NewGuid() };
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        Assert.Equal(0, f.Writer.Calls);
    }

    [Fact]
    public async Task RECOVERY05_Anonymous_Unapproved_Or_Single_Root_Cannot_Activate()
    {
        var f = Valid();
        f.Root.Id = null;
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        f.Root.Id = Guid.NewGuid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        f.Root.Id = f.First;
        f.RecoveryOptions.Value.CoSignerUserIds.Remove(f.Second);
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Recipient));
        Assert.Equal(0, f.Writer.Calls);
    }

    [Fact]
    public async Task RECOVERY06_Disabled_Signer_Or_Recipient_Cannot_Be_Recovered()
    {
        var f = Valid();
        foreach (var account in new[] { f.First, f.Second, f.Recipient })
        {
            f.Accounts.Disabled.Add(account);
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                f.Coordinator.RecoverAsync(f.Building, f.Recipient));
            f.Accounts.Disabled.Remove(account);
        }
        Assert.Equal(0, f.Writer.Calls);
    }

    [Fact]
    public async Task RECOVERY07_No_Self_Award_Or_Trust_In_Client_Supplied_Approver()
    {
        var f = Valid();
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.Owner));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, f.First));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(f.Building, Guid.Empty));
        await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
            f.Coordinator.RecoverAsync(Guid.Empty, f.Recipient));
        Assert.Equal(0, f.Writer.Calls);
    }

    private static Fixture Valid()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var building = Guid.NewGuid();
        var root = new Root { Id = first };
        var accounts = new Accounts();
        accounts.Disabled.Add(owner);
        var approvals = new Approvals
        {
            Proof = new(first, second, Guid.NewGuid())
        };
        var founder = new Founder { Id = owner };
        var writer = new Writer();
        var options = Options.Create(new BuildingOwnerBootstrapSecurityOptions
        {
            RootUserIds = new HashSet<Guid> { first }
        });
        var recoveryOptions = Options.Create(new BuildingOwnerRecoverySecurityOptions
        {
            CoSignerUserIds = new HashSet<Guid> { second }
        });
        return new(first, second, owner, recipient, building, root,
            accounts, approvals, founder, writer, options, recoveryOptions,
            new(root, accounts, approvals, founder, writer, options, recoveryOptions));
    }

    private sealed record Fixture(Guid First, Guid Second, Guid Owner,
        Guid Recipient, Guid Building, Root Root, Accounts Accounts,
        Approvals Approvals, Founder Founder, Writer Writer,
        IOptions<BuildingOwnerBootstrapSecurityOptions> Options,
        IOptions<BuildingOwnerRecoverySecurityOptions> RecoveryOptions,
        BuildingOwnerRecoveryCoordinator Coordinator);

    private sealed class Root : IBuildingOwnerBootstrapPrincipalReader
    {
        public Guid? Id { get; set; }
        public Task<Guid?> GetAuthorizedIssuerAsync() => Task.FromResult(Id);
    }

    private sealed class Accounts : IBuildingOperatorActiveAccountReader
    {
        public HashSet<Guid> Disabled { get; } = new();
        public Task<bool> IsActiveAsync(Guid id)
            => Task.FromResult(!Disabled.Contains(id));
    }

    private sealed class Approvals : IBuildingOwnerRecoveryApprovalReader
    {
        public BuildingOwnerRecoveryApproval? Proof { get; set; }
        public Task<BuildingOwnerRecoveryApproval?> ReadApprovedAsync(
            Guid buildingId, Guid newOwnerId) => Task.FromResult(Proof);
    }

    private sealed class Founder : IBuildingOwnerRecoveryFounderReader
    {
        public Guid? Id { get; set; }
        public Task<Guid?> ReadFoundingOwnerAsync(Guid buildingId)
            => Task.FromResult(Id);
    }

    private sealed class Writer : IBuildingOwnerRecoveryWriter
    {
        public int Calls { get; private set; }
        public Guid FoundingOwner { get; private set; }
        public Guid NewOwner { get; private set; }
        public Guid Evidence { get; private set; }
        public Task<Guid> RecoverAtomicallyAsync(
            Guid buildingId, Guid originalOwnerId, Guid newOwnerId,
            Guid firstApprovedRootId, Guid secondApprovedRootId,
            Guid approvalEvidenceId)
        {
            Calls++;
            FoundingOwner = originalOwnerId;
            NewOwner = newOwnerId;
            Evidence = approvalEvidenceId;
            return Task.FromResult(Guid.NewGuid());
        }
    }
}
