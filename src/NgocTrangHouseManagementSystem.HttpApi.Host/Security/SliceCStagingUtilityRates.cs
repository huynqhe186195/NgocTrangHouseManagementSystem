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
/// Isolated-staging-only utility tariffs. NEVER expose the unscoped
/// UtilityRateAppService controller. Server-owned building membership and
/// distinct persisted Identity utility permissions are required for every
/// request. All writes serialize on the Building row BEFORE the rate row
/// and share the original UtilityRateAppService's business validation.
/// </summary>
public sealed class SliceCStagingUtilityRates
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly ICurrentUser _user;
    private readonly IUtilityRateAppService _rates;

    public SliceCStagingUtilityRates(
        IDbContextProvider<BuildingManagementDbContext> provider,
        SliceAStagingIdentityCommitLease identity,
        ICurrentUser user, IUtilityRateAppService rates)
    {
        _provider = provider;
        _identity = identity;
        _user = user;
        _rates = rates;
    }

    public async Task<IReadOnlyList<SliceCUtilityRateRead>> GetListAsync(
        int skipCount, int maxResultCount)
    {
        if (skipCount < 0 || maxResultCount is < 1 or > 100)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        return await AuthorizedRates(db, actor, now)
            .Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public async Task<SliceCUtilityRateRead> GetAsync(Guid id)
    {
        if (id == Guid.Empty)
            throw Denied();
        var actor = Actor();
        await _identity.AcquireUtilityReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await AuthorizedRates(db, actor, DateTime.UtcNow)
            .SingleOrDefaultAsync(x => x.Id == id) ?? throw Denied();
    }

    public Task<SliceCUtilityRateRead> CreateAsync(CreateUtilityRateDto input)
    {
        if (input is null || input.BuildingId == Guid.Empty)
            throw Denied();
        return WriteAsync(input.BuildingId, null, async () =>
            Project(await _rates.CreateAsync(input)));
    }

    public async Task<SliceCUtilityRateRead> UpdateAsync(
        Guid rateId, UpdateUtilityRateDto input)
    {
        if (rateId == Guid.Empty || input is null ||
            input.BuildingId == Guid.Empty)
            throw Denied();
        var db = await _provider.GetDbContextAsync();
        var currentBuilding = await db.UtilityRates.AsNoTracking()
            .Where(x => x.Id == rateId && !x.IsDeleted)
            .Select(x => x.BuildingId).SingleOrDefaultAsync();
        // Do not allow changing tariff ownership; never let a caller
        // authorize with a self-declared target BuildingId.
        if (currentBuilding == Guid.Empty ||
            currentBuilding != input.BuildingId)
            throw Denied();
        return await WriteAsync(currentBuilding, rateId, async () =>
            Project(await _rates.UpdateAsync(rateId, input)));
    }

    public async Task<bool> DeleteAsync(Guid rateId)
    {
        if (rateId == Guid.Empty)
            throw Denied();
        var db = await _provider.GetDbContextAsync();
        var building = await db.UtilityRates.AsNoTracking()
            .Where(x => x.Id == rateId && !x.IsDeleted)
            .Select(x => x.BuildingId).SingleOrDefaultAsync();
        if (building == Guid.Empty)
            throw Denied();
        return await WriteAsync(building, rateId, async () =>
        {
            await _rates.DeleteAsync(rateId);
            return true;
        });
    }

    private async Task<T> WriteAsync<T>(
        Guid building, Guid? rateId, Func<Task<T>> mutation)
    {
        var actor = Actor();
        await _identity.AcquireUtilityManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, TimeProvider.System)
            .ExecuteAsync(actor, building, async () =>
            {
                // Serializes all same-building tariff writes, including
                // simultaneous overlapping INSERTs. Consistent lock order:
                // Building UPDATE -> rate UPDATE -> original service.
                await using (var lockBuilding = Command(db, """
                    SELECT "Id" FROM "Buildings"
                     WHERE "Id"=@id AND "IsDeleted"=FALSE FOR UPDATE
                    """))
                {
                    Add(lockBuilding, "id", building);
                    if (await lockBuilding.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                if (rateId.HasValue)
                {
                    await using var lockRate = Command(db, """
                        SELECT "Id" FROM "BuildingManagementUtilityRates"
                         WHERE "Id"=@rate AND "BuildingId"=@building
                           AND "IsDeleted"=FALSE FOR UPDATE
                        """);
                    Add(lockRate, "rate", rateId.Value);
                    Add(lockRate, "building", building);
                    if (await lockRate.ExecuteScalarAsync() is not Guid)
                        throw Denied();
                }
                return await mutation();
            });
    }

    private static IQueryable<SliceCUtilityRateRead> AuthorizedRates(
        BuildingManagementDbContext db, Guid actor, DateTime now)
        => from rate in db.UtilityRates.AsNoTracking()
           join building in db.Buildings.AsNoTracking()
             on rate.BuildingId equals building.Id
           where !rate.IsDeleted && !building.IsDeleted &&
             db.OperatorBuildingGrants.Any(membership =>
               membership.OperatorUserId == actor &&
               membership.BuildingId == building.Id &&
               membership.RevokedAtUtc == null &&
               membership.GrantedAtUtc <= now &&
               (membership.ExpiresAtUtc == null ||
                membership.ExpiresAtUtc > now))
           orderby rate.BuildingId, rate.UtilityType,
               rate.EffectiveFrom, rate.Id
           select new SliceCUtilityRateRead(
               rate.Id, rate.BuildingId, rate.UtilityType,
               rate.UnitPrice, rate.EffectiveFrom, rate.EffectiveTo);

    private static SliceCUtilityRateRead Project(UtilityRateDto rate)
        => new(rate.Id, rate.BuildingId, rate.UtilityType,
            rate.UnitPrice, rate.EffectiveFrom, rate.EffectiveTo);

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

    private static void Add(DbCommand command, string name, Guid id)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = id;
        command.Parameters.Add(parameter);
    }

    private static AbpAuthorizationException Denied()
        => new("Utility tariff requires an active persisted Utilities permission and live building membership.");
}

public sealed record SliceCUtilityRateRead(
    Guid Id, Guid BuildingId, UtilityType UtilityType,
    decimal UnitPrice, DateTime EffectiveFrom, DateTime? EffectiveTo);
