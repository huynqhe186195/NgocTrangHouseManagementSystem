using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Permissions;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Users;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Staging-only, explicit DI; never registered by convention. Each signer
/// must sign under their OWN authenticated server principal. No HTTP route.
/// The additive PostgreSQL migration owns the evidence ledger.
/// </summary>
public sealed class SliceAStagingOwnerRecoveryEvidenceStore :
    IBuildingOwnerRecoveryApprovalReader, IBuildingOwnerRecoveryFounderReader
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _provider;
    private readonly ICurrentUser _user;
    private readonly IPermissionChecker _permissions;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IOptions<BuildingOwnerBootstrapSecurityOptions> _bootstrap;
    private readonly IOptions<BuildingOwnerRecoverySecurityOptions> _recovery;

    public SliceAStagingOwnerRecoveryEvidenceStore(
        IDbContextProvider<BuildingManagementDbContext> provider,
        ICurrentUser user, IPermissionChecker permissions,
        IBuildingOperatorActiveAccountReader accounts,
        IOptions<BuildingOwnerBootstrapSecurityOptions> bootstrap,
        IOptions<BuildingOwnerRecoverySecurityOptions> recovery)
    {
        _provider = provider;
        _user = user;
        _permissions = permissions;
        _accounts = accounts;
        _bootstrap = bootstrap;
        _recovery = recovery;
    }

    /// <summary>
    /// Non-remote, server-administered record. Only one unique signoff
    /// per signer/evidence. Never trusts a claimed user ID in a DTO.
    /// Requires a configured BootstrapOwner OR distinct recovery co-signer,
    /// the corresponding dedicated ABP permission, and an inactive founder.
    /// </summary>
    public async Task RecordSignoffAsync(
        Guid evidenceId, Guid buildingId, Guid foundingOwnerId,
        Guid newOwnerId, DateTime expiresAtUtc)
    {
        if (!_user.IsAuthenticated || !_user.Id.HasValue ||
            _user.Id.Value == Guid.Empty ||
            evidenceId == Guid.Empty || buildingId == Guid.Empty ||
            foundingOwnerId == Guid.Empty || newOwnerId == Guid.Empty ||
            foundingOwnerId == newOwnerId ||
            expiresAtUtc.Kind != DateTimeKind.Utc ||
            expiresAtUtc <= DateTime.UtcNow ||
            expiresAtUtc > DateTime.UtcNow.AddHours(24))
            throw Denied();
        var signer = _user.Id.Value;
        var isRoot = _bootstrap.Value.RootUserIds.Contains(signer);
        var isCoSigner = _recovery.Value.CoSignerUserIds.Contains(signer);
        if (isRoot == isCoSigner ||
            !await _accounts.IsActiveAsync(signer) ||
            !await _accounts.IsActiveAsync(newOwnerId) ||
            await _accounts.IsActiveAsync(foundingOwnerId))
            throw Denied();
        if (!await _permissions.IsGrantedAsync(isRoot
                ? BuildingManagementPermissions.Buildings.BootstrapOwner
                : BuildingManagementPermissions.Buildings.CoSignOwnerRecovery))
            throw Denied();
        if (await ReadFoundingOwnerAsync(buildingId) != foundingOwnerId)
            throw Denied();

        var db = await _provider.GetDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BuildingManagementOwnerRecoverySignoffs"
                ("EvidenceId", "BuildingId", "FoundingOwnerId",
                 "NewOwnerId", "ApprovedByUserId", "ApprovedAtUtc",
                 "ExpiresAtUtc")
            VALUES ({evidenceId}, {buildingId}, {foundingOwnerId},
                    {newOwnerId}, {signer}, {DateTime.UtcNow}, {expiresAtUtc})
            """);
    }

    public async Task<Guid?> ReadFoundingOwnerAsync(Guid buildingId)
    {
        if (buildingId == Guid.Empty)
            return null;
        var db = await _provider.GetDbContextAsync();
        return await ScalarGuidAsync(db,
            """
            SELECT g."OperatorUserId"
              FROM "BuildingManagementOperatorBuildingGrants" g
              JOIN "Buildings" b ON b."Id" = g."BuildingId"
             WHERE g."BuildingId" = @building
               AND g."OperatorUserId" = g."GrantedByUserId"
               AND g."RevokedAtUtc" IS NULL AND b."IsDeleted" = FALSE
             LIMIT 1
            """, buildingId);
    }

    public async Task<BuildingOwnerRecoveryApproval?> ReadApprovedAsync(
        Guid buildingId, Guid newOwnerId)
    {
        if (buildingId == Guid.Empty || newOwnerId == Guid.Empty)
            return null;
        var db = await _provider.GetDbContextAsync();
        await db.Database.OpenConnectionAsync();
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = """
            SELECT "EvidenceId", "ApprovedByUserId", "FoundingOwnerId"
              FROM "BuildingManagementOwnerRecoverySignoffs"
             WHERE "BuildingId" = @building AND "NewOwnerId" = @recipient
               AND "ConsumedAtUtc" IS NULL AND "ExpiresAtUtc" > @now
             ORDER BY "EvidenceId", "ApprovedByUserId"
            """;
        Add(cmd, "building", buildingId);
        Add(cmd, "recipient", newOwnerId);
        Add(cmd, "now", DateTime.UtcNow);

        var matches = new Dictionary<Guid, (Guid Founder, HashSet<Guid> Signers)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var evidence = reader.GetGuid(0);
                var signer = reader.GetGuid(1);
                var founder = reader.GetGuid(2);
                if (!matches.TryGetValue(evidence, out var group))
                    group = (founder, new HashSet<Guid>());
                if (group.Founder != founder)
                    return null;
                group.Signers.Add(signer);
                matches[evidence] = group;
            }
        }
        var rootIds = _bootstrap.Value.RootUserIds;
        var cosigners = _recovery.Value.CoSignerUserIds;
        var eligible = matches.Where(x =>
            x.Value.Signers.Count == 2 &&
            x.Value.Signers.Count(rootIds.Contains) == 1 &&
            x.Value.Signers.Count(cosigners.Contains) == 1).ToArray();
        if (eligible.Length != 1)
            return null;
        var first = eligible[0].Value.Signers.Single(rootIds.Contains);
        var second = eligible[0].Value.Signers.Single(cosigners.Contains);
        return new(first, second, eligible[0].Key);
    }

    private static async Task<Guid?> ScalarGuidAsync(
        BuildingManagementDbContext db, string sql, Guid buildingId)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        Add(command, "building", buildingId);
        return await command.ExecuteScalarAsync() is Guid value ? value : null;
    }

    private static void Add(System.Data.Common.DbCommand cmd,
        string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static AbpAuthorizationException Denied()
        => new("Owner recovery evidence requires a verified individual signer.");
}
