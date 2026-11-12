using System;
using System.Threading.Tasks;

namespace BuildingManagement.Security;

/// <summary>
/// Never accepts client-provided approvals. The Application coordinator
/// authenticates two independent configured root approvers and verifies the
/// inactive founding owner; this contract is reserved for reviewed staging.
/// The writer must recheck the immutable founding grant and add a NEW grant
/// with durable co-signature evidence in a single transaction.
/// </summary>
public interface IBuildingOwnerRecoveryWriter
{
    Task<Guid> RecoverAtomicallyAsync(
        Guid buildingId, Guid originalOwnerId, Guid newOwnerId,
        Guid firstApprovedRootId, Guid secondApprovedRootId,
        Guid approvalEvidenceId);
}
