using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Security;
using Volo.Abp.EntityFrameworkCore;
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
    public SliceAStagingFirstOwnerWriter(
        IDbContextProvider<BuildingManagementDbContext> provider) => _provider = provider;

    public async Task<Building> CreateWithOwnerAtomicallyAsync(
        Guid trustedOwnerUserId, string name, string address, string? description)
        => await new EfBuildingOwnerBootstrapAtomicWriter(
            await _provider.GetDbContextAsync())
            .CreateWithOwnerAtomicallyAsync(trustedOwnerUserId, name, address, description);
}

public sealed class SliceAStagingWriteBoundary : IBuildingScopedWriteBoundary
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly ICurrentUser _user;
    private readonly IBuildingCrudAccessPolicy _policy;

    public SliceAStagingWriteBoundary(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser user,
        IBuildingCrudAccessPolicy policy)
    {
        _provider = provider;
        _user = user;
        _policy = policy;
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
    public SliceAStagingOwnerRecoveryWriter(
        IDbContextProvider<BuildingManagementDbContext> provider)
        => _provider = provider;

    public async Task<Guid> RecoverAtomicallyAsync(
        Guid buildingId, Guid originalOwnerId, Guid newOwnerId,
        Guid firstApprovedRootId, Guid secondApprovedRootId,
        Guid approvalEvidenceId)
        => await new EfBuildingOwnerRecoveryAtomicWriter(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .RecoverAtomicallyAsync(buildingId, originalOwnerId,
                newOwnerId, firstApprovedRootId, secondApprovedRootId,
                approvalEvidenceId);
}

public sealed class SliceAStagingGrantMutationWriter : IBuildingOperatorGrantMutationWriter
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    public SliceAStagingGrantMutationWriter(
        IDbContextProvider<BuildingManagementDbContext> provider) => _provider = provider;

    public async Task<Guid> AssignAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        DateTime? expiresAtUtc)
        => await new EfBuildingOperatorGrantAtomicMutationWriter(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .AssignAtomicallyAsync(
                trustedIssuerId, recipientUserId, buildingId, expiresAtUtc);

    public async Task RevokeAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        int expectedRevision)
        => await new EfBuildingOperatorGrantAtomicMutationWriter(
            await _provider.GetDbContextAsync(), TimeProvider.System)
            .RevokeAtomicallyAsync(
                trustedIssuerId, recipientUserId, buildingId, expectedRevision);
}
