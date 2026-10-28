using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Security;
using BuildingManagement.Tenants;
using BuildingManagement.RoomReservations;
using BuildingManagement.Payments;
using BuildingManagement.MoveOuts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Explicit, isolated-staging-only draft contract mutation adapter.
/// NOT a global replacement for the unscoped legacy contract service.
/// Requires BOTH active ABP Contracts.Manage (locked until Building commit)
/// and live building membership (FOR SHARE until Building commit).
/// Room/floor links and the current contract row are also locked in the
/// SAME Building transaction to prevent a cross-building TOCTOU change.
/// </summary>
public sealed class SliceBStagingContractMutation
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly IBuildingCrudAccessPolicy _policy;
    private readonly IBuildingBusinessResourceScopeReader _resources;
    private readonly ICurrentUser _user;
    private readonly SliceAStagingIdentityCommitLease _identity;
    private readonly IContractAppService _contracts;
    private readonly ITenantAppService _tenants;
    private readonly IContractRenewalHoldAppService _holds;
    private readonly IRoomReservationAppService _reservations;
    private readonly IMoveOutRequestAppService _moveOuts;
    private readonly IPaymentAppService _payments;

    public SliceBStagingContractMutation(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        ICurrentUser user,
        SliceAStagingIdentityCommitLease identity,
        IContractAppService contracts, ITenantAppService tenants,
        IContractRenewalHoldAppService holds,
        IRoomReservationAppService reservations,
        IMoveOutRequestAppService moveOuts,
        IPaymentAppService payments)
    {
        _provider = provider;
        _policy = policy;
        _resources = resources;
        _user = user;
        _identity = identity;
        _contracts = contracts;
        _tenants = tenants;
        _holds = holds;
        _reservations = reservations;
        _moveOuts = moveOuts;
        _payments = payments;
    }

    /// <summary>
    /// Reserve only a room from the operator's building for a person
    /// already known in THAT building. Room UPDATE lock serializes
    /// competing reservation and contract checks in the shared UoW.
    /// No global Tenant ID existence disclosure or implicit onboarding.
    /// </summary>
    public async Task<RoomReservationDto> CreateRoomReservationAsync(
        CreateRoomReservationDto input)
    {
        if (input is null || input.RoomId == Guid.Empty ||
            input.TenantId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        var building = await ResolveAsync(
            BuildingBusinessResourceKind.Room, input.RoomId);
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, input.RoomId,
            BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await Fence(db).ExecuteAsync(actor, building, async () =>
        {
            await LockRoomExclusivelyAsync(db, input.RoomId);
            if (await LockRoomBuildingAsync(db, input.RoomId) != building)
                throw Denied();
            await LockKnownTenantInBuildingAsync(db, input.TenantId, building);
            return await _reservations.CreateAsync(input);
        }, () => _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, input.RoomId,
            BuildingBusinessCapability.ManageContract));
    }

    /// <summary>
    /// Cancel a reservation under the resolved Room building only. Lock
    /// Reservation before graph, preserving the original cancellation and
    /// settlement lock order. No client-supplied room/building authority.
    /// No second transaction or direct manual settlement endpoint.
    /// </summary>
    public Task<RoomReservationDto> CancelRoomReservationAsync(
        Guid reservationId, CancelRoomReservationDto input)
    {
        if (reservationId == Guid.Empty || input is null)
            throw Denied();
        return MutateReservationAsync(reservationId,
            () => _reservations.CancelAsync(reservationId, input));
    }

    private async Task<T> MutateReservationAsync<T>(
        Guid reservationId, Func<Task<T>> operation)
    {
        var db = await _provider.GetDbContextAsync();
        var roomId = await db.RoomReservations.AsNoTracking()
            .Where(r => r.Id == reservationId && !r.IsDeleted)
            .Select(r => r.RoomId).SingleOrDefaultAsync();
        if (roomId == Guid.Empty)
            throw Denied();

        var buildingId = await ResolveAsync(
            BuildingBusinessResourceKind.Room, roomId);
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, roomId,
            BuildingBusinessCapability.ManageContract);
        var actor = RequireActor();
        await _identity.AcquireContractManageAsync(actor);

        return await Fence(db).ExecuteAsync(actor, buildingId, async () =>
        {
            // Lock the exact reservation (not just a stale scalar lookup)
            // before its current Room graph or deposit disposition.
            await using var command = Command(db, """
                SELECT r."RoomId"
                  FROM "BuildingManagementRoomReservations" AS r
                 WHERE r."Id" = @id AND r."IsDeleted" = FALSE
                 FOR UPDATE OF r
                """);
            AddId(command, reservationId);
            if (await command.ExecuteScalarAsync() is not Guid lockedRoom ||
                lockedRoom != roomId ||
                await LockRoomBuildingAsync(db, lockedRoom) != buildingId)
                throw Denied();

            return await operation();
        }, () => _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, roomId,
            BuildingBusinessCapability.ManageContract));
    }

    /// <summary>
    /// A new payment is only a Pending record. Bind the caller-supplied
    /// reservation ID to the route and hold its live building authorization
    /// until commit. Never permit a global payment-create API to bypass
    /// contract/room membership.
    /// </summary>
    public Task<PaymentDto> CreateReservationDepositPaymentAsync(
        Guid reservationId, CreateReservationDepositPaymentDto input)
    {
        if (input is null || reservationId == Guid.Empty ||
            input.RoomReservationId != reservationId)
            throw Denied();

        return MutateReservationAsync(reservationId,
            () => _payments.CreateReservationDepositAsync(input));
    }

    /// <summary>
    /// Confirm one existing pending ReservationDeposit payment, not any
    /// arbitrary global Payment/ContractDeposit. The legacy completion
    /// service acquires Contract -> Reservation -> Payment locks; do NOT
    /// prelock the Reservation here (would invert its concurrency order).
    /// Room building is rechecked under a PostgreSQL share lock AFTER
    /// payment completion, before the shared transaction commits.
    /// </summary>
    public async Task<PaymentDto> CompleteReservationDepositPaymentAsync(
        Guid reservationId, Guid paymentId, CompletePaymentDto input)
    {
        if (reservationId == Guid.Empty || paymentId == Guid.Empty || input is null)
            throw Denied();

        var db = await _provider.GetDbContextAsync();
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.Id == paymentId && !p.IsDeleted)
            .Select(p => new { p.RoomReservationId, p.Purpose })
            .SingleOrDefaultAsync();
        if (payment is null ||
            payment.Purpose != PaymentPurpose.ReservationDeposit ||
            payment.RoomReservationId != reservationId)
            throw Denied();

        var roomId = await db.RoomReservations.AsNoTracking()
            .Where(r => r.Id == reservationId && !r.IsDeleted)
            .Select(r => r.RoomId).SingleOrDefaultAsync();
        if (roomId == Guid.Empty)
            throw Denied();

        var buildingId = await ResolveAsync(
            BuildingBusinessResourceKind.Room, roomId);
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, roomId,
            BuildingBusinessCapability.ManageContract);
        var actor = RequireActor();
        await _identity.AcquireContractManageAsync(actor);

        return await Fence(db).ExecuteAsync(actor, buildingId, async () =>
        {
            // Use the original domain path and lock order; it will reject
            // duplicate completion and re-check deposit/room conflicts.
            var result = await _payments.CompleteAsync(paymentId, input);
            if (result.Purpose != PaymentPurpose.ReservationDeposit ||
                result.RoomReservationId != reservationId ||
                !await db.Payments.AnyAsync(p =>
                    p.Id == paymentId && !p.IsDeleted &&
                    p.RoomReservationId == reservationId &&
                    p.Purpose == PaymentPurpose.ReservationDeposit) ||
                !await db.RoomReservations.AnyAsync(r =>
                    r.Id == reservationId && !r.IsDeleted &&
                    r.RoomId == roomId) ||
                await LockRoomBuildingAsync(db, roomId) != buildingId)
                throw Denied();
            return result;
        }, () => _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, roomId,
            BuildingBusinessCapability.ManageContract));
    }

    /// <summary>
    /// Create a notice only for the route's own authorized Active contract.
    /// The body cannot substitute a foreign ContractId. Existing domain
    /// notice-date, open-request and transaction rules remain authoritative.
    /// </summary>
    public Task<MoveOutRequestDto> CreateMoveOutRequestAsync(
        Guid contractId, CreateMoveOutRequestDto input)
    {
        if (input is null || contractId == Guid.Empty ||
            input.ContractId != contractId)
            throw Denied();
        return MutateLinkAsync(contractId,
            (_, _) => _moveOuts.CreateAsync(input));
    }

    /// <summary>
    /// Approving a request only creates the original move-out checklist.
    /// Completion and all room/deposit/contract state transitions remain
    /// unavailable through this controller.
    /// </summary>
    public Task<MoveOutRequestDto> ApproveMoveOutRequestAsync(
        Guid contractId, Guid requestId, ApproveMoveOutRequestDto input)
    {
        if (input is null || contractId == Guid.Empty || requestId == Guid.Empty)
            throw Denied();
        return MutateLinkAsync(contractId, async (db, _) =>
        {
            if (!await db.MoveOutRequests.AnyAsync(request =>
                request.Id == requestId && request.ContractId == contractId &&
                !request.IsDeleted))
                throw Denied();
            return await _moveOuts.ApproveAsync(requestId, input);
        });
    }

    public Task<MoveOutRequestDto> RejectMoveOutRequestAsync(
        Guid contractId, Guid requestId, RejectMoveOutRequestDto input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Reason))
            throw Denied();
        return MutateMoveOutRequestAsync(contractId, requestId,
            () => _moveOuts.RejectAsync(requestId, input));
    }

    public Task<MoveOutRequestDto> CancelMoveOutRequestAsync(
        Guid contractId, Guid requestId)
        => MutateMoveOutRequestAsync(contractId, requestId,
            () => _moveOuts.CancelAsync(requestId));

    /// <summary>
    /// Reject/cancel use the same Contract FOR UPDATE gate as approve.
    /// Check the route's contract against the persisted request while
    /// the parent contract is locked, before calling the original service.
    /// </summary>
    private Task<MoveOutRequestDto> MutateMoveOutRequestAsync(
        Guid contractId, Guid requestId, Func<Task<MoveOutRequestDto>> change)
    {
        if (contractId == Guid.Empty || requestId == Guid.Empty)
            throw Denied();
        return MutateLinkAsync(contractId, async (db, _) =>
        {
            if (!await db.MoveOutRequests.AnyAsync(request =>
                request.Id == requestId && request.ContractId == contractId &&
                !request.IsDeleted))
                throw Denied();
            return await change();
        });
    }

    public async Task<ContractDto> CreateAsync(CreateContractDto input)
    {
        if (input is null || input.RoomId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        var initial = await ResolveAsync(BuildingBusinessResourceKind.Room, input.RoomId);
        await _policy.EnsureResourceAsync(BuildingBusinessResourceKind.Room,
            input.RoomId, BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await Fence(db).ExecuteAsync(actor, initial, async () =>
        {
            if (await LockRoomBuildingAsync(db, input.RoomId) != initial)
                throw Denied();
            return await _contracts.CreateAsync(input);
        }, () => _policy.EnsureResourceAsync(BuildingBusinessResourceKind.Room,
            input.RoomId, BuildingBusinessCapability.ManageContract));
    }

    public async Task<ContractDto> UpdateAsync(Guid id, UpdateContractDto input)
    {
        if (id == Guid.Empty || input is null || input.RoomId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        var sourceBuilding = await ResolveAsync(
            BuildingBusinessResourceKind.Contract, id);
        var targetBuilding = await ResolveAsync(
            BuildingBusinessResourceKind.Room, input.RoomId);

        // A change of building would require two independent membership
        // row locks and a dedicated transfer protocol. For this first write
        // vertical we explicitly forbid cross-building room transfers,
        // including when the actor is a member of both buildings.
        if (sourceBuilding != targetBuilding)
            throw Denied();

        await _policy.EnsureResourceAsync(BuildingBusinessResourceKind.Contract,
            id, BuildingBusinessCapability.ManageContract);
        await _policy.EnsureResourceAsync(BuildingBusinessResourceKind.Room,
            input.RoomId, BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await Fence(db).ExecuteAsync(actor, sourceBuilding, async () =>
        {
            var existingRoomId = await LockContractRoomAsync(db, id);
            var currentBuilding = await LockRoomBuildingAsync(db, existingRoomId);
            if (currentBuilding != sourceBuilding ||
                await LockRoomBuildingAsync(db, input.RoomId) != sourceBuilding)
                throw Denied();
            // The existing app service preserves its Draft-only validation,
            // uniqueness checks, persistence and DTO contract.
            return await _contracts.UpdateAsync(id, input);
        }, async () =>
        {
            await _policy.EnsureResourceAsync(BuildingBusinessResourceKind.Contract,
                id, BuildingBusinessCapability.ManageContract);
            await _policy.EnsureResourceAsync(BuildingBusinessResourceKind.Room,
                input.RoomId, BuildingBusinessCapability.ManageContract);
        });
    }

    /// <summary>
    /// Signing/activating a contract changes the committed occupancy state.
    /// Membership+Identity authority are held to commit, and the room row
    /// is locked EXCLUSIVELY before conflict checks so two different draft
    /// contracts on the same room cannot both pass overlap preflight.
    /// Original domain service still enforces tenant, period, room status
    /// and lifecycle validation.
    /// </summary>
    public Task<ContractDto> SignAsync(Guid contractId)
        => TransitionAsync(contractId, activate: false);

    public Task<ContractDto> ActivateAsync(Guid contractId)
        => TransitionAsync(contractId, activate: true);

    private async Task<ContractDto> TransitionAsync(Guid contractId, bool activate)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        var buildingId = await ResolveAsync(
            BuildingBusinessResourceKind.Contract, contractId);
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await Fence(db).ExecuteAsync(actor, buildingId, async () =>
        {
            var currentRoom = await LockContractRoomAsync(db, contractId);
            // Serialize all signing and activation candidates for this room.
            // A second transition must re-read overlap/status after first
            // transaction commits. Acquiring this exclusive lock before the
            // shared floor/building graph also avoids a SHARE→UPDATE upgrade.
            await LockRoomExclusivelyAsync(db, currentRoom);
            if (await LockRoomBuildingAsync(db, currentRoom) != buildingId)
                throw Denied();
            return activate
                ? await _contracts.ActivateAsync(contractId)
                : await _contracts.SignAsync(contractId);
        }, () => _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract));
    }

    private static async Task LockRoomExclusivelyAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT r."Id" FROM "BuildingManagementRooms" AS r
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
             FOR UPDATE OF r
            """);
        AddId(cmd, roomId);
        if (await cmd.ExecuteScalarAsync() is not Guid)
            throw Denied();
    }

    /// <summary>
    /// A renewal hold MUST use the same contract-row UPDATE gate as
    /// reservation deposit settlement and Contract.Renew. A caller-supplied
    /// CurrentContractId alone is not authority; scope it via the graph.
    /// </summary>
    public Task<ContractRenewalHoldDto> CreateRenewalHoldAsync(
        Guid currentContractId, string? notes)
        => MutateLinkAsync(currentContractId, async (db, _) =>
        {
            return await _holds.CreateAsync(new CreateContractRenewalHoldDto
            {
                CurrentContractId = currentContractId,
                Notes = notes
            });
        });

    /// <summary>
    /// Cancel an existing hold ONLY through its current contract's scope.
    /// The hold's current contract ID is resolved server-side, then both
    /// the contract and the matching hold are locked in the same UoW.
    /// </summary>
    public async Task<ContractRenewalHoldDto> CancelRenewalHoldAsync(Guid holdId)
    {
        if (holdId == Guid.Empty)
            throw Denied();
        var db = await _provider.GetDbContextAsync();
        var current = await db.ContractRenewalHolds.AsNoTracking()
            .Where(h => h.Id == holdId && !h.IsDeleted)
            .Select(h => h.CurrentContractId).SingleOrDefaultAsync();
        if (current == Guid.Empty)
            throw Denied();
        return await MutateLinkAsync(current, async (lockedDb, _) =>
        {
            await using var command = Command(lockedDb, """
                SELECT h."Id"
                  FROM "BuildingManagementContractRenewalHolds" AS h
                 WHERE h."Id" = @hold AND h."CurrentContractId" = @contract
                   AND h."IsDeleted" = FALSE
                 FOR UPDATE OF h
                """);
            AddGuid(command, "hold", holdId);
            AddGuid(command, "contract", current);
            if (await command.ExecuteScalarAsync() is not Guid)
                throw Denied();
            return await _holds.CancelAsync(holdId);
        });
    }

    /// <summary>
    /// Renew must serialize against both the existing Active Contract and
    /// any conflicting signed-contract or reservation status for its room.
    /// Original domain service owns hold consumption, reservation deposit
    /// checks, tenant copying and atomic pending-reservation cancellation.
    /// </summary>
    public Task<ContractDto> RenewAsync(Guid currentContractId, RenewContractDto input)
    {
        if (input is null)
            throw Denied();
        return MutateRenewalWithRoomLockAsync(currentContractId, input);
    }

    private async Task<ContractDto> MutateRenewalWithRoomLockAsync(
        Guid currentContractId, RenewContractDto input)
    {
        if (currentContractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        var buildingId = await ResolveAsync(
            BuildingBusinessResourceKind.Contract, currentContractId);
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, currentContractId,
            BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await Fence(db).ExecuteAsync(actor, buildingId, async () =>
        {
            // Lock order must be Contract -> Room UPDATE -> graph SHARE,
            // never first acquire Room SHARE then upgrade to UPDATE.
            var roomId = await LockContractRoomAsync(db, currentContractId);
            await LockRoomExclusivelyAsync(db, roomId);
            if (await LockRoomBuildingAsync(db, roomId) != buildingId)
                throw Denied();
            return await _contracts.RenewAsync(currentContractId, input);
        }, () => _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, currentContractId,
            BuildingBusinessCapability.ManageContract));
    }

    /// <summary>
    /// The only new-tenant onboarding route: atomically create a new global
    /// profile AND bind it to a Draft contract of an authorized building.
    /// Never expose generic TenantAppService CRUD or create orphan profiles.
    /// Duplicate citizen ID/phone errors must not reveal which global
    /// person record already exists in another building.
    /// </summary>
    public Task<SliceBScopedContractTenantLink> RegisterTenantAsync(
        Guid contractId, SliceBNewContractTenantInput input)
    {
        if (input is null || input.Tenant is null ||
            !Enum.IsDefined(input.Role))
            throw Denied();
        return MutateLinkAsync(contractId, async (db, _) =>
        {
            // Contract row is already FOR UPDATE locked by MutateLinkAsync.
            // Reject a non-Draft before inserting private tenant data.
            if (!await db.Contracts.AnyAsync(c =>
                c.Id == contractId && !c.IsDeleted &&
                c.Status == ContractStatus.Draft))
                throw Denied();

            TenantDto tenant;
            try
            {
                tenant = await _tenants.CreateAsync(input.Tenant);
            }
            catch (BusinessException)
            {
                // Never echo globally unique phone/citizen ID back to an
                // otherwise authorized operator in a different building.
                // ABP completes the request with a generic denial and
                // rolls back the enclosing Building UnitOfWork.
                throw Denied();
            }
            await _contracts.AddTenantAsync(contractId,
                new AddContractTenantDto
                {
                    TenantId = tenant.Id,
                    Role = input.Role
                });
            return new SliceBScopedContractTenantLink(tenant.Id, input.Role);
        });
    }

    /// <summary>
    /// A tenant ID is a GLOBAL person record, not a building-owned record.
    /// Add only when the existing, live relationship proves provenance
    /// inside the SAME building. First-time tenant onboarding is separate.
    /// </summary>
    public Task<ContractTenantDto> AddTenantAsync(Guid contractId, AddContractTenantDto input)
    {
        if (input is null || input.TenantId == Guid.Empty)
            throw Denied();
        return MutateLinkAsync(contractId, async (db, buildingId) =>
        {
            await LockKnownTenantInBuildingAsync(db, input.TenantId, buildingId);
            return await _contracts.AddTenantAsync(contractId, input);
        });
    }

    public Task<ContractTenantDto> UpdateTenantRoleAsync(
        Guid contractId, Guid tenantId, UpdateContractTenantRoleDto input)
    {
        if (input is null || tenantId == Guid.Empty)
            throw Denied();
        return MutateLinkAsync(contractId, async (db, _) =>
        {
            await LockCurrentTenantLinkAsync(db, contractId, tenantId);
            return await _contracts.UpdateTenantRoleAsync(contractId, tenantId, input);
        });
    }

    public Task<bool> RemoveTenantAsync(Guid contractId, Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw Denied();
        return MutateLinkAsync(contractId, async (db, _) =>
        {
            await LockCurrentTenantLinkAsync(db, contractId, tenantId);
            await _contracts.RemoveTenantAsync(contractId, tenantId);
            return true;
        });
    }

    private async Task<T> MutateLinkAsync<T>(
        Guid contractId, Func<BuildingManagementDbContext, Guid, Task<T>> mutation)
    {
        if (contractId == Guid.Empty)
            throw Denied();
        var actor = RequireActor();
        var buildingId = await ResolveAsync(
            BuildingBusinessResourceKind.Contract, contractId);
        await _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract);
        await _identity.AcquireContractManageAsync(actor);
        var db = await _provider.GetDbContextAsync();
        return await Fence(db).ExecuteAsync(actor, buildingId, async () =>
        {
            // Lock the contract first. If a room transfer or state change
            // committed before this lock, reject rather than using a stale
            // resource-to-building check.
            var currentRoom = await LockContractRoomAsync(db, contractId);
            if (await LockRoomBuildingAsync(db, currentRoom) != buildingId)
                throw Denied();
            return await mutation(db, buildingId);
        }, () => _policy.EnsureResourceAsync(
            BuildingBusinessResourceKind.Contract, contractId,
            BuildingBusinessCapability.ManageContract));
    }

    private static async Task LockCurrentTenantLinkAsync(
        BuildingManagementDbContext db, Guid contractId, Guid tenantId)
    {
        await using var command = Command(db, """
            SELECT ct."Id"
              FROM "BuildingManagementContractTenants" AS ct
             WHERE ct."ContractId" = @contract AND ct."TenantId" = @tenant
               AND ct."IsDeleted" = FALSE
             FOR UPDATE OF ct
            """);
        AddGuid(command, "contract", contractId);
        AddGuid(command, "tenant", tenantId);
        if (await command.ExecuteScalarAsync() is not Guid)
            throw Denied();
    }

    private static async Task LockKnownTenantInBuildingAsync(
        BuildingManagementDbContext db, Guid tenantId, Guid buildingId)
    {
        await using var command = Command(db, """
            SELECT ct."Id"
              FROM "BuildingManagementContractTenants" AS ct
              JOIN "BuildingManagementContracts" AS c
                ON c."Id" = ct."ContractId"
              JOIN "BuildingManagementRooms" AS r ON r."Id" = c."RoomId"
              JOIN "BuildingManagementFloors" AS f ON f."Id" = r."FloorId"
              JOIN "Buildings" AS b ON b."Id" = f."BuildingId"
              JOIN "BuildingManagementTenants" AS t ON t."Id" = ct."TenantId"
             WHERE ct."TenantId" = @tenant AND f."BuildingId" = @building
               AND ct."IsDeleted" = FALSE AND c."IsDeleted" = FALSE
               AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
               AND b."IsDeleted" = FALSE AND t."IsDeleted" = FALSE
             ORDER BY ct."Id"
             LIMIT 1 FOR SHARE OF ct, c, r, f, b, t
            """);
        AddGuid(command, "tenant", tenantId);
        AddGuid(command, "building", buildingId);
        if (await command.ExecuteScalarAsync() is not Guid)
            throw Denied();
    }

    private EfBuildingScopedWriteFenceCandidate Fence(BuildingManagementDbContext db)
        => new(db, TimeProvider.System);

    private Guid RequireActor()
    {
        if (!_user.IsAuthenticated || _user.Id is not Guid actor ||
            actor == Guid.Empty)
            throw Denied();
        return actor;
    }

    private async Task<Guid> ResolveAsync(
        BuildingBusinessResourceKind kind, Guid id)
    {
        var result = await _resources.ReadAsync(kind, id);
        if (result is null || result.ResourceId != id ||
            result.BuildingId == Guid.Empty)
            throw Denied();
        return result.BuildingId;
    }

    private static async Task<Guid> LockContractRoomAsync(
        BuildingManagementDbContext db, Guid contractId)
    {
        await using var cmd = Command(db, """
            SELECT c."RoomId" FROM "BuildingManagementContracts" AS c
             WHERE c."Id" = @id AND c."IsDeleted" = FALSE
             FOR UPDATE OF c
            """);
        AddId(cmd, contractId);
        return await cmd.ExecuteScalarAsync() is Guid roomId && roomId != Guid.Empty
            ? roomId : throw Denied();
    }

    private static async Task<Guid> LockRoomBuildingAsync(
        BuildingManagementDbContext db, Guid roomId)
    {
        await using var cmd = Command(db, """
            SELECT f."BuildingId"
              FROM "BuildingManagementRooms" AS r
              JOIN "BuildingManagementFloors" AS f ON f."Id" = r."FloorId"
              JOIN "Buildings" AS b ON b."Id" = f."BuildingId"
             WHERE r."Id" = @id AND r."IsDeleted" = FALSE
               AND f."IsDeleted" = FALSE AND b."IsDeleted" = FALSE
             FOR SHARE OF r, f, b
            """);
        AddId(cmd, roomId);
        return await cmd.ExecuteScalarAsync() is Guid buildingId && buildingId != Guid.Empty
            ? buildingId : throw Denied();
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

    private static void AddId(DbCommand command, Guid id)
        => AddGuid(command, "id", id);

    private static void AddGuid(DbCommand command, string name, Guid id)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = id;
        command.Parameters.Add(parameter);
    }

    private static AbpAuthorizationException Denied()
        => new("Draft contract change requires a current building-scoped Contracts.Manage grant.");
}

public sealed class SliceBNewContractTenantInput
{
    public CreateTenantDto Tenant { get; set; } = default!;
    public ContractTenantRole Role { get; set; }
}
