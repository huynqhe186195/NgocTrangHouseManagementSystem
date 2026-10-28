using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.RoomReservations;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Staging-only reservation GET/list adapter: the legacy unscoped
/// RoomReservationAppService HTTP API is still DENY. A reservation has no
/// independent BuildingId, so membership must be joined through its
/// current Room/Floor/Building graph inside the same SQL query.
/// Do not project global Tenant PII, payment evidence, or free-form notes.
/// </summary>
public sealed class SliceBStagingReservationQuery
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly ICurrentUser _currentUser;
    private readonly SliceAStagingIdentityCommitLease _identityLease;

    public SliceBStagingReservationQuery(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        ICurrentUser currentUser,
        SliceAStagingIdentityCommitLease identityLease)
    {
        _provider = provider;
        _policy = policy;
        _currentUser = currentUser;
        _identityLease = identityLease;
    }

    public async Task<IReadOnlyList<SliceBScopedReservationRead>> GetListAsync(
        int skipCount, int maxResultCount)
    {
        if (skipCount < 0 || maxResultCount < 1 || maxResultCount > 100)
            throw Denied();

        var actor = RequireActor();
        await _policy.GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);

        return await AuthorizedReservations(
            await _provider.GetDbContextAsync(), actor)
            .Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public async Task<SliceBScopedReservationRead> GetAsync(Guid reservationId)
    {
        if (reservationId == Guid.Empty)
            throw Denied();

        var actor = RequireActor();
        await _policy.GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);

        return await AuthorizedReservations(
            await _provider.GetDbContextAsync(), actor, reservationId)
            .SingleOrDefaultAsync() ?? throw Denied();
    }

    private Guid RequireActor()
    {
        if (!_currentUser.IsAuthenticated ||
            _currentUser.Id is not Guid actor || actor == Guid.Empty)
            throw Denied();
        return actor;
    }

    private static IQueryable<SliceBScopedReservationRead> AuthorizedReservations(
        BuildingManagementDbContext db, Guid actor, Guid? reservationId = null)
    {
        var now = DateTime.UtcNow;
        return from reservation in db.RoomReservations.AsNoTracking()
               join room in db.Rooms.AsNoTracking()
                   on reservation.RoomId equals room.Id
               join floor in db.Floors.AsNoTracking()
                   on room.FloorId equals floor.Id
               join building in db.Buildings.AsNoTracking()
                   on floor.BuildingId equals building.Id
               where (!reservationId.HasValue || reservation.Id == reservationId.Value) &&
                     !reservation.IsDeleted && !room.IsDeleted &&
                     !floor.IsDeleted && !building.IsDeleted &&
                     db.OperatorBuildingGrants.Any(grant =>
                         grant.OperatorUserId == actor &&
                         grant.BuildingId == building.Id &&
                         grant.RevokedAtUtc == null &&
                         grant.GrantedAtUtc <= now &&
                         (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
               orderby reservation.ReservationNumber, reservation.Id
               select new SliceBScopedReservationRead(
                   reservation.Id, reservation.RoomId, building.Id,
                   reservation.TenantId, reservation.ReservationNumber,
                   reservation.Status, reservation.ExpectedMoveInDate,
                   reservation.QuotedMonthlyRent,
                   reservation.RequiredDepositAmount,
                   reservation.MinimumReservationDepositAmount);
    }

    private static AbpAuthorizationException Denied()
        => new("No active building-scoped Contracts.Read authorization for reservation.");
}

/// <summary>Building-scoped read projection; no global tenant identity fields.</summary>
public sealed record SliceBScopedReservationRead(
    Guid Id, Guid RoomId, Guid BuildingId, Guid TenantId,
    string ReservationNumber, RoomReservationStatus Status,
    DateTime ExpectedMoveInDate, decimal QuotedMonthlyRent,
    decimal RequiredDepositAmount, decimal MinimumReservationDepositAmount);
