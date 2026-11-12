using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Security;
using Volo.Abp.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// The ONLY opt-in real persistence adapters for isolated Slice A staging.
/// Explicit host registration is required; these have no ABP conventional
/// service markers and never replace the production DenyAll defaults.
/// They resolve the SAME ABP UnitOfWork DbContext as domain repositories.
/// </summary>
public sealed class SliceAStagingGrantReader : IBuildingOperatorGrantReader
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    public SliceAStagingGrantReader(
        IDbContextProvider<BuildingManagementDbContext> provider) => _provider = provider;

    public async Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid userId)
        => await new EfBuildingOperatorGrantCandidateReader(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .GetActiveBuildingIdsAsync(userId);
}

public sealed class SliceAStagingFirstOwnerWriter : IBuildingOwnerBootstrapWriter
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly ICurrentUser _actor;
    private readonly IOptions<BuildingOwnerBootstrapSecurityOptions> _roots;
    private readonly SliceAStagingIdentityCommitLease _lease;

    public SliceAStagingFirstOwnerWriter(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser actor,
        IOptions<BuildingOwnerBootstrapSecurityOptions> roots,
        SliceAStagingIdentityCommitLease lease)
    {
        _provider = provider;
        _actor = actor;
        _roots = roots;
        _lease = lease;
    }

    public async Task<Building> CreateWithOwnerAtomicallyAsync(
        Guid trustedOwnerUserId, string name, string address, string? description)
    {
        // The staging writer must not trust its own in-process caller:
        // only the one configured, authenticated root can bootstrap.
        // This check also protects direct writer use outside the coordinator.
        if (trustedOwnerUserId == Guid.Empty ||
            !_actor.IsAuthenticated || _actor.Id != trustedOwnerUserId ||
            _roots.Value.RootUserIds is not { Count: 1 } roots ||
            !roots.Contains(trustedOwnerUserId))
            throw new Volo.Abp.Authorization.AbpAuthorizationException(
                "First-owner writer requires the designated authenticated root.");

        // Hold Identity account and persisted ABP BootstrapOwner permission
        // locks until the Building UoW commits (or fails).
        await _lease.AcquireBootstrapAsync(trustedOwnerUserId);
        return await new EfBuildingOwnerBootstrapAtomicWriter(
            await _provider.GetDbContextAsync())
            .CreateWithOwnerAtomicallyAsync(trustedOwnerUserId, name, address, description);
    }
}

public sealed class SliceAStagingWriteBoundary : IBuildingScopedWriteBoundary
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly ICurrentUser _user;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly SliceAStagingIdentityCommitLease _lease;

    public SliceAStagingWriteBoundary(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser user,
        IBuildingCrudAccessPolicy policy,
        SliceAStagingIdentityCommitLease lease)
    {
        _provider = provider;
        _user = user;
        _policy = policy;
        _lease = lease;
    }

    public async Task<T> ExecuteBuildingAsync<T>(
        Guid serverResolvedBuildingId, Func<Task<T>> mutation,
        BuildingScopedMutationKind kind = BuildingScopedMutationKind.Building)
    {
        if (!_user.IsAuthenticated || !_user.Id.HasValue ||
            _user.Id.Value == Guid.Empty || serverResolvedBuildingId == Guid.Empty)
            throw new Volo.Abp.Authorization.AbpAuthorizationException(
                "Missing authenticated operator for staging write.");

        var permission = kind switch
        {
            BuildingScopedMutationKind.Building => BuildingBusinessCapability.ManageBuilding,
            BuildingScopedMutationKind.Floor => BuildingBusinessCapability.ManageFloor,
            BuildingScopedMutationKind.Room => BuildingBusinessCapability.ManageRoom,
            _ => throw new Volo.Abp.Authorization.AbpAuthorizationException(
                "Unknown building write capability.")
        };

        // Identity row locks survive until the ABP Building transaction
        // commits, serializing account disable/lockout and direct-user
        // permission revocation against writes. No role-only bypass.
        await _lease.AcquireAsync(_user.Id.Value, permission);

        // Re-evaluate CURRENT ABP Identity account + operation permission +
        // scoped membership after mutation, while the Building PostgreSQL
        // transaction still holds the membership lock. This cannot provide
        // atomic revocation with a separate Identity database by itself.
        return await new EfBuildingScopedWriteFenceCandidate(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .ExecuteAsync(_user.Id.Value, serverResolvedBuildingId, mutation,
                () => _policy.EnsureBuildingAsync(serverResolvedBuildingId, permission));
    }
}

/// <summary>
/// Enabled ONLY by the reviewed isolated staging host composition.
/// The durable evidence must be recorded by two independent principals.
/// </summary>
public sealed class SliceAStagingOwnerRecoveryWriter : IBuildingOwnerRecoveryWriter
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly ICurrentUser _actor;
    private readonly IOptions<BuildingOwnerBootstrapSecurityOptions> _roots;
    private readonly IOptions<BuildingOwnerRecoverySecurityOptions> _signers;
    private readonly SliceAStagingIdentityCommitLease _lease;

    public SliceAStagingOwnerRecoveryWriter(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser actor,
        IOptions<BuildingOwnerBootstrapSecurityOptions> roots,
        IOptions<BuildingOwnerRecoverySecurityOptions> signers,
        SliceAStagingIdentityCommitLease lease)
    {
        _provider = provider;
        _actor = actor;
        _roots = roots;
        _signers = signers;
        _lease = lease;
    }

    public async Task<Guid> RecoverAtomicallyAsync(
        Guid buildingId, Guid originalOwnerId, Guid newOwnerId,
        Guid firstApprovedRootId, Guid secondApprovedRootId,
        Guid approvalEvidenceId)
    {
        // A direct in-process call cannot impersonate either signer.
        // Recovery authority must not grant the co-signer BootstrapOwner.
        if (!_actor.IsAuthenticated || _actor.Id != firstApprovedRootId ||
            firstApprovedRootId == Guid.Empty ||
            _roots.Value.RootUserIds is not { Count: 1 } roots ||
            !roots.Contains(firstApprovedRootId) ||
            _signers.Value.CoSignerUserIds is not { Count: 1 } signers ||
            !signers.Contains(secondApprovedRootId) ||
            firstApprovedRootId == secondApprovedRootId)
            throw new Volo.Abp.Authorization.AbpAuthorizationException(
                "Recovery writer requires two distinct designated authorities.");

        await _lease.AcquireOwnerRecoveryCommitAsync(firstApprovedRootId,
            secondApprovedRootId, originalOwnerId, newOwnerId);
        return await new EfBuildingOwnerRecoveryAtomicWriter(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .RecoverAtomicallyAsync(buildingId, originalOwnerId,
                newOwnerId, firstApprovedRootId, secondApprovedRootId,
                approvalEvidenceId);
    }
}

public sealed class SliceAStagingGrantMutationWriter : IBuildingOperatorGrantMutationWriter
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly ICurrentUser _actor;
    private readonly SliceAStagingIdentityCommitLease _lease;

    public SliceAStagingGrantMutationWriter(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser actor, SliceAStagingIdentityCommitLease lease)
    {
        _provider = provider;
        _actor = actor;
        _lease = lease;
    }

    public async Task<Guid> AssignAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        DateTime? expiresAtUtc)
    {
        EnsureIssuer(trustedIssuerId);
        await _lease.AcquireDelegationAsync(trustedIssuerId,
            recipientUserId, BuildingOperatorChangeKind.Assign);
        return await new EfBuildingOperatorGrantAtomicMutationWriter(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .AssignAtomicallyAsync(
                trustedIssuerId, recipientUserId, buildingId, expiresAtUtc);
    }

    public async Task RevokeAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        int expectedRevision)
    {
        EnsureIssuer(trustedIssuerId);
        await _lease.AcquireDelegationAsync(trustedIssuerId,
            recipientUserId, BuildingOperatorChangeKind.Revoke);
        await new EfBuildingOperatorGrantAtomicMutationWriter(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .RevokeAtomicallyAsync(
                trustedIssuerId, recipientUserId, buildingId, expectedRevision);
    }

    private void EnsureIssuer(Guid issuer)
    {
        if (!_actor.IsAuthenticated || !_actor.Id.HasValue ||
            _actor.Id.Value == Guid.Empty || _actor.Id.Value != issuer)
            throw new Volo.Abp.Authorization.AbpAuthorizationException(
                "The authenticated issuer must match the persisted grant issuer.");
    }
}
