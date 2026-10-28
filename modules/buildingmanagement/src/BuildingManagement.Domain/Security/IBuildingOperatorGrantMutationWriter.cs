using System;
using System.Threading.Tasks;

namespace BuildingManagement.Security;

/// <summary>
/// Internal persistence contract for operator delegation. The caller MUST
/// verify real ABP permission and active user identity; the writer RECHECKS
/// the issuer's building membership inside the same serializable transaction.
/// NOT a public controller, and NOT implemented by runtime EF DI.
/// </summary>
public interface IBuildingOperatorGrantMutationWriter
{
    Task<Guid> AssignAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        DateTime? expiresAtUtc);

    Task RevokeAtomicallyAsync(
        Guid trustedIssuerId, Guid recipientUserId, Guid buildingId,
        int expectedRevision);
}
