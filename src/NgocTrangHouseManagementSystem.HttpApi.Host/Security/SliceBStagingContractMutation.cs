using System;
using System.Data.Common;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.Security;
using BuildingManagement.Security;
using BuildingManagement.Tenants;
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

    public SliceBStagingContractMutation(
        IDbContextProvider<BuildingManagementDbContext> provider,
        IBuildingCrudAccessPolicy policy,
        IBuildingBusinessResourceScopeReader resources,
        ICurrentUser user,
        SliceAStagingIdentityCommitLease identity,
        IContractAppService contracts, ITenantAppService tenants)
    {
        _provider = provider;
        _policy = policy;
        _resources = resources;
        _user = user;
        _identity = identity;
        _contracts = contracts;
        _tenants = tenants;
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
