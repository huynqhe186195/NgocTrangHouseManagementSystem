using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Volo.Abp.Authorization;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// D04-B ISOLATED ACCEPTANCE CANDIDATE, NOT runtime registered.
/// This is NOT an AllowAll authority. Before a NEW financial commit, it
/// locks both REAL ABP Identity accounts and distinct direct-user financial
/// permissions in the Identity database, plus both effective Building grants
/// in the SAME SERIALIZABLE transaction that posts the Payment and journal.
/// The Identity locks stay alive until the writer's Building commit.
///
/// Intentionally restrict to the three purpose-named disposable PostgreSQL
/// databases from the local acceptance runner. Production is always denied.
/// A separately reviewed activation is required to expand to live staging.
/// </summary>
public sealed class PayOsD04StagingDualAuthority : IPayOsD04CurrentDualAuthority
{
    private readonly BuildingManagementDbContext _buildingDb;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _host;

    public PayOsD04StagingDualAuthority(
        BuildingManagementDbContext buildingDb,
        IConfiguration config,
        IHostEnvironment host)
    {
        _buildingDb = buildingDb ?? throw new ArgumentNullException(nameof(buildingDb));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<IAsyncDisposable> AcquireCommitLeaseAsync(
        Guid buildingId, Guid paymentId, Guid bankReviewerId,
        Guid ownerApproverId, CancellationToken cancellationToken)
    {
        if (buildingId == Guid.Empty || paymentId == Guid.Empty ||
            bankReviewerId == Guid.Empty || ownerApproverId == Guid.Empty ||
            bankReviewerId == ownerApproverId ||
            !_host.IsStaging() ||
            !_config.GetValue<bool>("BuildingManagement:PayOsD04B:Enabled") ||
            _config["BuildingManagement:PayOsD04B:Activation"] !=
                "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY")
            throw Denied();

        // Never let client-provided DB names influence this critical boundary.
        // A config flag alone never enables financial posting on live data.
        var identityString = _config.GetConnectionString("Default");
        var buildingString = _config.GetConnectionString("BuildingManagement");
        if (!IsExactDisposable(identityString, "payos_d04_identity_test") ||
            !IsExactDisposable(buildingString, "payos_d04_legacy_test") ||
            !_buildingDb.Database.IsNpgsql() ||
            _buildingDb.Database.CurrentTransaction is null ||
            _buildingDb.Database.CurrentTransaction.GetDbTransaction()
                .IsolationLevel != IsolationLevel.Serializable)
            throw Denied();

        var dbActual = new NpgsqlConnectionStringBuilder(
            _buildingDb.Database.GetDbConnection().ConnectionString);
        // Npgsql may hide the password after opening a connection.
        // Validate the actual database LOCATION, while the configured
        // connection was already checked for the exact disposable secret.
        if (!IsExactDisposableLocation(dbActual, "payos_d04_legacy_test"))
            throw Denied();

        // Identity is a SEPARATE DB. A ReadCommitted transaction with SHARE
        // row locks serializes account disable/lockout and direct grant revoke
        // against the Building commit. No role claims, JWT cache or DTO flags.
        var identity = new NpgsqlConnection(identityString);
        NpgsqlTransaction? identityTx = null;
        try
        {
            await identity.OpenAsync(cancellationToken);
            identityTx = await identity.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            // Order locks by UUID to avoid deadlocks with independent approvers.
            var first = bankReviewerId.CompareTo(ownerApproverId) < 0
                ? bankReviewerId : ownerApproverId;
            var second = first == bankReviewerId ? ownerApproverId : bankReviewerId;
            await LockActiveAccountAsync(identity, identityTx, first, cancellationToken);
            await LockActiveAccountAsync(identity, identityTx, second, cancellationToken);

            await LockDirectGrantAsync(identity, identityTx, bankReviewerId,
                BuildingManagementPermissions.Payments.ReviewBankEvidence, cancellationToken);
            await LockDirectGrantAsync(identity, identityTx, ownerApproverId,
                BuildingManagementPermissions.Payments.ApproveD04Posting, cancellationToken);
            // Only a persisted Owner role membership qualifies for approval.
            // Dedicated user permission by itself does NOT make staff an Owner.
            await LockOwnerRoleAsync(identity, identityTx, ownerApproverId,
                cancellationToken);

            // Hold live grant rows in the writer's existing SERIALIZABLE
            // Building transaction; revoke/expire and building delete race
            // with this FOR SHARE. Never use a second Building connection.
            await LockBuildingGrantsAsync(buildingId, first, second,
                cancellationToken);

            return new IdentityLease(identity, identityTx);
        }
        catch
        {
            if (identityTx is not null)
                await identityTx.DisposeAsync();
            await identity.DisposeAsync();
            throw;
        }
    }


    /// <summary>
    /// Single reviewer authority for appending an independently inspected
    /// statement attestation. The caller must bind reviewerId to ICurrentUser,
    /// not a DTO. Its locks stay alive until the INSERT transaction commits.
    /// </summary>
    public Task<IAsyncDisposable> AcquireReviewLeaseAsync(
        Guid buildingId, Guid reviewerId, CancellationToken cancellationToken)
        => AcquireSingleActorLeaseAsync(buildingId, reviewerId,
            BuildingManagementPermissions.Payments.ReviewBankEvidence,
            requireOwnerRole: false, ct: cancellationToken);

    /// <summary>
    /// Single Owner authority for submitting a separate approval. The
    /// subsequent financial writer STILL validates BOTH actors afresh.
    /// </summary>
    public Task<IAsyncDisposable> AcquireOwnerApprovalLeaseAsync(
        Guid buildingId, Guid ownerId, CancellationToken cancellationToken)
        => AcquireSingleActorLeaseAsync(buildingId, ownerId,
            BuildingManagementPermissions.Payments.ApproveD04Posting,
            requireOwnerRole: true, ct: cancellationToken);

    private async Task<IAsyncDisposable> AcquireSingleActorLeaseAsync(
        Guid buildingId, Guid actorId, string permission,
        bool requireOwnerRole, CancellationToken ct)
    {
        if (buildingId == Guid.Empty || actorId == Guid.Empty ||
            !_host.IsStaging() ||
            !_config.GetValue<bool>("BuildingManagement:PayOsD04B:Enabled") ||
            _config["BuildingManagement:PayOsD04B:Activation"] !=
                "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY" ||
            !IsExactDisposable(_config.GetConnectionString("Default"),
                "payos_d04_identity_test") ||
            !IsExactDisposable(_config.GetConnectionString("BuildingManagement"),
                "payos_d04_legacy_test") ||
            !_buildingDb.Database.IsNpgsql() ||
            _buildingDb.Database.CurrentTransaction is null ||
            _buildingDb.Database.CurrentTransaction.GetDbTransaction()
                .IsolationLevel != IsolationLevel.Serializable)
            throw Denied();

        var actual = new NpgsqlConnectionStringBuilder(
            _buildingDb.Database.GetDbConnection().ConnectionString);
        if (!IsExactDisposableLocation(actual, "payos_d04_legacy_test"))
            throw Denied();

        var identity = new NpgsqlConnection(
            _config.GetConnectionString("Default"));
        NpgsqlTransaction? tx = null;
        try
        {
            await identity.OpenAsync(ct);
            tx = await identity.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct);
            await LockActiveAccountAsync(identity, tx, actorId, ct);
            await LockDirectGrantAsync(identity, tx, actorId, permission, ct);
            if (requireOwnerRole)
                await LockOwnerRoleAsync(identity, tx, actorId, ct);
            await LockBuildingGrantAsync(buildingId, actorId, ct);
            return new IdentityLease(identity, tx);
        }
        catch
        {
            if (tx is not null) await tx.DisposeAsync();
            await identity.DisposeAsync();
            throw;
        }
    }

    private async Task LockBuildingGrantAsync(
        Guid buildingId, Guid actorId, CancellationToken ct)
    {
        var tx = _buildingDb.Database.CurrentTransaction ?? throw Denied();
        var conn = (NpgsqlConnection)_buildingDb.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            SELECT g."Id"
              FROM "BuildingManagementOperatorBuildingGrants" g
              JOIN "Buildings" b ON b."Id"=g."BuildingId"
             WHERE g."BuildingId"=@building AND g."OperatorUserId"=@actor
               AND g."RevokedAtUtc" IS NULL
               AND g."GrantedAtUtc"<=NOW()
               AND (g."ExpiresAtUtc" IS NULL OR g."ExpiresAtUtc">NOW())
               AND b."IsDeleted"=FALSE
             FOR SHARE OF g,b
            """, conn, (NpgsqlTransaction)tx.GetDbTransaction());
        cmd.Parameters.AddWithValue("building", buildingId);
        cmd.Parameters.AddWithValue("actor", actorId);
        if (await cmd.ExecuteScalarAsync(ct) is not Guid)
            throw Denied();
    }

    private static bool IsExactDisposable(string? source, string name)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try { return IsExactDisposable(new NpgsqlConnectionStringBuilder(source), name); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsExactDisposable(NpgsqlConnectionStringBuilder cs, string name)
        => IsExactDisposableLocation(cs, name) && cs.Password == "disposable-only";

    private static bool IsExactDisposableLocation(NpgsqlConnectionStringBuilder cs, string name)
        => cs.Host is ("127.0.0.1" or "localhost") &&
           cs.Port == 15432 &&
           string.Equals(cs.Database, name, StringComparison.Ordinal) &&
           string.Equals(cs.Username, "postgres", StringComparison.Ordinal);

    private static async Task LockActiveAccountAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid actor,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT "Id" FROM "AbpUsers"
             WHERE "Id"=@actor AND "IsDeleted"=FALSE AND "IsActive"=TRUE
               AND ("LockoutEnabled"=FALSE OR "LockoutEnd" IS NULL
                    OR "LockoutEnd"<=NOW())
             FOR SHARE
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is not Guid)
            throw Denied();
    }

    private static async Task LockDirectGrantAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid actor,
        string permission, CancellationToken cancellationToken)
    {
        // Direct 'U' grant only for this disposable acceptance pilot.
        // No broad Root/Admin/ManageContract permission fallback.
        await using var cmd = new NpgsqlCommand("""
            SELECT p."Id"
              FROM "AbpPermissionGrants" p
              JOIN "AbpUsers" u ON u."Id"=@actor
             WHERE p."ProviderName"='U'
               AND p."ProviderKey"=@key
               AND p."Name"=@permission
               AND p."TenantId" IS NOT DISTINCT FROM u."TenantId"
             FOR SHARE OF p
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("key", actor.ToString("D"));
        cmd.Parameters.AddWithValue("permission", permission);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is not Guid)
            throw Denied();
    }

    private static async Task LockOwnerRoleAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid owner,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT ur."UserId"
              FROM "AbpUserRoles" ur
              JOIN "AbpUsers" u ON u."Id"=ur."UserId"
              JOIN "AbpRoles" r ON r."Id"=ur."RoleId"
             WHERE ur."UserId"=@owner AND r."Name"='Owner'
               AND r."IsDeleted"=FALSE
               AND r."TenantId" IS NOT DISTINCT FROM u."TenantId"
             ORDER BY r."Id"
             LIMIT 1
             FOR SHARE OF ur,r
            """, conn, tx);
        cmd.Parameters.AddWithValue("owner", owner);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is not Guid)
            throw Denied();
    }

    private async Task LockBuildingGrantsAsync(
        Guid buildingId, Guid first, Guid second, CancellationToken cancellationToken)
    {
        var tx = _buildingDb.Database.CurrentTransaction
            ?? throw Denied();
        var conn = _buildingDb.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            SELECT g."OperatorUserId"
              FROM "BuildingManagementOperatorBuildingGrants" g
              JOIN "Buildings" b ON b."Id"=g."BuildingId"
             WHERE g."BuildingId"=@building
               AND g."OperatorUserId" IN (@first,@second)
               AND g."RevokedAtUtc" IS NULL
               AND g."GrantedAtUtc"<=NOW()
               AND (g."ExpiresAtUtc" IS NULL OR g."ExpiresAtUtc">NOW())
               AND b."IsDeleted"=FALSE
             ORDER BY g."OperatorUserId"
             FOR SHARE OF g,b
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx.GetDbTransaction());
        cmd.Parameters.AddWithValue("building", buildingId);
        cmd.Parameters.AddWithValue("first", first);
        cmd.Parameters.AddWithValue("second", second);
        var hasFirst = false;
        var hasSecond = false;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var actor = reader.GetGuid(0);
            if (actor == first) hasFirst = true;
            if (actor == second) hasSecond = true;
        }
        if (!hasFirst || !hasSecond) throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("D04 requires current direct financial grants, active identities, an Owner role, and locked building memberships.");

    private sealed class IdentityLease : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;
        private bool _disposed;
        public IdentityLease(NpgsqlConnection conn, NpgsqlTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await _tx.RollbackAsync(); } // Read-only locks; no mutation.
            finally
            {
                await _tx.DisposeAsync();
                await _conn.DisposeAsync();
            }
        }
    }
}
