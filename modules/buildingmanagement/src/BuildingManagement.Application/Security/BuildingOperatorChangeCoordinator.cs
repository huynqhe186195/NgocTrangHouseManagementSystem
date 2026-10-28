using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;
using BuildingManagement.Security;

namespace BuildingManagement.Security;

/// <summary>
/// Production DENY ALL until migration, identity+permission persistence,
/// issuer privilege policy and release gate have been approved.
/// </summary>
[ExposeServices(typeof(IBuildingOperatorGrantMutationWriter))]
public sealed class DenyAllBuildingOperatorGrantMutationWriter :
    IBuildingOperatorGrantMutationWriter, ITransientDependency
{
    public Task<Guid> AssignAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        DateTime? expiresAtUtc)
        => throw Denied();

    public Task RevokeAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        int expectedRevision)
        => throw Denied();

    private static AbpAuthorizationException Denied()
        => new("Building operator delegation is not enabled.");
}

/// <summary>
/// Non-remote application workflow; the design preflight checks ABP issuer,
/// active accounts, a separate Assign/Revoke permission and resource scope.
/// The writer MUST independently verify issuer membership IN its transaction.
/// No direct HTTP exposure or implicit admin role bypass.
/// </summary>
[RemoteService(IsEnabled = false)]
public sealed class BuildingOperatorChangeCoordinator : ITransientDependency
{
    private readonly BuildingOperatorChangeDesignGate _preflight;
    private readonly IBuildingOperatorChangeIssuerReader _issuers;
    private readonly IBuildingOperatorGrantMutationWriter _writer;

    public BuildingOperatorChangeCoordinator(
        BuildingOperatorChangeDesignGate preflight,
        IBuildingOperatorChangeIssuerReader issuers,
        IBuildingOperatorGrantMutationWriter writer)
    {
        _preflight = preflight;
        _issuers = issuers;
        _writer = writer;
    }

    public async Task<Guid> AssignAsync(
        Guid buildingId, Guid recipientUserId, DateTime? expiresAtUtc)
    {
        var issuer = await CheckAsync(
            BuildingOperatorChangeKind.Assign, buildingId, recipientUserId);
        return await _writer.AssignAtomicallyAsync(
            issuer, recipientUserId, buildingId, expiresAtUtc);
    }

    public async Task RevokeAsync(
        Guid buildingId, Guid recipientUserId, int expectedRevision)
    {
        if (expectedRevision < 1)
            throw Denied();
        var issuer = await CheckAsync(
            BuildingOperatorChangeKind.Revoke, buildingId, recipientUserId);
        await _writer.RevokeAtomicallyAsync(
            issuer, recipientUserId, buildingId, expectedRevision);
    }

    private async Task<Guid> CheckAsync(
        BuildingOperatorChangeKind change, Guid buildingId, Guid recipientUserId)
    {
        if (await _preflight.AssessDesignOnlyAsync(
                change, buildingId, recipientUserId) !=
            BuildingOperatorChangePreflightStatus.EligibleForDesignOnly)
            throw Denied();

        // A fresh, server-resolved issuer is required. The writer's separate
        // in-transaction membership recheck closes stale preflight grants.
        var issuer = await _issuers.ReadAsync(change);
        if (issuer is null || issuer.OperatorId == Guid.Empty ||
            !issuer.PrivilegedPermissionGranted ||
            issuer.OperatorId == recipientUserId)
            throw Denied();

        return issuer.OperatorId;
    }

    private static AbpAuthorizationException Denied()
        => new("Insufficient per-building operator delegation authority.");
}
