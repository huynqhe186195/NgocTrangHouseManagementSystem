using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace BuildingManagement.Security;

/// <summary>
/// Integration candidate: combines ABP-validated user+permission, live-account
/// check, current server-owned building grants and scoped SQL list/paging.
/// NOT a CRUD service, public API, or stable authorization token.
/// Every currently configured account/grant provider is deny-all.
/// </summary>
public enum BuildingScopedReadDesignStatus
{
    Denied = 0,
    EligibleForDesignOnly = 1
}

public sealed record BuildingScopedReadDesignResult(
    BuildingScopedReadDesignStatus Status,
    BuildingScopedListPage Page);

[RemoteService(IsEnabled = false)]
public sealed class BuildingScopedReadDesignGate : ITransientDependency
{
    private static readonly BuildingScopedListPage EmptyPage =
        new(0, Array.Empty<Guid>());

    private readonly IBuildingBusinessPrincipalReader _principal;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IBuildingOperatorGrantReader _grants;
    private readonly IBuildingScopedCollectionStore _store;

    public BuildingScopedReadDesignGate(
        IBuildingBusinessPrincipalReader principal,
        IBuildingOperatorActiveAccountReader accounts,
        IBuildingOperatorGrantReader grants,
        IBuildingScopedCollectionStore store)
    {
        _principal = principal;
        _accounts = accounts;
        _grants = grants;
        _store = store;
    }

    public async Task<BuildingScopedReadDesignResult> AssessPageDesignOnlyAsync(
        BuildingScopedCollectionKind kind,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken = default)
    {
        // Rejected calls must never reach DB read/count functions.
        if (skipCount < 0 || maxResultCount < 1 || maxResultCount > 100)
            return Denied();
        var capability = kind switch
        {
            BuildingScopedCollectionKind.Building =>
                BuildingBusinessCapability.ReadBuilding,
            BuildingScopedCollectionKind.Floor =>
                BuildingBusinessCapability.ReadFloor,
            BuildingScopedCollectionKind.Room =>
                BuildingBusinessCapability.ReadRoom,
            BuildingScopedCollectionKind.Contract =>
                BuildingBusinessCapability.ReadContract,
            _ => (BuildingBusinessCapability)0
        };
        if (capability == 0)
            return Denied();

        var caller = await _principal.ReadAsync(capability);
        if (caller is null || caller.OperatorId == Guid.Empty ||
            !caller.PermissionGranted ||
            !await _accounts.IsActiveAsync(caller.OperatorId))
            return Denied();

        var current = await _grants.GetActiveBuildingIdsAsync(caller.OperatorId);
        if (current is null || current.Count == 0)
            return Denied();

        var usable = new HashSet<Guid>();
        foreach (var buildingId in current)
            if (buildingId != Guid.Empty)
                usable.Add(buildingId);
        if (usable.Count == 0)
            return Denied();

        // The store uses SQL-side scope filtering BEFORE count and paging.
        // In future actual CRUD, the permission+grant read and data snapshot
        // must be made consistent with revocation and other concurrent writes.
        var page = await _store.ReadPageAsync(
            kind, usable, skipCount, maxResultCount, cancellationToken);

        return new BuildingScopedReadDesignResult(
            BuildingScopedReadDesignStatus.EligibleForDesignOnly, page);
    }

    private static BuildingScopedReadDesignResult Denied()
        => new(BuildingScopedReadDesignStatus.Denied, EmptyPage);
}
