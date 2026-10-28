using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
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
