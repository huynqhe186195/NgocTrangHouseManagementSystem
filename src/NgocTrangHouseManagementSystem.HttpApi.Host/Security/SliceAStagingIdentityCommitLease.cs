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

    public async Task AcquireAsync(
        Guid trustedUserId, BuildingBusinessCapability capability)
    {
        if (trustedUserId == Guid.Empty ||
            !_config.GetValue<bool>("BuildingManagement:SliceAStaging:Enabled") ||
            _config["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES")
            throw Denied();
        var permission = capability switch
        {
            BuildingBusinessCapability.ManageBuilding =>
                BuildingManagementPermissions.Buildings.Manage,
            BuildingBusinessCapability.ManageFloor =>
                BuildingManagementPermissions.Floors.Manage,
            BuildingBusinessCapability.ManageRoom =>
                BuildingManagementPermissions.Rooms.Manage,
            _ => throw Denied()
        };
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
            // Prefer a persisted direct user grant. If absent, verify
            // one actual persisted ABP role permission: user-role mapping,
            // live role row and its role-provider grant are all locked in
            // the same Identity transaction until Building UoW completion.
            // Locking role name also serializes role rename/soft-delete.
            var direct = false;
            await using (var grant = new NpgsqlCommand(
                """
                SELECT "Id" FROM "AbpPermissionGrants"
                 WHERE "ProviderName" = 'U' AND "ProviderKey" = @user
                   AND "Name" = @permission
                 FOR SHARE
                """, connection, transaction))
            {
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
                      JOIN "AbpRoles" AS r ON r."Id" = ur."RoleId"
                      JOIN "AbpPermissionGrants" AS p
                        ON p."ProviderName" = 'R'
                       AND p."ProviderKey" = r."Name"
                       AND p."Name" = @permission
                     WHERE ur."UserId" = @actor
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
