using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Permissions;
using BuildingManagement.Rooms;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Users;

namespace BuildingManagement.Security;

/// <summary>
/// Resource-specific capability catalogue. The caller must not pass an
/// arbitrary permission string or a supposedly authorized building ID.
/// No public HTTP endpoint uses this design gate.
/// </summary>
public enum BuildingBusinessCapability
{
    ReadBuilding = 1,
    ManageBuilding = 2,
    ReadFloor = 3,
    ManageFloor = 4,
    ReadRoom = 5,
    ManageRoom = 6,
    ReadContract = 7,
    ManageContract = 8
}

public enum BuildingBusinessResourceKind
{
    Building = 1,
    Floor = 2,
    Room = 3,
    Contract = 4
}

public enum BuildingBusinessScopeAssessment
{
    Denied = 0,
    EligibleForDesignOnly = 1
}

public sealed record BuildingBusinessPrincipal(
    Guid OperatorId, bool PermissionGranted);

public interface IBuildingBusinessPrincipalReader
{
    Task<BuildingBusinessPrincipal?> ReadAsync(BuildingBusinessCapability capability);
}

[ExposeServices(typeof(IBuildingBusinessPrincipalReader))]
public sealed class AbpBuildingBusinessPrincipalReader :
    IBuildingBusinessPrincipalReader, ITransientDependency
{
    private readonly ICurrentUser _user;
    private readonly IPermissionChecker _permissions;

    public AbpBuildingBusinessPrincipalReader(
        ICurrentUser user, IPermissionChecker permissions)
    {
        _user = user;
        _permissions = permissions;
    }

    public async Task<BuildingBusinessPrincipal?> ReadAsync(
        BuildingBusinessCapability capability)
    {
        if (!_user.IsAuthenticated || !_user.Id.HasValue ||
            _user.Id.Value == Guid.Empty)
            return null;

        var permissionName = capability switch
        {
            BuildingBusinessCapability.ReadBuilding =>
                BuildingManagementPermissions.Buildings.Read,
            BuildingBusinessCapability.ManageBuilding =>
                BuildingManagementPermissions.Buildings.Manage,
            BuildingBusinessCapability.ReadFloor =>
                BuildingManagementPermissions.Floors.Read,
            BuildingBusinessCapability.ManageFloor =>
                BuildingManagementPermissions.Floors.Manage,
            BuildingBusinessCapability.ReadRoom =>
                BuildingManagementPermissions.Rooms.Read,
            BuildingBusinessCapability.ManageRoom =>
                BuildingManagementPermissions.Rooms.Manage,
            BuildingBusinessCapability.ReadContract =>
                BuildingManagementPermissions.Contracts.Read,
            BuildingBusinessCapability.ManageContract =>
                BuildingManagementPermissions.Contracts.Manage,
            _ => null
        };

        if (permissionName is null)
            return null;

        return new BuildingBusinessPrincipal(
            _user.Id.Value,
            await _permissions.IsGrantedAsync(permissionName));
    }
}

/// <summary>
/// IDs are reconstructed from real repositories in the active ABP data scope.
/// Neither a client BuildingId nor a room's claimed ownership is trusted.
/// ABP's normal soft-delete and tenant filters must remain enabled.
/// </summary>
public sealed record BuildingBusinessResolvedScope(
    Guid ResourceId, Guid BuildingId);

public interface IBuildingBusinessResourceScopeReader
{
    Task<BuildingBusinessResolvedScope?> ReadAsync(
        BuildingBusinessResourceKind kind, Guid resourceId);
}

[ExposeServices(typeof(IBuildingBusinessResourceScopeReader))]
public sealed class EfBuildingBusinessResourceScopeReader :
    IBuildingBusinessResourceScopeReader, ITransientDependency
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;

    public EfBuildingBusinessResourceScopeReader(
        IRepository<Building, Guid> buildings,
        IRepository<Floor, Guid> floors,
        IRepository<Room, Guid> rooms,
        IRepository<Contract, Guid> contracts)
    {
        _buildings = buildings;
        _floors = floors;
        _rooms = rooms;
        _contracts = contracts;
    }

    public async Task<BuildingBusinessResolvedScope?> ReadAsync(
        BuildingBusinessResourceKind kind, Guid resourceId)
    {
        if (resourceId == Guid.Empty)
            return null;

        Guid buildingId;
        switch (kind)
        {
            case BuildingBusinessResourceKind.Building:
                buildingId = resourceId;
                break;

            case BuildingBusinessResourceKind.Floor:
                var floor = await _floors.FindAsync(resourceId);
                if (floor is null)
                    return null;
                buildingId = floor.BuildingId;
                break;

            case BuildingBusinessResourceKind.Room:
                var room = await _rooms.FindAsync(resourceId);
                if (room is null)
                    return null;
                var roomFloor = await _floors.FindAsync(room.FloorId);
                if (roomFloor is null)
                    return null;
                buildingId = roomFloor.BuildingId;
                break;

            case BuildingBusinessResourceKind.Contract:
                var contract = await _contracts.FindAsync(resourceId);
                if (contract is null)
                    return null;
                var contractRoom = await _rooms.FindAsync(contract.RoomId);
                if (contractRoom is null)
                    return null;
                var contractFloor = await _floors.FindAsync(contractRoom.FloorId);
                if (contractFloor is null)
                    return null;
                buildingId = contractFloor.BuildingId;
                break;

            default:
                return null;
        }

        if (buildingId == Guid.Empty ||
            await _buildings.FindAsync(buildingId) is null)
            return null;

        return new BuildingBusinessResolvedScope(resourceId, buildingId);
    }
}

/// <summary>
/// No operator-building membership has been reviewed or deployed.
/// Replacing this source needs an explicit design/schema and revocation audit.
/// </summary>
public interface IBuildingOperatorGrantReader
{
    Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid operatorId);
}

[ExposeServices(typeof(IBuildingOperatorGrantReader))]
public sealed class DenyAllBuildingOperatorGrantReader :
    IBuildingOperatorGrantReader, ITransientDependency
{
    public Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(Guid operatorId)
        => Task.FromResult<IReadOnlyCollection<Guid>>(Array.Empty<Guid>());
}

/// <summary>
/// Read-only preflight: never an authorization token. It is deliberately
/// NOT wired into any existing AppService or public endpoint. Future CRUD
/// actions must re-read grants and resource links inside their transaction,
/// and collections must filter the DB query BEFORE paging and counting.
/// </summary>
public sealed class BuildingBusinessScopeDesignGate : ITransientDependency
{
    private readonly IBuildingBusinessPrincipalReader _principals;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly IBuildingOperatorGrantReader _grants;

    public BuildingBusinessScopeDesignGate(
        IBuildingBusinessPrincipalReader principals,
        IBuildingBusinessResourceScopeReader resources,
        IBuildingOperatorGrantReader grants)
    {
        _principals = principals;
        _resources = resources;
        _grants = grants;
    }

    public async Task<BuildingBusinessScopeAssessment> AssessDesignOnlyAsync(
        BuildingBusinessResourceKind kind,
        Guid resourceId,
        BuildingBusinessCapability capability)
    {
        if (resourceId == Guid.Empty || !Matches(kind, capability))
            return BuildingBusinessScopeAssessment.Denied;

        var principal = await _principals.ReadAsync(capability);
        if (principal is null || principal.OperatorId == Guid.Empty ||
            !principal.PermissionGranted)
            return BuildingBusinessScopeAssessment.Denied;

        var resolved = await _resources.ReadAsync(kind, resourceId);
        if (resolved is null || resolved.ResourceId != resourceId ||
            resolved.BuildingId == Guid.Empty)
            return BuildingBusinessScopeAssessment.Denied;

        var granted = await _grants.GetActiveBuildingIdsAsync(principal.OperatorId);
        if (granted is null)
            return BuildingBusinessScopeAssessment.Denied;

        foreach (var buildingId in granted)
            if (buildingId == resolved.BuildingId)
                return BuildingBusinessScopeAssessment.EligibleForDesignOnly;

        return BuildingBusinessScopeAssessment.Denied;
    }

    private static bool Matches(
        BuildingBusinessResourceKind kind,
        BuildingBusinessCapability capability) => (kind, capability) switch
    {
        (BuildingBusinessResourceKind.Building,
            BuildingBusinessCapability.ReadBuilding or
            BuildingBusinessCapability.ManageBuilding) => true,
        (BuildingBusinessResourceKind.Floor,
            BuildingBusinessCapability.ReadFloor or
            BuildingBusinessCapability.ManageFloor) => true,
        (BuildingBusinessResourceKind.Room,
            BuildingBusinessCapability.ReadRoom or
            BuildingBusinessCapability.ManageRoom) => true,
        (BuildingBusinessResourceKind.Contract,
            BuildingBusinessCapability.ReadContract or
            BuildingBusinessCapability.ManageContract) => true,
        _ => false
    };
}
