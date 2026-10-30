using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Staging-only physical meter CRUD and replacement. No conventional global
/// UtilityMeterAppService HTTP actions are unlocked. Server-owned room->floor
/// joins determine the building, the currently committed Identity permission
/// is leased for the transaction, and persisted building grants are fenced
/// through commit. Lock order: building, room, existing meter; original
/// service owns validation, historical immutability and replacement atomicity.
/// </summary>
public sealed class SliceCStagingUtilityMeters
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly ICurrentUser _user;
    private readonly IUtilityMeterAppService _meters;

    public SliceCStagingUtilityMeters(
        IDbContextProvider<BuildingManagementDbContext> provider,
        SliceAStagingIdentityCommitLease identity,
        ICurrentUser user, IUtilityMeterAppService meters)
    {
        _provider = provider;
        _identity = identity;
        _user = user;
        _meters = meters;
    }

    public async Task<IReadOnlyList<SliceCUtilityMeterRead>> GetListAsync(
        int skipCount, int maxResultCount)
    {
        if (skipCount < 0 || maxResultCount is < 1 or > 100)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await AuthorizedMeters(db, actor, DateTime.UtcNow)
            .Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public async Task<SliceCUtilityMeterRead> GetAsync(Guid id)
    {
        if (id == Guid.Empty) throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await AuthorizedMeters(db, actor, DateTime.UtcNow, id)
            .SingleOrDefaultAsync() ?? throw Denied();
    }

    public async Task<SliceCUtilityMeterRead> CreateAsync(
        CreateUtilityMeterDto input)
    {
        if (input is null || input.RoomId == Guid.Empty)
            throw Denied();
        var db = await _provider.GetDbContextAsync();
        var building = await ResolveRoomBuildingAsync(db, input.RoomId);
        return await WriteAsync(building, input.RoomId, null,
            async () => Project(await _meters.CreateAsync(input), building));
    }

    public async Task<SliceCUtilityMeterRead> UpdateAsync(
        Guid id, UpdateUtilityMeterDto input)
    {
        if (id == Guid.Empty || input is null) throw Denied();
        var source = await ResolveMeterAsync(id);
        return await WriteAsync(source.Building, source.Room, id,
            async () => Project(await _meters.UpdateAsync(id, input),
                source.Building));
    }

    public async Task<SliceCMeterReplacementRead> ReplaceAsync(
        Guid id, ReplaceUtilityMeterDto input)
    {
        if (id == Guid.Empty || input is null) throw Denied();
        var source = await ResolveMeterAsync(id);
        return await WriteAsync(source.Building, source.Room, id,
            async () => Project(await _meters.ReplaceAsync(id, input),
                source.Building));
    }

    public async Task<SliceCUtilityMeterRead> DeactivateAsync(Guid id)
    {
        if (id == Guid.Empty) throw Denied();
        var source = await ResolveMeterAsync(id);
        return await WriteAsync(source.Building, source.Room, id,
            async () => Project(await _meters.DeactivateAsync(id),
                source.Building));
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        if (id == Guid.Empty) throw Denied();
        var source = await ResolveMeterAsync(id);
        return await WriteAsync(source.Building, source.Room, id,
            async () =>
            {
                await _meters.DeleteAsync(id);
                return true;
            });
    }

    public async Task<SliceCMeterReplacementRead> GetReplacementAsync(Guid id)
    {
        if (id == Guid.Empty) throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await AuthorizedReplacements(db, actor, DateTime.UtcNow, id, null)
            .SingleOrDefaultAsync() ?? throw Denied();
    }

    public async Task<IReadOnlyList<SliceCMeterReplacementRead>>
        GetReplacementsByRoomAsync(Guid roomId)
    {
        if (roomId == Guid.Empty) throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        // A foreign/nonexistent room must be denied even with no
        // replacement records; a blank list must not bypass ownership.
        var now = DateTime.UtcNow;
        var allowedRoom = await (
            from room in db.Rooms.AsNoTracking()
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where room.Id == roomId && !room.IsDeleted &&
                  !floor.IsDeleted && !building.IsDeleted &&
                  db.OperatorBuildingGrants.Any(grant =>
                      grant.OperatorUserId == actor &&
                      grant.BuildingId == building.Id &&
                      grant.RevokedAtUtc == null &&
                      grant.GrantedAtUtc <= now &&
                      (grant.ExpiresAtUtc == null ||
                       grant.ExpiresAtUtc > now))
            select room.Id).AnyAsync();
        if (!allowedRoom) throw Denied();
        return await AuthorizedReplacements(db, actor, now, null, roomId)
            .ToListAsync();
    }

    private async Task<(Guid Building, Guid Room)> ResolveMeterAsync(Guid id)
    {
        var db = await _provider.GetDbContextAsync();
        var source = await (
            from meter in db.UtilityMeters.AsNoTracking()
            join room in db.Rooms.AsNoTracking()
                on meter.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where meter.Id == id && !meter.IsDeleted &&
                  !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted
            select new { Building = building.Id, Room = room.Id })
            .SingleOrDefaultAsync();
        if (source is null) throw Denied();
        return (source.Building, source.Room);
    }

    private static async Task<Guid> ResolveRoomBuildingAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        var building = await (
            from room in db.Rooms.AsNoTracking()
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join owner in db.Buildings.AsNoTracking()
                on floor.BuildingId equals owner.Id
            where room.Id == roomId && !room.IsDeleted &&
                  !floor.IsDeleted && !owner.IsDeleted
            select owner.Id).SingleOrDefaultAsync();
        return building != Guid.Empty ? building : throw Denied();
    }

    private async Task<T> WriteAsync<T>(
        Guid building, Guid roomId, Guid? meterId, Func<Task<T>> mutation)
    {
        if (building == Guid.Empty || roomId == Guid.Empty)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, TimeProvider.System)
            .ExecuteAsync(actor, building, async () =>
            {
                await using (var buildingLock = Command(db, """
                    SELECT "Id" FROM "Buildings"
                     WHERE "Id"=@building AND "IsDeleted"=FALSE FOR UPDATE
                    """))
                {
                    Add(buildingLock, "building", building);
                    if (await buildingLock.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                await using (var roomLock = Command(db, """
                    SELECT r."Id" FROM "BuildingManagementRooms" AS r
                      JOIN "BuildingManagementFloors" AS f ON f."Id" = r."FloorId"
                     WHERE r."Id"=@room AND f."BuildingId"=@building
                       AND r."IsDeleted"=FALSE AND f."IsDeleted"=FALSE
                     FOR UPDATE OF r
                    """))
                {
                    Add(roomLock, "room", roomId);
                    Add(roomLock, "building", building);
                    if (await roomLock.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                if (meterId.HasValue)
                {
                    await using var meterLock = Command(db, """
                        SELECT "Id" FROM "BuildingManagementUtilityMeters"
                         WHERE "Id"=@meter AND "RoomId"=@room
                           AND "IsDeleted"=FALSE FOR UPDATE
                        """);
                    Add(meterLock, "meter", meterId.Value);
                    Add(meterLock, "room", roomId);
                    if (await meterLock.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                return await mutation();
            });
    }

    private static IQueryable<SliceCUtilityMeterRead> AuthorizedMeters(
        BuildingManagementDbContext db, Guid actor, DateTime now,
        Guid? onlyMeterId = null)
        => from meter in db.UtilityMeters.AsNoTracking()
           join room in db.Rooms.AsNoTracking()
               on meter.RoomId equals room.Id
           join floor in db.Floors.AsNoTracking()
               on room.FloorId equals floor.Id
           join building in db.Buildings.AsNoTracking()
               on floor.BuildingId equals building.Id
           where (!onlyMeterId.HasValue || meter.Id == onlyMeterId.Value) &&
                 !meter.IsDeleted && !room.IsDeleted &&
                 !floor.IsDeleted && !building.IsDeleted &&
                 db.OperatorBuildingGrants.Any(grant =>
                     grant.OperatorUserId == actor &&
                     grant.BuildingId == building.Id &&
                     grant.RevokedAtUtc == null &&
                     grant.GrantedAtUtc <= now &&
                     (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
           orderby building.Id, room.Id, meter.UtilityType, meter.Id
           select new SliceCUtilityMeterRead(
               meter.Id, meter.RoomId, building.Id, meter.UtilityType,
               meter.MeterCode, meter.InitialReading,
               meter.InstalledDate, meter.IsActive);

    private static IQueryable<SliceCMeterReplacementRead> AuthorizedReplacements(
        BuildingManagementDbContext db, Guid actor, DateTime now,
        Guid? onlyReplacementId, Guid? onlyRoomId)
        => from replacement in db.MeterReplacements.AsNoTracking()
           join room in db.Rooms.AsNoTracking()
               on replacement.RoomId equals room.Id
           join floor in db.Floors.AsNoTracking()
               on room.FloorId equals floor.Id
           join building in db.Buildings.AsNoTracking()
               on floor.BuildingId equals building.Id
           where (!onlyReplacementId.HasValue ||
                     replacement.Id == onlyReplacementId.Value) &&
                 (!onlyRoomId.HasValue || room.Id == onlyRoomId.Value) &&
                 !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted &&
                 db.OperatorBuildingGrants.Any(grant =>
                     grant.OperatorUserId == actor &&
                     grant.BuildingId == building.Id &&
                     grant.RevokedAtUtc == null &&
                     grant.GrantedAtUtc <= now &&
                     (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
           orderby replacement.ReplacementDate, replacement.Id
           select new SliceCMeterReplacementRead(
               replacement.Id, replacement.OldMeterId,
               replacement.NewMeterId, room.Id, building.Id,
               replacement.UtilityType, replacement.ReplacementDate,
               replacement.OldPreviousReading, replacement.OldFinalReading,
               replacement.NewInitialReading);

    private static SliceCUtilityMeterRead Project(
        UtilityMeterDto meter, Guid building)
        => new(meter.Id, meter.RoomId, building, meter.UtilityType,
            meter.MeterCode, meter.InitialReading,
            meter.InstalledDate, meter.IsActive);

    private static SliceCMeterReplacementRead Project(
        MeterReplacementDto record, Guid building)
        => new(record.Id, record.OldMeterId, record.NewMeterId, record.RoomId,
            building, record.UtilityType, record.ReplacementDate,
            record.OldPreviousReading, record.OldFinalReading,
            record.NewInitialReading);

    private Guid Actor()
        => _user.IsAuthenticated && _user.Id is Guid id && id != Guid.Empty
            ? id : throw Denied();

    private static DbCommand Command(BuildingManagementDbContext db, string sql)
    {
        if (!db.Database.IsNpgsql())
            throw Denied();
        var tx = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, Guid value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static AbpAuthorizationException Denied()
        => new("Physical meters require persisted Utilities permission and a live building grant.");
}

public sealed record SliceCUtilityMeterRead(
    Guid Id, Guid RoomId, Guid BuildingId, UtilityType UtilityType,
    string MeterCode, decimal InitialReading,
    DateTime InstalledDate, bool IsActive);

/// <summary>Structural replacement evidence; no private notes/reason.</summary>
public sealed record SliceCMeterReplacementRead(
    Guid Id, Guid OldMeterId, Guid NewMeterId,
    Guid RoomId, Guid BuildingId, UtilityType UtilityType,
    DateTime ReplacementDate, decimal OldPreviousReading,
    decimal OldFinalReading, decimal NewInitialReading);
