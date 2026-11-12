using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// REVIEW-ONLY query building blocks for future authorized read operations.
/// The caller MUST obtain active IDs from a trusted server-side grant source,
/// validate the authenticated ABP user and the specific read permission.
/// The current CRUD application services DO NOT use this type.
/// </summary>
public static class BuildingBusinessScopedCollectionQueries
{
    public static IQueryable<Building> Buildings(
        IQueryable<Building> buildings,
        IReadOnlyCollection<Guid>? trustedBuildingIds)
    {
        ArgumentNullException.ThrowIfNull(buildings);
        var ids = SafeIds(trustedBuildingIds);
        return buildings.Where(b => !b.IsDeleted && ids.Contains(b.Id));
    }

    public static IQueryable<Floor> Floors(
        IQueryable<Floor> floors,
        IQueryable<Building> buildings,
        IReadOnlyCollection<Guid>? trustedBuildingIds)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(buildings);
        var ids = SafeIds(trustedBuildingIds);
        return from floor in floors
               join building in buildings on floor.BuildingId equals building.Id
               where !floor.IsDeleted && !building.IsDeleted &&
                     ids.Contains(building.Id)
               select floor;
    }

    public static IQueryable<Room> Rooms(
        IQueryable<Room> rooms,
        IQueryable<Floor> floors,
        IQueryable<Building> buildings,
        IReadOnlyCollection<Guid>? trustedBuildingIds)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(buildings);
        var ids = SafeIds(trustedBuildingIds);
        return from room in rooms
               join floor in floors on room.FloorId equals floor.Id
               join building in buildings on floor.BuildingId equals building.Id
               where !room.IsDeleted && !floor.IsDeleted &&
                     !building.IsDeleted && ids.Contains(building.Id)
               select room;
    }

    public static IQueryable<Contract> Contracts(
        IQueryable<Contract> contracts,
        IQueryable<Room> rooms,
        IQueryable<Floor> floors,
        IQueryable<Building> buildings,
        IReadOnlyCollection<Guid>? trustedBuildingIds)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(buildings);
        var ids = SafeIds(trustedBuildingIds);
        return from contract in contracts
               join room in rooms on contract.RoomId equals room.Id
               join floor in floors on room.FloorId equals floor.Id
               join building in buildings on floor.BuildingId equals building.Id
               where !contract.IsDeleted && !room.IsDeleted &&
                     !floor.IsDeleted && !building.IsDeleted &&
                     ids.Contains(building.Id)
               select contract;
    }

    private static Guid[] SafeIds(IReadOnlyCollection<Guid>? ids)
        => ids is null ? Array.Empty<Guid>() :
            ids.Where(id => id != Guid.Empty).Distinct().ToArray();
}
