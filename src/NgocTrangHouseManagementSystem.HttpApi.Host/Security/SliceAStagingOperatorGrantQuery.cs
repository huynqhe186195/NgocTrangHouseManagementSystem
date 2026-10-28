using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Isolated staging-only, explicit DI. Lists persisted membership history
/// for ONE building. Requires both a dedicated delegation ABP permission
/// and an active, scoped issuer account. No cross-building listing or
/// client-provided issuer identity.
/// </summary>
public sealed class SliceAStagingOperatorGrantQuery
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingOperatorChangeIssuerReader _issuers;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IBuildingOperatorGrantReader _grants;
    private readonly ICurrentUser _currentUser;
    private readonly SliceAStagingIdentityCommitLease _lease;

    public SliceAStagingOperatorGrantQuery(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingOperatorChangeIssuerReader issuers,
        IBuildingOperatorActiveAccountReader accounts,
        IBuildingOperatorGrantReader grants,
        ICurrentUser currentUser,
        SliceAStagingIdentityCommitLease lease)
    {
        _provider = provider;
        _issuers = issuers;
        _accounts = accounts;
        _grants = grants;
        _currentUser = currentUser;
        _lease = lease;
    }

    public async Task<IReadOnlyList<OperatorGrantListItem>> GetAsync(
        Guid buildingId, int skipCount, int maxResultCount)
    {
        if (buildingId == Guid.Empty || skipCount < 0 ||
            maxResultCount < 1 || maxResultCount > 100)
            throw Denied();

        // Only true ABP delegation authority, never Buildings.Read/Manage
        // alone. Both abilities can legitimately inspect current revocations.
        var assign = await _issuers.ReadAsync(BuildingOperatorChangeKind.Assign);
        var revoke = await _issuers.ReadAsync(BuildingOperatorChangeKind.Revoke);
        // The caller identity must come from the authenticated ABP principal,
        // never from an authority reader result. A non-privileged Assign
        // result cannot borrow a privileged Revoke result for another user.
        var actor = _currentUser.IsAuthenticated
            ? _currentUser.Id ?? Guid.Empty
            : Guid.Empty;
        if (!HasDelegationPrivilege(actor, assign, revoke) ||
            !await _accounts.IsActiveAsync(actor) ||
            !(await _grants.GetActiveBuildingIdsAsync(actor)).Contains(buildingId))
            throw Denied();

        // ABP permissions may be cached. Prove a persisted Identity grant
        // and hold its lock through the transactional Building read, so
        // revocation and history disclosure have a defined commit order.
        await _lease.AcquireGrantHistoryReadAsync(actor);

        var db = await _provider.GetDbContextAsync();
        // Grant membership checked above is fresh but not a mutation fence.
        // Repeat it in the same SQL query as the data read to prevent a
        // cross-building data leak if scope was revoked in between awaits.
        var now = DateTime.UtcNow;
        return await (
            from entry in db.Set<BuildingOperatorAccessGrant>().AsNoTracking()
            join building in db.Buildings.AsNoTracking()
                on entry.BuildingId equals building.Id
            where entry.BuildingId == buildingId && !building.IsDeleted
            where db.Set<BuildingOperatorAccessGrant>().Any(issuer =>
                issuer.BuildingId == buildingId &&
                issuer.OperatorUserId == actor &&
                issuer.RevokedAtUtc == null &&
                issuer.GrantedAtUtc <= now &&
                (issuer.ExpiresAtUtc == null || issuer.ExpiresAtUtc > now))
            orderby entry.GrantedAtUtc descending, entry.Id
            select new OperatorGrantListItem(
                entry.Id, entry.OperatorUserId, entry.BuildingId,
                entry.GrantedByUserId, entry.GrantedAtUtc,
                entry.ExpiresAtUtc, entry.RevokedAtUtc,
                entry.RevisionNumber)
        ).Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    private static bool HasDelegationPrivilege(
        Guid actor, BuildingOperatorChangeIssuer? assign,
        BuildingOperatorChangeIssuer? revoke)
        => actor != Guid.Empty &&
           ((assign is { PrivilegedPermissionGranted: true } &&
             assign.OperatorId == actor) ||
            (revoke is { PrivilegedPermissionGranted: true } &&
             revoke.OperatorId == actor));

    private static AbpAuthorizationException Denied()
        => new("No active building-scoped permission to view operator grants.");
}

public sealed record OperatorGrantListItem(
    Guid GrantId, Guid OperatorUserId, Guid BuildingId,
    Guid GrantedByUserId, DateTime GrantedAtUtc, DateTime? ExpiresAtUtc,
    DateTime? RevokedAtUtc, int RevisionNumber);
