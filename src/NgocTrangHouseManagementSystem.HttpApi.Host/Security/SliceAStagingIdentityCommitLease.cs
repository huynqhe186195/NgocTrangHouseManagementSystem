using System;
using System.Data;
using System.Threading.Tasks;
using BuildingManagement.Permissions;
using BuildingManagement.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Opt-in isolated PostgreSQL staging only. A SECOND connection on the
/// Identity database holds row locks for the ENTIRE Building UoW lifetime:
/// post-commit callbacks release the locks only AFTER the Building commit.
/// Native ABP account UPDATE, direct-user permission UPDATE/DELETE, and
/// role-grant/role-membership UPDATE/DELETE on the selected role must wait
/// until the Building commit. A role-only grant is allowed ONLY when actual
/// ABP Identity role, membership, and permission rows can be locked.
/// This is NOT XA/2PC; new/alternate role grants, role caches or unrelated
/// privileged SQL are not made atomic by this lease.
/// </summary>
public sealed class SliceAStagingIdentityCommitLease
{
    private readonly IConfiguration _config;
    private readonly IUnitOfWorkManager _uowManager;

    public SliceAStagingIdentityCommitLease(
        IConfiguration config, IUnitOfWorkManager uowManager)
    {
        _config = config;
        _uowManager = uowManager;
    }

    /// <summary>
    /// The ONLY founding root must retain a persisted BootstrapOwner grant
    /// and an enabled Identity account until the first Building + grant
    /// commit. A stale permission cache or authenticated root claim alone
    /// is insufficient.
    /// </summary>
    public Task AcquireBootstrapAsync(Guid trustedOwnerUserId)
        => AcquirePermissionAsync(trustedOwnerUserId,
            BuildingManagementPermissions.Buildings.BootstrapOwner, null);

    /// <summary>
    /// One recovery sign-off holds the independently authenticated signer's
    /// current persisted permission, active recipient, and inactive founder
    /// account state until the sign-off audit row commits. A deleted or
    /// missing founder without a lockable record fails closed.
    /// </summary>
    public Task AcquireOwnerRecoverySignoffAsync(
        Guid authenticatedSignerId, bool isBootstrapRoot,
        Guid originalFounderId, Guid recipientId)
    {
        if (originalFounderId == Guid.Empty ||
            recipientId == Guid.Empty ||
            originalFounderId == authenticatedSignerId ||
            originalFounderId == recipientId)
            throw Denied();
        return AcquirePermissionAsync(authenticatedSignerId,
            isBootstrapRoot
                ? BuildingManagementPermissions.Buildings.BootstrapOwner
                : BuildingManagementPermissions.Buildings.CoSignOwnerRecovery,
            recipientId, originalFounderId);
    }

    /// <summary>
    /// Recovery commit requires BOTH persisted Identity permissions and
    /// currently active independent signers. The recipient must be active
    /// and the historical founder inactive. All relevant Identity rows
    /// stay locked through Building grant + sign-off consumption commit.
    /// This is a coordinated lease, NOT XA/2PC.
    /// </summary>
    public async Task AcquireOwnerRecoveryCommitAsync(
        Guid authenticatedRootId, Guid coSignerId,
        Guid originalFounderId, Guid recipientId)
    {
        if (authenticatedRootId == Guid.Empty || coSignerId == Guid.Empty ||
            recipientId == Guid.Empty || originalFounderId == Guid.Empty ||
            authenticatedRootId == coSignerId ||
            authenticatedRootId == recipientId ||
            coSignerId == recipientId ||
            originalFounderId == authenticatedRootId ||
            originalFounderId == coSignerId ||
            originalFounderId == recipientId)
            throw Denied();

        await AcquirePermissionAsync(authenticatedRootId,
            BuildingManagementPermissions.Buildings.BootstrapOwner,
            recipientId, originalFounderId);
        await AcquirePermissionAsync(coSignerId,
            BuildingManagementPermissions.Buildings.CoSignOwnerRecovery,
            null, originalFounderId);
    }

    public Task AcquireAsync(
        Guid trustedUserId, BuildingBusinessCapability capability)
        => AcquirePermissionAsync(trustedUserId, capability switch
        {
            BuildingBusinessCapability.ManageBuilding =>
                BuildingManagementPermissions.Buildings.Manage,
            BuildingBusinessCapability.ManageFloor =>
                BuildingManagementPermissions.Floors.Manage,
            BuildingBusinessCapability.ManageRoom =>
                BuildingManagementPermissions.Rooms.Manage,
            _ => throw Denied()
        }, null);

    /// <summary>
    /// Delegation requires its OWN ABP permission, not Buildings.Manage.
    /// Assign also holds the target account row until the Building grant
    /// insert commits. Revocation deliberately allows disabled recipients.
    /// </summary>
    public Task AcquireDelegationAsync(
        Guid trustedIssuerId, Guid recipientUserId, BuildingOperatorChangeKind change)
    {
        if (recipientUserId == Guid.Empty ||
            recipientUserId == trustedIssuerId)
            throw Denied();
        return AcquirePermissionAsync(trustedIssuerId, change switch
        {
            BuildingOperatorChangeKind.Assign =>
                BuildingManagementPermissions.Buildings.AssignOperators,
            BuildingOperatorChangeKind.Revoke =>
                BuildingManagementPermissions.Buildings.RevokeOperators,
            _ => throw Denied()
        }, change == BuildingOperatorChangeKind.Assign ? recipientUserId : null);
    }

    private async Task AcquirePermissionAsync(
        Guid trustedUserId, string permission, Guid? activeRecipientId,
        Guid? inactiveFounderId = null)
    {
        if (trustedUserId == Guid.Empty ||
            !_config.GetValue<bool>("BuildingManagement:SliceAStaging:Enabled") ||
            _config["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES")
            throw Denied();
        var cs = _config.GetConnectionString("Default");
        var db = new NpgsqlConnectionStringBuilder(cs);
        if (!db.Database.StartsWith(
                "bm_slicea_staging_", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(db.Host))
            throw Denied();

        var uow = _uowManager.Current ?? throw Denied();
        if (uow.IsDisposed || uow.IsCompleted ||
            uow.Options.IsTransactional != true)
            throw Denied();

        var connection = new NpgsqlConnection(cs);
        NpgsqlTransaction? transaction = null;
        var attached = false;
        try
        {
            await connection.OpenAsync();
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);
            await using (var account = new NpgsqlCommand(
                """
                SELECT "Id" FROM "AbpUsers"
                 WHERE "Id" = @id AND "IsDeleted" = FALSE
                   AND "IsActive" = TRUE
                   AND ("LockoutEnabled" = FALSE OR "LockoutEnd" IS NULL
                        OR "LockoutEnd" <= NOW())
                 FOR SHARE
                """, connection, transaction))
            {
                account.Parameters.AddWithValue("id", trustedUserId);
                if (await account.ExecuteScalarAsync() is not Guid)
                    throw Denied();
            }
            if (activeRecipientId.HasValue)
            {
                // Lock recipient account through grant issuance so a user
                // cannot be disabled between assignment preflight/INSERT.
                await using var recipient = new NpgsqlCommand(
                    """
                    SELECT "Id" FROM "AbpUsers"
                     WHERE "Id" = @id AND "IsDeleted" = FALSE
                       AND "IsActive" = TRUE
                       AND ("LockoutEnabled" = FALSE OR "LockoutEnd" IS NULL
                            OR "LockoutEnd" <= NOW())
                     FOR SHARE
                    """, connection, transaction);
                recipient.Parameters.AddWithValue("id", activeRecipientId.Value);
                if (await recipient.ExecuteScalarAsync() is not Guid)
                    throw Denied();
            }
            if (inactiveFounderId.HasValue)
            {
                // Prevent a founder reactivation between sign-off preflight
                // and durable Building audit commit. Missing rows are denied.
                await using var founder = new NpgsqlCommand(
                    """
                    SELECT "Id" FROM "AbpUsers"
                     WHERE "Id" = @id
                       AND ("IsDeleted" = TRUE OR "IsActive" = FALSE
                            OR ("LockoutEnabled" = TRUE
                                AND "LockoutEnd" > NOW()))
                     FOR SHARE
                    """, connection, transaction);
                founder.Parameters.AddWithValue("id", inactiveFounderId.Value);
                if (await founder.ExecuteScalarAsync() is not Guid)
                    throw Denied();
            }
            // Prefer a persisted direct user grant. If absent, verify
            // one actual persisted ABP role permission: user-role mapping,
            // live role row and its role-provider grant are all locked in
            // the same Identity transaction until Building UoW completion.
            // Locking role name also serializes role rename/soft-delete.
            var direct = false;
            await using (var grant = new NpgsqlCommand(
                """
                SELECT p."Id" FROM "AbpPermissionGrants" AS p
                  JOIN "AbpUsers" AS u ON u."Id" = @actor
                 WHERE p."ProviderName" = 'U' AND p."ProviderKey" = @user
                   AND p."Name" = @permission
                   AND p."TenantId" IS NOT DISTINCT FROM u."TenantId"
                 FOR SHARE OF p
                """, connection, transaction))
            {
                grant.Parameters.AddWithValue("actor", trustedUserId);
                grant.Parameters.AddWithValue("user",
                    trustedUserId.ToString("D"));
                grant.Parameters.AddWithValue("permission", permission);
                direct = await grant.ExecuteScalarAsync() is Guid;
            }
            if (!direct)
            {
                await using var roleGrant = new NpgsqlCommand(
                    """
                    SELECT p."Id"
                      FROM "AbpUserRoles" AS ur
                      JOIN "AbpUsers" AS u ON u."Id" = ur."UserId"
                      JOIN "AbpRoles" AS r ON r."Id" = ur."RoleId"
                      JOIN "AbpPermissionGrants" AS p
                        ON p."ProviderName" = 'R'
                       AND p."ProviderKey" = r."Name"
                       AND p."Name" = @permission
                       AND p."TenantId" IS NOT DISTINCT FROM r."TenantId"
                     WHERE ur."UserId" = @actor
                       AND r."TenantId" IS NOT DISTINCT FROM u."TenantId"
                     ORDER BY r."Id"
                     LIMIT 1
                     FOR SHARE OF ur, r, p
                    """, connection, transaction);
                roleGrant.Parameters.AddWithValue("actor", trustedUserId);
                roleGrant.Parameters.AddWithValue("permission", permission);
                if (await roleGrant.ExecuteScalarAsync() is not Guid)
                    throw Denied();
            }

            var lease = transaction;
            // UoW only invokes OnCompleted AFTER its Building transaction
            // commits. Failed/disposed paths close connections and rollback.
            uow.OnCompleted(async () =>
            {
                await lease.RollbackAsync(); // releases read locks; no writes
                await connection.DisposeAsync();
            });
            uow.Failed += (_, _) => connection.Dispose();
            uow.Disposed += (_, _) => connection.Dispose();
            attached = true;
        }
        finally
        {
            if (!attached)
            {
                if (transaction is not null)
                    await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    private static AbpAuthorizationException Denied()
        => new("Isolated staging write requires a commit-bound Identity lease.");
}
