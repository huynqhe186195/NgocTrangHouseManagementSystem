using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;

namespace BuildingManagement.OperatorMemberships;

/// <summary>
/// Internal read boundary. The application service supplies only server-verified
/// building IDs from the current operator's active grants; this is not a public
/// authorization contract and has no mutation methods.
/// </summary>
public interface IOperatorMembershipReadStore
{
    Task<OperatorMembershipReadPage> ReadPageAsync(
        Guid trustedOperatorId,
        IReadOnlyCollection<Guid> trustedBuildingIds,
        Guid? trustedBuildingFilter,
        DateTime trustedNowUtc,
        int skipCount,
        int maxResultCount);
}

public sealed record OperatorMembershipReadPage(
    int TotalCount,
    IReadOnlyList<OperatorMembershipReadModel> Items);

public sealed record OperatorMembershipReadModel(
    Guid GrantId,
    Guid OperatorUserId,
    Guid BuildingId,
    string BuildingName,
    Guid GrantedByUserId,
    DateTime GrantedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    int RevisionNumber);

/// <summary>
/// Production remains fail-closed until the Identity/membership integration,
/// database role and revocation protocol receive a separate activation review.
/// </summary>
[ExposeServices(typeof(IOperatorMembershipReadStore))]
public sealed class DenyAllOperatorMembershipReadStore :
    IOperatorMembershipReadStore, ITransientDependency
{
    public Task<OperatorMembershipReadPage> ReadPageAsync(
        Guid trustedOperatorId,
        IReadOnlyCollection<Guid> trustedBuildingIds,
        Guid? trustedBuildingFilter,
        DateTime trustedNowUtc,
        int skipCount,
        int maxResultCount)
        => Task.FromResult(new OperatorMembershipReadPage(
            0, Array.Empty<OperatorMembershipReadModel>()));
}
