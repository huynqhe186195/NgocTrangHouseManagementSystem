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

    public SliceAStagingWriteBoundary(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser user)
    {
        _provider = provider;
        _user = user;
    }

    public async Task<T> ExecuteBuildingAsync<T>(
        Guid serverResolvedBuildingId, Func<Task<T>> mutation,
        BuildingScopedMutationKind kind = BuildingScopedMutationKind.Building)
        => await new EfBuildingScopedWriteBoundaryCandidate(
            await _provider.GetDbContextAsync(), _user, TimeProvider.System)
            .ExecuteBuildingAsync(serverResolvedBuildingId, mutation, kind);
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
