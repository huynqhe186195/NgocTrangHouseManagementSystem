using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Isolated-staging-only, explicitly registered read adapter for Slice B.
/// The old unscoped ContractAppService HTTP actions stay deny-all.
/// Returned rows are filtered via a live membership JOIN/EXISTS in SQL.
/// </summary>
public sealed class SliceBStagingContractQuery
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _access;
    private readonly ICurrentUser _currentUser;
    private readonly SliceAStagingIdentityCommitLease _identityLease;

    public SliceBStagingContractQuery(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy access,
        ICurrentUser currentUser,
        SliceAStagingIdentityCommitLease identityLease)
    {
        _provider = provider;
        _access = access;
        _currentUser = currentUser;
        _identityLease = identityLease;
    }

    public async Task<IReadOnlyList<SliceBScopedContractRead>> GetListAsync(
        int skipCount, int maxResultCount)
    {
        if (skipCount < 0 || maxResultCount < 1 || maxResultCount > 100)
            throw Denied();
        var actor = RequireActor();
        await _access.GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        var rows = AuthorizedContracts(await _provider.GetDbContextAsync(), actor);
        return await rows.Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public async Task<SliceBScopedContractRead> GetAsync(Guid contractId)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        return await AuthorizedContracts(
                await _provider.GetDbContextAsync(), actor, contractId)
            .SingleOrDefaultAsync()
            ?? throw Denied();
    }

    /// <summary>
    /// Return only relation metadata, never global Tenant PII. The contract
    /// scope is checked before the query, and membership is revalidated
    /// within the SQL relation query itself.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedContractTenantLink>> GetTenantsAsync(
        Guid contractId)
    {
        var scoped = await GetAsync(contractId);
        var actor = RequireActor();
        var db = await _provider.GetDbContextAsync();
        return await (
            from link in db.ContractTenants.AsNoTracking()
            where link.ContractId == contractId && !link.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == scoped.BuildingId &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= DateTime.UtcNow &&
                    (grant.ExpiresAtUtc == null ||
                     grant.ExpiresAtUtc > DateTime.UtcNow))
            orderby link.TenantId
            select new SliceBScopedContractTenantLink(link.TenantId, link.Role))
            .ToListAsync();
    }

    /// <summary>
    /// A move-out request inherits contract scope. Join its current
    /// Contract→Room→Floor→Building path AND the live operator grant in SQL
    /// before returning any result; do not expose free-form private notes.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedMoveOutRequestRead>> GetMoveOutRequestsAsync(
        Guid contractId)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);
        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        return await (
            from request in db.MoveOutRequests.AsNoTracking()
            join contract in db.Contracts.AsNoTracking()
                on request.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where request.ContractId == contractId &&
                !request.IsDeleted && !contract.IsDeleted &&
                !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted &&
                request.RoomId == room.Id &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            orderby request.CreationTime descending, request.Id
            select new SliceBScopedMoveOutRequestRead(
                request.Id, request.ContractId, request.RoomId,
                building.Id, request.Status, request.RequestedMoveOutDate,
                request.ApprovedMoveOutDate, request.CreationTime))
            .ToListAsync();
    }

    /// <summary>
    /// The checklist must be scoped using the request's current Contract,
    /// Room and Building, not by trusting an arbitrary request ID.
    /// </summary>
    public async Task<IReadOnlyList<SliceBScopedMoveOutChecklistRead>> GetMoveOutChecklistAsync(
        Guid contractId, Guid requestId)
    {
        if (contractId == Guid.Empty || requestId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        await _access.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ReadContract);
        await _identityLease.AcquireContractReadAsync(actor);

        var db = await _provider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var authorizedRequest = await (
            from request in db.MoveOutRequests.AsNoTracking()
            join contract in db.Contracts.AsNoTracking()
                on request.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where request.Id == requestId && request.ContractId == contractId &&
                request.RoomId == room.Id &&
                !request.IsDeleted && !contract.IsDeleted && !room.IsDeleted &&
                !floor.IsDeleted && !building.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            select request.Id).AnyAsync();
        if (!authorizedRequest)
            throw Denied();

        // Check the grant again in the SAME SELECT that reads checklist
        // data. A membership revoke between preflight and this query must
        // never turn a stale preflight into disclosure.
        return await (
            from task in db.MoveOutChecklistTasks.AsNoTracking()
            join request in db.MoveOutRequests.AsNoTracking()
                on task.MoveOutRequestId equals request.Id
            join contract in db.Contracts.AsNoTracking()
                on request.ContractId equals contract.Id
            join room in db.Rooms.AsNoTracking()
                on contract.RoomId equals room.Id
            join floor in db.Floors.AsNoTracking()
                on room.FloorId equals floor.Id
            join building in db.Buildings.AsNoTracking()
                on floor.BuildingId equals building.Id
            where request.Id == requestId && request.ContractId == contractId &&
                request.RoomId == room.Id &&
                !request.IsDeleted && !contract.IsDeleted &&
                !room.IsDeleted && !floor.IsDeleted && !building.IsDeleted &&
                db.OperatorBuildingGrants.Any(grant =>
                    grant.OperatorUserId == actor &&
                    grant.BuildingId == building.Id &&
                    grant.RevokedAtUtc == null &&
                    grant.GrantedAtUtc <= now &&
                    (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
            orderby task.Type
            select new SliceBScopedMoveOutChecklistRead(
                task.Id, task.MoveOutRequestId, task.Type,
                task.IsCompleted, task.CompletedAt))
            .ToListAsync();
    }

    private Guid RequireActor()
    {
        if (!_currentUser.IsAuthenticated || _currentUser.Id is not Guid id ||
            id == Guid.Empty)
            throw Denied();
        return id;
    }

    private static IQueryable<SliceBScopedContractRead> AuthorizedContracts(
        BuildingManagementDbContext db, Guid actor,
        Guid? onlyContractId = null)
    {
        var now = DateTime.UtcNow;
        return from contract in db.Contracts.AsNoTracking()
               join room in db.Rooms.AsNoTracking()
                   on contract.RoomId equals room.Id
               join floor in db.Floors.AsNoTracking()
                   on room.FloorId equals floor.Id
               join building in db.Buildings.AsNoTracking()
                   on floor.BuildingId equals building.Id
               where (!onlyContractId.HasValue ||
                       contract.Id == onlyContractId.Value) &&
                     !contract.IsDeleted && !room.IsDeleted &&
                     !floor.IsDeleted && !building.IsDeleted &&
                     db.OperatorBuildingGrants.Any(grant =>
                         grant.OperatorUserId == actor &&
                         grant.BuildingId == building.Id &&
                         grant.RevokedAtUtc == null &&
                         grant.GrantedAtUtc <= now &&
                         (grant.ExpiresAtUtc == null || grant.ExpiresAtUtc > now))
               orderby contract.ContractNumber, contract.Id
               select new SliceBScopedContractRead(
                   contract.Id, contract.RoomId, building.Id,
                   contract.ContractNumber, contract.Status,
                   contract.MonthlyRent, contract.StartDate, contract.EndDate);
    }

    private static AbpAuthorizationException Denied()
        => new("No active building-scoped Contracts.Read authorization.");
}

public sealed record SliceBScopedContractRead(
    Guid Id, Guid RoomId, Guid BuildingId, string ContractNumber,
    ContractStatus Status, decimal MonthlyRent, DateTime StartDate,
    DateTime? EndDate);

public sealed record SliceBScopedContractTenantLink(
    Guid TenantId, ContractTenantRole Role);


/// <summary>Move-out metadata only: no personal reasons or review notes.</summary>
public sealed record SliceBScopedMoveOutRequestRead(
    Guid Id, Guid ContractId, Guid RoomId, Guid BuildingId,
    MoveOutRequestStatus Status, DateOnly RequestedMoveOutDate,
    DateOnly? ApprovedMoveOutDate, DateTime CreationTime);


/// <summary>Checklist progress only; no unrelated tenant identity data.</summary>
public sealed record SliceBScopedMoveOutChecklistRead(
    Guid Id, Guid MoveOutRequestId, MoveOutChecklistTaskType Type,
    bool IsCompleted, DateTime? CompletedAt);
