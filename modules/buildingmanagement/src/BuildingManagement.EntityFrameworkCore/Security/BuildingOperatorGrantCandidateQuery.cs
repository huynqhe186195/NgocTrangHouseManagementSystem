using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// RFC-only membership candidate SELECT for a later reviewed grant reader.
/// This is intentionally NOT a registered application service: the running
/// BuildingManagementDbContext does not map BuildingOperatorAccessGrant yet.
///
/// A candidate building ID is NOT an authorization decision. The caller
/// must separately validate ICurrentUser, enabled ABP identity account,
/// operation permission, scope graph, and transaction/revocation safety.
/// Never pass a user ID from an untrusted client or cache this result for
/// a later write.
/// </summary>
public static class BuildingOperatorGrantCandidateQuery
{
    public static async Task<IReadOnlyCollection<Guid>> FindActiveBuildingIdsAsync(
        IQueryable<BuildingOperatorAccessGrant> grantRows,
        IQueryable<Building> buildingRows,
        Guid trustedOperatorUserId,
        DateTime trustedNowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grantRows);
        ArgumentNullException.ThrowIfNull(buildingRows);

        if (trustedOperatorUserId == Guid.Empty)
            return Array.Empty<Guid>();

        if (trustedNowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "Caller must supply a trusted UTC clock value.",
                nameof(trustedNowUtc));

        // Keep every predicate in SQL. Never read all grants into memory,
        // cache a previous positive result, or trust a client BuildingId.
        // Join with a live building to prevent grants to soft-deleted sites.
        // Explicit !IsDeleted is defense in depth for test DbContexts that
        // do not enable the ABP ISoftDelete global filter.
        return await (
            from grant in grantRows.AsNoTracking()
            join building in buildingRows.AsNoTracking()
                on grant.BuildingId equals building.Id
            where grant.OperatorUserId == trustedOperatorUserId
                && grant.GrantedAtUtc <= trustedNowUtc
                && grant.RevokedAtUtc == null
                && (grant.ExpiresAtUtc == null ||
                    grant.ExpiresAtUtc > trustedNowUtc)
                && !building.IsDeleted
            select grant.BuildingId
        ).Distinct().ToArrayAsync(cancellationToken);
    }
}
