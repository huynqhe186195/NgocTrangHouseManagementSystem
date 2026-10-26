using System;
using System.Threading.Tasks;
using BuildingManagement.Permissions;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Users;

namespace BuildingManagement.Security;

/// <summary>
/// Deliberately independent grant/revoke operations. A normal
/// Buildings.Manage or Read permission cannot authorize delegation.
/// </summary>
public enum BuildingOperatorChangeKind
{
    Assign = 1,
    Revoke = 2
}

public enum BuildingOperatorChangePreflightStatus
{
    Denied = 0,
    EligibleForDesignOnly = 1
}

public sealed record BuildingOperatorChangeIssuer(
    Guid OperatorId, bool PrivilegedPermissionGranted);

public interface IBuildingOperatorChangeIssuerReader
{
    Task<BuildingOperatorChangeIssuer?> ReadAsync(BuildingOperatorChangeKind change);
}

/// <summary>
/// Server identity and fixed ABP permission names only. No caller-supplied
/// user ID or permission string is accepted for the issuer.
/// </summary>
[ExposeServices(typeof(IBuildingOperatorChangeIssuerReader))]
public sealed class AbpBuildingOperatorChangeIssuerReader :
    IBuildingOperatorChangeIssuerReader, ITransientDependency
{
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionChecker _permissions;

    public AbpBuildingOperatorChangeIssuerReader(
        ICurrentUser currentUser, IPermissionChecker permissions)
    {
        _currentUser = currentUser;
        _permissions = permissions;
    }

    public async Task<BuildingOperatorChangeIssuer?> ReadAsync(
        BuildingOperatorChangeKind change)
    {
        if (!_currentUser.IsAuthenticated ||
            !_currentUser.Id.HasValue ||
            _currentUser.Id.Value == Guid.Empty)
            return null;

        var name = change switch
        {
            BuildingOperatorChangeKind.Assign =>
                BuildingManagementPermissions.Buildings.AssignOperators,
            BuildingOperatorChangeKind.Revoke =>
                BuildingManagementPermissions.Buildings.RevokeOperators,
            _ => null
        };
        if (name is null)
            return null;

        return new BuildingOperatorChangeIssuer(
            _currentUser.Id.Value,
            await _permissions.IsGrantedAsync(name));
    }
}

/// <summary>
/// Must be resolved from real ABP Identity (no client-supplied account state).
/// The live default denies until a reviewed host identity adapter is wired.
/// </summary>
public interface IBuildingOperatorActiveAccountReader
{
    Task<bool> IsActiveAsync(Guid userId);
}

[ExposeServices(typeof(IBuildingOperatorActiveAccountReader))]
public sealed class DenyAllBuildingOperatorActiveAccountReader :
    IBuildingOperatorActiveAccountReader, ITransientDependency
{
    public Task<bool> IsActiveAsync(Guid userId) => Task.FromResult(false);
}

/// <summary>
/// Read-only assessment for future admin flows; returns no credential.
/// Intentionally not wired to any HTTP action, grant writer, or DB migration.
/// Prevents a user from assigning/removing their own scope. The first-owner
/// bootstrap needs a separately reviewed and audited workflow.
/// </summary>
public sealed class BuildingOperatorChangeDesignGate : ITransientDependency
{
    private readonly IBuildingOperatorChangeIssuerReader _issuers;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly IBuildingOperatorGrantReader _grants;

    public BuildingOperatorChangeDesignGate(
        IBuildingOperatorChangeIssuerReader issuers,
        IBuildingOperatorActiveAccountReader accounts,
        IBuildingBusinessResourceScopeReader resources,
        IBuildingOperatorGrantReader grants)
    {
        _issuers = issuers;
        _accounts = accounts;
        _resources = resources;
        _grants = grants;
    }

    public async Task<BuildingOperatorChangePreflightStatus> AssessDesignOnlyAsync(
        BuildingOperatorChangeKind change,
        Guid requestedBuildingId,
        Guid recipientUserId)
    {
        if (requestedBuildingId == Guid.Empty ||
            recipientUserId == Guid.Empty ||
            change is not (BuildingOperatorChangeKind.Assign or
                BuildingOperatorChangeKind.Revoke))
            return BuildingOperatorChangePreflightStatus.Denied;

        var issuer = await _issuers.ReadAsync(change);
        if (issuer is null || issuer.OperatorId == Guid.Empty ||
            !issuer.PrivilegedPermissionGranted ||
            issuer.OperatorId == recipientUserId)
            return BuildingOperatorChangePreflightStatus.Denied;

        // Issuer must be active for every operation. A disabled/locked
        // recipient must still be REVOKABLE so a stale grant can be cleared.
        // Assignments require an active recipient. A real revoke writer
        // must separately resolve the exact existing grant row.
        if (!await _accounts.IsActiveAsync(issuer.OperatorId))
            return BuildingOperatorChangePreflightStatus.Denied;
        if (change == BuildingOperatorChangeKind.Assign &&
            !await _accounts.IsActiveAsync(recipientUserId))
            return BuildingOperatorChangePreflightStatus.Denied;

        var resource = await _resources.ReadAsync(
            BuildingBusinessResourceKind.Building, requestedBuildingId);
        if (resource is null || resource.ResourceId != requestedBuildingId ||
            resource.BuildingId != requestedBuildingId)
            return BuildingOperatorChangePreflightStatus.Denied;

        // No broad admin shortcut or self-declared owner. An issuer must
        // already possess active building scope AND the separate permission.
        var activeBuildings = await _grants
            .GetActiveBuildingIdsAsync(issuer.OperatorId);
        if (activeBuildings is null)
            return BuildingOperatorChangePreflightStatus.Denied;

        foreach (var id in activeBuildings)
            if (id == requestedBuildingId)
                return BuildingOperatorChangePreflightStatus.EligibleForDesignOnly;

        return BuildingOperatorChangePreflightStatus.Denied;
    }
}
