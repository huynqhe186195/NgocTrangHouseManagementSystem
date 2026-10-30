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
/// Isolated-staging-only reading CRUD. The old unscoped conventional
/// MeterReadingAppService API remains DENY ALL. SQL resolves the owning
/// building through reading -> physical meter -> room -> floor, never from
/// a caller-supplied building ID. Reads filter membership before paging;
/// mutations fence the building grant and the persisted Identity permission
/// until commit, lock building then meter then existing reading, and delegate
/// month/date/monotonicity/replacement rules to the original app service.
/// </summary>
public sealed class SliceCStagingMeterReadings
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly ICurrentUser _user;
    private readonly IMeterReadingAppService _readings;

    public SliceCStagingMeterReadings(
        IDbContextProvider<BuildingManagementDbContext> provider,
        SliceAStagingIdentityCommitLease identity,
        ICurrentUser user, IMeterReadingAppService readings)
    {
        _provider = provider;
        _identity = identity;
        _user = user;
        _readings = readings;
    }

    public async Task<IReadOnlyList<SliceCMeterReadingRead>> GetListAsync(
        int skipCount, int maxResultCount)
    {
        if (skipCount < 0 || maxResultCount is < 1 or > 100)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await AuthorizedReadings(db, actor, DateTime.UtcNow)
            .Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public async Task<SliceCMeterReadingRead> GetAsync(Guid id)
    {
        if (id == Guid.Empty)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await AuthorizedReadings(db, actor, DateTime.UtcNow, id)
            .SingleOrDefaultAsync() ?? throw Denied();
    }

    public async Task<SliceCMeterReadingRead> CreateAsync(
        CreateMeterReadingDto input)
    {
        if (input is null || input.UtilityMeterId == Guid.Empty)
            throw Denied();
        var db = await _provider.GetDbContextAsync();
        var building = await ResolveMeterBuildingAsync(db, input.UtilityMeterId);
        return await WriteAsync(building, input.UtilityMeterId, null,
            async () => Project(await _readings.CreateAsync(input), building));
    }

    public async Task<SliceCMeterReadingRead> UpdateAsync(
        Guid id, UpdateMeterReadingDto input)
    {
        if (id == Guid.Empty || input is null)
            throw Denied();
        var db = await _provider.GetDbContextAsync();
        var meterId = await db.MeterReadings.AsNoTracking()
            .Where(x => x.Id == id && !x.IsDeleted)
            .Select(x => x.UtilityMeterId).SingleOrDefaultAsync();
        if (meterId == Guid.Empty)
            throw Denied();
        var building = await ResolveMeterBuildingAsync(db, meterId);
        return await WriteAsync(building, meterId, id,
            async () => Project(await _readings.UpdateAsync(id, input), building));
    }

    private async Task<T> WriteAsync<T>(
        Guid building, Guid meterId, Guid? readingId, Func<Task<T>> mutation)
    {
        if (building == Guid.Empty || meterId == Guid.Empty)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, TimeProvider.System)
            .ExecuteAsync(actor, building, async () =>
            {
                // Lock order: membership SHARE -> building UPDATE ->
                // physical meter UPDATE -> monthly reading UPDATE.
                // The original app service re-acquires the same meter lock.
                await using (var buildingLock = Command(db, """
                    SELECT "Id" FROM "Buildings"
                     WHERE "Id"=@building AND "IsDeleted"=FALSE FOR UPDATE
                    """))
                {
                    Add(buildingLock, "building", building);
                    if (await buildingLock.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                // Re-join *inside* the transaction after locking the
                // building. Deleted or moved source rows must fail closed.
                await using (var meterLock = Command(db, """
                    SELECT m."Id" FROM "BuildingManagementUtilityMeters" AS m
                      JOIN "BuildingManagementRooms" AS r ON r."Id" = m."RoomId"
                      JOIN "BuildingManagementFloors" AS f ON f."Id" = r."FloorId"
                     WHERE m."Id"=@meter AND f."BuildingId"=@building
                       AND m."IsDeleted"=FALSE AND r."IsDeleted"=FALSE
                       AND f."IsDeleted"=FALSE
                     FOR UPDATE OF m
                    """))
                {
                    Add(meterLock, "meter", meterId);
                    Add(meterLock, "building", building);
                    if (await meterLock.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }

                if (readingId.HasValue)
                {
                    await using var readingLock = Command(db, """
                        SELECT "Id" FROM "BuildingManagementMeterReadings"
                         WHERE "Id"=@reading AND "UtilityMeterId"=@meter
                           AND "IsDeleted"=FALSE FOR UPDATE
                        """);
                    Add(readingLock, "reading", readingId.Value);
                    Add(readingLock, "meter", meterId);
                    if (await readingLock.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                return await mutation();
            });
    }

    private static async Task<Guid> ResolveMeterBuildingAsync(
        BuildingManagementDbContext db, Guid meterId)
    {
        if (meterId == Guid.Empty)
            throw Denied();
        var building = await (
            from meter in db.UtilityMeters.AsNoTracking()
            join room in db.Rooms.AsNoTracking()
                on meter.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join owner in db.Buildings.AsNoTracking()
                on floor.BuildingId equals owner.Id
            where meter.Id == meterId && !meter.IsDeleted &&
                  !room.IsDeleted && !floor.IsDeleted && !owner.IsDeleted
            select owner.Id).SingleOrDefaultAsync();
        return building != Guid.Empty ? building : throw Denied();
    }

    private static IQueryable<SliceCMeterReadingRead> AuthorizedReadings(
        BuildingManagementDbContext db, Guid actor, DateTime now,
        Guid? onlyReadingId = null)
        => from reading in db.MeterReadings.AsNoTracking()
           join meter in db.UtilityMeters.AsNoTracking()
               on reading.UtilityMeterId equals meter.Id
           join room in db.Rooms.AsNoTracking()
               on meter.RoomId equals room.Id
           join floor in db.Floors.AsNoTracking()
               on room.FloorId equals floor.Id
           join owner in db.Buildings.AsNoTracking()
               on floor.BuildingId equals owner.Id
           where (!onlyReadingId.HasValue || reading.Id == onlyReadingId.Value) &&
                 !reading.IsDeleted && !meter.IsDeleted && !room.IsDeleted &&
                 !floor.IsDeleted && !owner.IsDeleted &&
                 db.OperatorBuildingGrants.Any(grant =>
                     grant.OperatorUserId == actor &&
                     grant.BuildingId == owner.Id &&
                     grant.RevokedAtUtc == null &&
                     grant.GrantedAtUtc <= now &&
                     (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
           orderby owner.Id, meter.Id, reading.BillingYear,
               reading.BillingMonth, reading.Id
           select new SliceCMeterReadingRead(
               reading.Id, meter.Id, owner.Id,
               reading.BillingYear, reading.BillingMonth,
               reading.PreviousReading, reading.CurrentReading,
               reading.ReadingDate);

    private static SliceCMeterReadingRead Project(MeterReadingDto reading,
        Guid building)
        => new(reading.Id, reading.UtilityMeterId, building,
            reading.BillingYear, reading.BillingMonth,
            reading.PreviousReading, reading.CurrentReading,
            reading.ReadingDate);

    private Guid Actor()
        => _user.IsAuthenticated && _user.Id is Guid id && id != Guid.Empty
            ? id : throw Denied();

    private static DbCommand Command(BuildingManagementDbContext db, string sql)
    {
        if (!db.Database.IsNpgsql())
            throw Denied();
        var tx = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, Guid id)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = id;
        command.Parameters.Add(parameter);
    }

    private static AbpAuthorizationException Denied()
        => new("Meter readings require persisted Utilities permission and a live building grant.");
}

/// <summary>Only non-private billing evidence crosses the HTTP boundary.</summary>
public sealed record SliceCMeterReadingRead(
    Guid Id, Guid UtilityMeterId, Guid BuildingId,
    int BillingYear, int BillingMonth,
    decimal PreviousReading, decimal CurrentReading, DateTime ReadingDate);
