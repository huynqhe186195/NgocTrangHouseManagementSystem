using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomChanges;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Phase 2H isolated staging request/review ONLY. Never executes a transfer,
/// changes a contract/room or settles/carries a deposit. Every operation
/// requires current ABP Contracts.Manage + active Identity permission lease
/// and live Building membership locked through the same PostgreSQL UoW.
/// </summary>
public sealed class SliceBStagingRoomChangeRequests
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly ICurrentUser _current;
    private readonly SliceAStagingIdentityCommitLease _identity;

    public SliceBStagingRoomChangeRequests(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        ICurrentUser current,
        SliceAStagingIdentityCommitLease identity)
    {
        _provider = provider;
        _policy = policy;
        _resources = resources;
        _current = current;
        _identity = identity;
    }

    public Task<SliceBRoomChangeRequestRead> SubmitAsync(
        Guid contractId, SliceBRoomChangeSubmitInput input)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || input.TargetRoomId == Guid.Empty ||
                input.TargetRoomId == sourceRoom ||
                input.RequestedDate == default ||
                input.RequestedDate < DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))))
                throw Denied();

            // The target must be independently authorized; a guessed GUID
            // must never be usable to infer/mutate another building.
            await _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, input.TargetRoomId,
                BuildingBusinessCapability.ManageContract);

            // Lock both rooms in stable GUID order. Source contract was
            // already FOR UPDATE locked, so two competing transfers cannot
            // bypass a source contract's single-open-request uniqueness.
            var rooms = new[] { sourceRoom, input.TargetRoomId }
                .OrderBy(x => x).ToArray();
            (Guid Building, RoomStatus Status) source = default;
            (Guid Building, RoomStatus Status) target = default;
            foreach (var roomId in rooms)
            {
                var locked = await LockRoomStatusAsync(db, roomId);
                if (roomId == sourceRoom) source = locked;
                else target = locked;
            }
            var contract = await db.Contracts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == contractId && !x.IsDeleted);
            if (contract is null || contract.Status != ContractStatus.Active ||
                contract.RoomId != sourceRoom ||
                source.Building != building || target.Building != building ||
                source.Status != RoomStatus.Occupied ||
                target.Status != RoomStatus.Available ||
                await db.MoveOutRequests.AsNoTracking().AnyAsync(x =>
                    x.ContractId == contractId && !x.IsDeleted &&
                    (x.Status == MoveOutRequestStatus.Pending ||
                     x.Status == MoveOutRequestStatus.Approved)) ||
                await db.Contracts.AsNoTracking().AnyAsync(x =>
                    x.RoomId == input.TargetRoomId && !x.IsDeleted &&
                    (x.Status == ContractStatus.Active ||
                     x.Status == ContractStatus.Signed)) ||
                await db.RoomReservations.AsNoTracking().AnyAsync(x =>
                    x.RoomId == input.TargetRoomId && !x.IsDeleted &&
                    (x.Status == RoomReservationStatus.PendingPayment ||
                     x.Status == RoomReservationStatus.Reserved)))
                throw Denied();

            RoomChangeRequest request;
            try
            {
                request = RoomChangeRequest.Submit(
                    Guid.NewGuid(), contractId, sourceRoom,
                    input.TargetRoomId, building, input.RequestedDate,
                    input.Reason, actor, DateTime.UtcNow, contract.Status,
                    source.Status, target.Status);
            }
            catch (InvalidOperationException) { throw Denied(); }

            db.RoomChangeRequests.Add(request);
            return AsRead(request);
        });

    public Task<SliceBRoomChangeRequestRead> GetAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var item = await db.RoomChangeRequests.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == requestId &&
                    x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building) ?? throw Denied();
            return AsRead(item);
        }, permitEndedContract: true);

    public Task<SliceBRoomChangeRequestRead[]> GetListAsync(Guid contractId)
        => ExecuteScopedAsync(contractId, async (db, _, building, sourceRoom) =>
        {
            var items = await db.RoomChangeRequests.AsNoTracking()
                .Where(x => x.SourceContractId == contractId &&
                    x.SourceRoomId == sourceRoom &&
                    x.BuildingId == building)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(100)
                .ToArrayAsync();
            return items.Select(AsRead).ToArray();
        }, permitEndedContract: true);

    public Task<SliceBRoomChangeRequestRead> HoldForPolicyAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeReviewInput input)
        => ReviewAsync(contractId, requestId, input,
            (r, actor, now, note) => r.HoldForPolicy(actor, now, note));

    public Task<SliceBRoomChangeRequestRead> RejectAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeReviewInput input)
        => ReviewAsync(contractId, requestId, input,
            (r, actor, now, note) => r.Reject(actor, now, note));

    public Task<SliceBRoomChangeRequestRead> CancelAsync(
        Guid contractId, Guid requestId)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            try { request.Cancel(actor, DateTime.UtcNow); }
            catch (InvalidOperationException) { throw Denied(); }
            return AsRead(request);
        });

    private Task<SliceBRoomChangeRequestRead> ReviewAsync(
        Guid contractId, Guid requestId, SliceBRoomChangeReviewInput input,
        Action<RoomChangeRequest, Guid, DateTime, string?> action)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null) throw Denied();
            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            try { action(request, actor, DateTime.UtcNow, input.ReviewNotes); }
            catch (InvalidOperationException) { throw Denied(); }
            return AsRead(request);
        });

    private async Task<T> ExecuteScopedAsync<T>(
        Guid contractId,
        Func<BuildingManagementDbContext, Guid, Guid, Guid, Task<T>> work,
        bool permitEndedContract = false)
    {
        if (contractId == Guid.Empty ||
            !_current.IsAuthenticated || _current.Id is not Guid actor ||
            actor == Guid.Empty)
            throw Denied();
        var scope = await _resources.ReadAsync(
            BuildingBusinessResourceKind.Contract, contractId);
        if (scope is null || scope.ResourceId != contractId ||
            scope.BuildingId == Guid.Empty)
            throw Denied();
        var building = scope.BuildingId;
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);

        var db = await _provider.GetDbContextAsync();
        return await new EfBuildingScopedWriteFenceCandidate(db, TimeProvider.System)
            .ExecuteAsync(actor, building, async () =>
            {
                var sourceRoom = await LockContractRoomAsync(db, contractId);
                var sourceBuilding = await LockRoomBuildingAsync(db, sourceRoom);
                if (sourceBuilding != building)
                    throw Denied();
                var contract = await db.Contracts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == contractId && !x.IsDeleted);
                if (contract is null || contract.RoomId != sourceRoom ||
                    (contract.Status != ContractStatus.Active &&
                     !(permitEndedContract &&
                       contract.Status == ContractStatus.Ended)))
                    throw Denied();
                return await work(db, actor, building, sourceRoom);
            }, () => _policy.EnsureResourceAsync(
                BuildingBusinessResourceKind.Contract, contractId,
                BuildingBusinessCapability.ManageContract));
    }

    private static async Task<RoomChangeRequest> LockRequestAsync(
        BuildingManagementDbContext db, Guid requestId,
        Guid contractId, Guid sourceRoomId, Guid buildingId)
    {
        if (requestId == Guid.Empty) throw Denied();
        await using (var cmd = Command(db, """
            SELECT "Id" FROM "BuildingManagementRoomChangeRequests"
             WHERE "Id" = @id AND "SourceContractId" = @contract
               AND "SourceRoomId" = @room AND "BuildingId" = @building
             FOR UPDATE
            """))
        {
            Add(cmd, "id", requestId);
            Add(cmd, "contract", contractId);
            Add(cmd, "room", sourceRoomId);
            Add(cmd, "building", buildingId);
            if (await cmd.ExecuteScalarAsync() is not Guid)
                throw Denied();
        }
        return await db.RoomChangeRequests
            .SingleOrDefaultAsync(x => x.Id == requestId &&
                x.SourceContractId == contractId &&
                x.SourceRoomId == sourceRoomId &&
                x.BuildingId == buildingId) ?? throw Denied();
    }

    private static async Task<Guid> LockContractRoomAsync(
        BuildingManagementDbContext db, Guid id)
    {
        await using var cmd = Command(db, """
            SELECT "RoomId" FROM "BuildingManagementContracts"
             WHERE "Id" = @id AND "IsDeleted" = FALSE FOR UPDATE
            """);
        Add(cmd, "id", id);
        return await cmd.ExecuteScalarAsync() is Guid value
            ? value : throw Denied();
    }

    private static async Task<Guid> LockRoomBuildingAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT f."BuildingId" FROM "BuildingManagementRooms" r
             JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
             JOIN "Buildings" b ON b."Id" = f."BuildingId"
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
               AND f."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
             FOR SHARE OF f, b
            """);
        Add(cmd, "id", roomId);
        return await cmd.ExecuteScalarAsync() is Guid value
            ? value : throw Denied();
    }

    private static async Task<(Guid Building, RoomStatus Status)> LockRoomStatusAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT f."BuildingId", r."Status"
              FROM "BuildingManagementRooms" r
              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
              JOIN "Buildings" b ON b."Id" = f."BuildingId"
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
               AND f."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
             FOR UPDATE OF r
            """);
        Add(cmd, "id", roomId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw Denied();
        return (reader.GetGuid(0), (RoomStatus)reader.GetInt32(1));
    }

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

    private static void Add(DbCommand command, string name, Guid value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static SliceBRoomChangeRequestRead AsRead(RoomChangeRequest r)
        => new(r.Id, r.SourceContractId, r.SourceRoomId, r.TargetRoomId,
            r.BuildingId, r.RequestedDate, r.Reason, r.Status,
            r.RequestedByUserId, r.CreatedAtUtc,
            r.ReviewedByUserId, r.ReviewedAtUtc, r.ReviewNotes);

    private static AbpAuthorizationException Denied()
        => new("Room change requires isolated staging Contracts.Manage and current building-scoped membership.");
}

public sealed record SliceBRoomChangeSubmitInput(
    Guid TargetRoomId, DateOnly RequestedDate, string? Reason);

public sealed record SliceBRoomChangeReviewInput(string? ReviewNotes);

public sealed record SliceBRoomChangeRequestRead(
    Guid Id, Guid SourceContractId, Guid SourceRoomId, Guid TargetRoomId,
    Guid BuildingId, DateOnly RequestedDate, string Reason,
    RoomChangeRequestStatus Status, Guid RequestedByUserId, DateTime CreatedAtUtc,
    Guid? ReviewedByUserId, DateTime? ReviewedAtUtc, string? ReviewNotes);
