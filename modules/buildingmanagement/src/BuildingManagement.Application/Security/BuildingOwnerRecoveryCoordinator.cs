using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace BuildingManagement.Security;

/// <summary>
/// Server-side, durable two-person approval reference. This is NOT a DTO:
/// no remote method receives or trusts either approver or evidence ID.
/// </summary>
public sealed record BuildingOwnerRecoveryApproval(
    Guid FirstRootId, Guid SecondRootId, Guid EvidenceId);

public interface IBuildingOwnerRecoveryApprovalReader
{
    Task<BuildingOwnerRecoveryApproval?> ReadApprovedAsync(
        Guid buildingId, Guid newOwnerId);
}

public interface IBuildingOwnerRecoveryFounderReader
{
    Task<Guid?> ReadFoundingOwnerAsync(Guid buildingId);
}

[ExposeServices(typeof(IBuildingOwnerRecoveryApprovalReader))]
public sealed class DenyAllBuildingOwnerRecoveryApprovalReader :
    IBuildingOwnerRecoveryApprovalReader, ITransientDependency
{
    public Task<BuildingOwnerRecoveryApproval?> ReadApprovedAsync(
        Guid buildingId, Guid newOwnerId)
        => Task.FromResult<BuildingOwnerRecoveryApproval?>(null);
}

[ExposeServices(typeof(IBuildingOwnerRecoveryFounderReader))]
public sealed class DenyAllBuildingOwnerRecoveryFounderReader :
    IBuildingOwnerRecoveryFounderReader, ITransientDependency
{
    public Task<Guid?> ReadFoundingOwnerAsync(Guid buildingId)
        => Task.FromResult<Guid?>(null);
}

[ExposeServices(typeof(IBuildingOwnerRecoveryWriter))]
public sealed class DenyAllBuildingOwnerRecoveryWriter :
    IBuildingOwnerRecoveryWriter, ITransientDependency
{
    public Task<Guid> RecoverAtomicallyAsync(
        Guid buildingId, Guid originalOwnerId, Guid newOwnerId,
        Guid firstApprovedRootId, Guid secondApprovedRootId,
        Guid approvalEvidenceId)
        => throw new AbpAuthorizationException(
            "Two-person building owner recovery is disabled.");
}

/// <summary>
/// Explicitly NON-REMOTE recovery coordinator. A second ROOT sign-off
/// must originate from a durable server-side approval reader; a request
/// body, role claim or root username alone is never acceptable evidence.
/// No positive writer/approval reader is registered in production.
/// </summary>
[RemoteService(IsEnabled = false)]
public sealed class BuildingOwnerRecoveryCoordinator : ITransientDependency
{
    private readonly IBuildingOwnerBootstrapPrincipalReader _currentRoot;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IBuildingOwnerRecoveryApprovalReader _approvals;
    private readonly IBuildingOwnerRecoveryFounderReader _founders;
    private readonly IBuildingOwnerRecoveryWriter _writer;
    private readonly IOptions<BuildingOwnerBootstrapSecurityOptions> _options;

    public BuildingOwnerRecoveryCoordinator(
        IBuildingOwnerBootstrapPrincipalReader currentRoot,
        IBuildingOperatorActiveAccountReader accounts,
        IBuildingOwnerRecoveryApprovalReader approvals,
        IBuildingOwnerRecoveryFounderReader founders,
        IBuildingOwnerRecoveryWriter writer,
        IOptions<BuildingOwnerBootstrapSecurityOptions> options)
    {
        _currentRoot = currentRoot;
        _accounts = accounts;
        _approvals = approvals;
        _founders = founders;
        _writer = writer;
        _options = options;
    }

    public async Task<Guid> RecoverAsync(Guid buildingId, Guid newOwnerId)
    {
        if (buildingId == Guid.Empty || newOwnerId == Guid.Empty)
            throw Denied();

        var caller = await _currentRoot.GetAuthorizedIssuerAsync();
        if (!caller.HasValue || caller.Value == Guid.Empty)
            throw Denied();

        var proof = await _approvals.ReadApprovedAsync(buildingId, newOwnerId);
        if (proof is null || proof.EvidenceId == Guid.Empty ||
            proof.FirstRootId == Guid.Empty || proof.SecondRootId == Guid.Empty ||
            proof.FirstRootId == proof.SecondRootId ||
            (caller.Value != proof.FirstRootId &&
             caller.Value != proof.SecondRootId))
            throw Denied();

        // A single configured root makes recovery impossible, not weaker.
        // Two explicitly designated roots AND one LIVE authenticated root
        // must participate; the normal root-only bootstrap policy is intact.
        var roots = _options.Value.RootUserIds;
        if (roots?.Contains(proof.FirstRootId) != true ||
            roots.Contains(proof.SecondRootId) != true)
            throw Denied();

        var original = await _founders.ReadFoundingOwnerAsync(buildingId);
        if (!original.HasValue || original.Value == Guid.Empty ||
            original.Value == newOwnerId ||
            original.Value == proof.FirstRootId && newOwnerId == proof.SecondRootId ||
            original.Value == proof.SecondRootId && newOwnerId == proof.FirstRootId)
            throw Denied();

        if (newOwnerId == proof.FirstRootId ||
            newOwnerId == proof.SecondRootId)
            throw Denied();

        if (await _accounts.IsActiveAsync(original.Value) ||
            !await _accounts.IsActiveAsync(proof.FirstRootId) ||
            !await _accounts.IsActiveAsync(proof.SecondRootId) ||
            !await _accounts.IsActiveAsync(newOwnerId))
            throw Denied();

        return await _writer.RecoverAtomicallyAsync(
            buildingId, original.Value, newOwnerId, proof.FirstRootId,
            proof.SecondRootId, proof.EvidenceId);
    }

    private static AbpAuthorizationException Denied()
        => new("Owner recovery requires an inactive founder and two active, " +
               "independent, designated root approvals.");
}
