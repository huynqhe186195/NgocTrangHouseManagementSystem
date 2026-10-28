using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace BuildingManagement.Security;

/// <summary>
/// Enforced authorization for Building/Floor/Room application-service entry
/// points. A permission alone NEVER authorizes another building. The
/// account + grant readers intentionally DENY ALL until a reviewed Identity
/// integration and membership migration are activated.
/// This is NOT the existing EligibleForDesignOnly preview gate.
/// </summary>
public interface IBuildingCrudAccessPolicy
{
    Task<IReadOnlyCollection<Guid>> GetAuthorizedBuildingIdsAsync(
        BuildingBusinessCapability capability);

    Task EnsureBuildingAsync(
        Guid buildingId, BuildingBusinessCapability capability);

    Task EnsureResourceAsync(
        BuildingBusinessResourceKind kind, Guid resourceId,
        BuildingBusinessCapability capability);

    Task EnsureBuildingCreationAsync();
}

[RemoteService(IsEnabled = false)]
[ExposeServices(typeof(IBuildingCrudAccessPolicy))]
public sealed class BuildingCrudAccessPolicy
    : IBuildingCrudAccessPolicy, ITransientDependency
{
    private readonly IBuildingBusinessPrincipalReader _principal;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IBuildingOperatorGrantReader _grants;
    private readonly IBuildingBusinessResourceScopeReader _resources;

    public BuildingCrudAccessPolicy(
        IBuildingBusinessPrincipalReader principal,
        IBuildingOperatorActiveAccountReader accounts,
        IBuildingOperatorGrantReader grants,
        IBuildingBusinessResourceScopeReader resources)
    {
        _principal = principal;
        _accounts = accounts;
        _grants = grants;
        _resources = resources;
    }

    public async Task<IReadOnlyCollection<Guid>> GetAuthorizedBuildingIdsAsync(
        BuildingBusinessCapability capability)
    {
        if (!IsSupported(capability))
            throw Denied();

        var actor = await _principal.ReadAsync(capability);
        if (actor is null || actor.OperatorId == Guid.Empty ||
            !actor.PermissionGranted)
            throw Denied();

        if (!await _accounts.IsActiveAsync(actor.OperatorId))
            throw Denied();

        var granted = await _grants.GetActiveBuildingIdsAsync(actor.OperatorId);
        if (granted is null || granted.Count == 0)
            throw Denied();

        var ids = granted.Where(id => id != Guid.Empty)
            .Distinct().ToArray();
        if (ids.Length == 0)
            throw Denied();
        return ids;
    }

    public Task EnsureBuildingAsync(
        Guid buildingId, BuildingBusinessCapability capability)
        => EnsureResourceAsync(
            BuildingBusinessResourceKind.Building, buildingId, capability);

    public async Task EnsureResourceAsync(
        BuildingBusinessResourceKind kind, Guid resourceId,
        BuildingBusinessCapability capability)
    {
        if (resourceId == Guid.Empty || !IsSupported(capability) ||
            !Matches(kind, capability))
            throw Denied();

        // Validate identity/permission/grants BEFORE resource lookup so
        // unauthorized users cannot distinguish arbitrary existing GUIDs.
        var granted = await GetAuthorizedBuildingIdsAsync(capability);
        var resolved = await _resources.ReadAsync(kind, resourceId);
        if (resolved is null || resolved.ResourceId != resourceId ||
            resolved.BuildingId == Guid.Empty ||
            !granted.Contains(resolved.BuildingId))
            throw Denied();
    }

    public Task EnsureBuildingCreationAsync()
    {
        // No approved first-owner bootstrap workflow exists. Creating a
        // building without an initial owner would make an orphan; trusting a
        // claimed BuildingId or super-admin role would enable escalation.
        // Deliberately BLOCK until an atomic bootstrap/grant protocol passes
        // independent security review, PostgreSQL tests and deployment gate.
        throw Denied();
    }

    // Enforce a fixed operation/resource matrix; no arbitrary permission
    // can be used to access an unrelated resource type. Two special cases:
    // creating a Floor needs its Building parent and ManageFloor; creating
    // a Room needs its Floor parent and ManageRoom.
    private static bool Matches(
        BuildingBusinessResourceKind kind,
        BuildingBusinessCapability permission) => (kind, permission) switch
    {
        (BuildingBusinessResourceKind.Building,
            BuildingBusinessCapability.ReadBuilding or
            BuildingBusinessCapability.ManageBuilding or
            BuildingBusinessCapability.ManageFloor or
            BuildingBusinessCapability.ManageRoom) => true,
        (BuildingBusinessResourceKind.Floor,
            BuildingBusinessCapability.ReadFloor or
            BuildingBusinessCapability.ManageFloor or
            BuildingBusinessCapability.ManageRoom) => true,
        (BuildingBusinessResourceKind.Room,
            BuildingBusinessCapability.ReadRoom or
            BuildingBusinessCapability.ManageRoom) => true,
        _ => false
    };

    private static bool IsSupported(BuildingBusinessCapability capability)
        => capability is
            BuildingBusinessCapability.ReadBuilding or
            BuildingBusinessCapability.ManageBuilding or
            BuildingBusinessCapability.ReadFloor or
            BuildingBusinessCapability.ManageFloor or
            BuildingBusinessCapability.ReadRoom or
            BuildingBusinessCapability.ManageRoom;

    private static AbpAuthorizationException Denied()
        => new("BuildingManagement: operation permission, active account, " +
               "building membership or resource scope is not authorized.");
}
