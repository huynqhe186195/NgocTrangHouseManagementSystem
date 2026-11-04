using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.RoomChanges;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// EXPLICIT ISOLATED-STAGING ONLY. The first tariff-write interface is
/// restricted to the persisted BootstrapOwner permission in addition to a
/// live, scoped Rooms.Manage permission and building membership. Ordinary
/// Staff are NOT implicitly allowed to edit service prices. This is narrower
/// than the eventual per-building Owner role and must not be deployed live.
/// </summary>
public sealed class SliceBStagingRoomScopedServiceTariffs
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _clock;

    public SliceBStagingRoomScopedServiceTariffs(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        SliceAStagingIdentityCommitLease identity,
        ICurrentUser user)
    {
        _provider = provider;
        _policy = policy;
        _resources = resources;
        _identity = identity;
        _user = user;
        _clock = TimeProvider.System;
    }

    public Task<SliceBRoomScopedServiceTariffRead[]> GetListAsync(
        Guid roomId, Guid serviceId, int skipCount, int maxResultCount)
        => ExecuteScopedAsync(roomId, async (db, _, _) =>
        {
            if (serviceId == Guid.Empty || skipCount < 0 ||
                maxResultCount is < 1 or > 100)
                throw Denied();

            var rows = await db.RoomScopedServiceTariffVersions.AsNoTracking()
                .Where(x => x.RoomId == roomId && x.ServiceId == serviceId)
                .OrderBy(x => x.Version)
                .Skip(skipCount).Take(maxResultCount).ToArrayAsync();
            foreach (var row in rows) row.VerifyFingerprint();
            return rows.Select(AsRead).ToArray();
        });

    public Task<SliceBRoomScopedServiceTariffRead> AppendAsync(
        Guid roomId, SliceBRoomScopedServiceTariffInput input)
        => ExecuteScopedAsync(roomId, async (db, actor, _) =>
        {
            if (input is null || input.ServiceId == Guid.Empty ||
                input.EffectiveFrom == default)
                throw Denied();
            var latest = await db.RoomScopedServiceTariffVersions
                .AsNoTracking()
                .Where(x => x.RoomId == roomId && x.ServiceId == input.ServiceId)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync();

            RoomScopedServiceTariffVersion row;
            try
            {
                var next = latest is null ? 1 : checked(latest.Version + 1);
                row = RoomScopedServiceTariffVersion.Create(
                    Guid.NewGuid(), roomId, input.ServiceId, next,
                    input.UnitPriceVnd, input.BillingUnit, input.ChargingRule,
                    input.EffectiveFrom, input.EffectiveThrough, actor,
                    _clock.GetUtcNow().UtcDateTime, input.SignedPolicyReference);
                if (latest is not null)
                    RoomScopedServiceTariffVersion.RequireNonOverlapping(latest, row);

                var vietnamToday = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(row.ApprovedAtUtc,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));
                if (row.EffectiveFrom < vietnamToday)
                    throw Denied();
            }
            catch (Exception error) when (error is InvalidOperationException or
                ArgumentException or OverflowException)
            {
                throw Denied();
            }

            db.RoomScopedServiceTariffVersions.Add(row);
            return AsRead(row);
        });

    private async Task<T> ExecuteScopedAsync<T>(
        Guid roomId,
        Func<BuildingManagementDbContext, Guid, Guid, Task<T>> work)
    {
        if (roomId == Guid.Empty ||
            !_user.IsAuthenticated || _user.Id is not Guid actor ||
            actor == Guid.Empty)
            throw Denied();
        var scope = await _resources.ReadAsync(
            BuildingBusinessResourceKind.Room, roomId);
        if (scope is null || scope.ResourceId != roomId ||
            scope.BuildingId == Guid.Empty)
            throw Denied();

        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, roomId,
            BuildingBusinessCapability.ManageRoom);

        // The root's actual persisted Identity permission, active user row
        // and room-management permission stay locked until Building COMMIT.
        // This deliberately does NOT borrow a stale Owner/Staff role claim.
        await _identity.AcquireBootstrapAsync(actor);
        await _identity.AcquireAsync(actor, BuildingBusinessCapability.ManageRoom);

        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, _clock)
            .ExecuteAsync(actor, scope.BuildingId, async () =>
            {
                // Enforce room -> floor -> building scope under the same
                // transaction as tariff INSERT. Locks serialize mutations.
                await using var command = Command(db, """
                    SELECT f."BuildingId"
                    FROM "BuildingManagementRooms" r
                    JOIN "BuildingManagementFloors" f ON f."Id"=r."FloorId"
                    JOIN "Buildings" b ON b."Id"=f."BuildingId"
                    WHERE r."Id"=@room AND r."IsDeleted"=FALSE
                      AND f."IsDeleted"=FALSE AND b."IsDeleted"=FALSE
                    FOR UPDATE OF r
                    """);
                var parameter = command.CreateParameter();
                parameter.ParameterName = "room";
                parameter.Value = roomId;
                command.Parameters.Add(parameter);
                var actual = await command.ExecuteScalarAsync();
                if (actual is not Guid building || building != scope.BuildingId)
                    throw Denied();

                return await work(db, actor, building);
            }, () => _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, roomId,
                BuildingBusinessCapability.ManageRoom));
    }

    private static SliceBRoomScopedServiceTariffRead AsRead(
        RoomScopedServiceTariffVersion row)
        => new(row.Id, row.RoomId, row.ServiceId, row.Version,
            row.UnitPriceVnd, row.BillingUnit, row.ChargingRule,
            row.EffectiveFrom, row.EffectiveThrough,
            row.ApprovedByUserId, row.ApprovedAtUtc, row.EvidenceSha256,
            IsPostedInvoice: false, IsExternalReceipt: false);

    private static DbCommand Command(
        BuildingManagementDbContext db, string sql)
    {
        if (!db.Database.IsNpgsql())
            throw Denied();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw Denied();
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static AbpAuthorizationException Denied()
        => new("Room-specific service tariff requires active BootstrapOwner, scoped Rooms.Manage and isolated staging.");
}

public sealed record SliceBRoomScopedServiceTariffInput(
    Guid ServiceId,
    decimal UnitPriceVnd,
    RoomScopedServiceBillingUnit BillingUnit,
    RoomScopedServiceChargingRule ChargingRule,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveThrough,
    string? SignedPolicyReference);

public sealed record SliceBRoomScopedServiceTariffRead(
    Guid Id, Guid RoomId, Guid ServiceId, int Version,
    decimal UnitPriceVnd,
    RoomScopedServiceBillingUnit BillingUnit,
    RoomScopedServiceChargingRule ChargingRule,
    DateOnly EffectiveFrom, DateOnly? EffectiveThrough,
    Guid ApprovedByUserId, DateTime ApprovedAtUtc,
    string EvidenceSha256, bool IsPostedInvoice, bool IsExternalReceipt);
