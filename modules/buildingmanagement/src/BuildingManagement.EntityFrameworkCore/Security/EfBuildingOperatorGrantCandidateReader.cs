using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// Unregistered, infrastructure-only Slice A candidate: query persisted memberships
/// in SQL on EVERY request. This is NOT a production authorization grant:
/// callers must still verify the authenticated ABP account, operation
/// permission, resource graph and transaction/revocation boundary.
///
/// Does not implement the Application-layer grant-reader interface: EF Core
/// must not depend on Application. Tests adapt it explicitly, while runtime
/// uses its Deny-All reader. No DI marker or service exposure. Runtime retains
/// DenyAllBuildingOperatorGrantReader until the reviewed grant migration,
/// active Identity adapter and atomic write protocol are ready.
/// </summary>
public sealed class EfBuildingOperatorGrantCandidateReader
{
    private readonly DbContext _db;
    private readonly TimeProvider _clock;

    public EfBuildingOperatorGrantCandidateReader(
        DbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IReadOnlyCollection<Guid>> GetActiveBuildingIdsAsync(
        Guid operatorId)
    {
        if (operatorId == Guid.Empty)
            return Array.Empty<Guid>();

        // The currently running BuildingManagementDbContext intentionally
        // lacks this mapping. An accidental candidate instantiation cannot
        // invent access from a nonexistent membership table.
        if (_db.Model.FindEntityType(
                typeof(BuildingOperatorAccessGrant)) is null ||
            _db.Model.FindEntityType(typeof(Building)) is null)
            return Array.Empty<Guid>();

        return await BuildingOperatorGrantCandidateQuery
            .FindActiveBuildingIdsAsync(
                _db.Set<BuildingOperatorAccessGrant>(),
                _db.Set<Building>(),
                operatorId,
                _clock.GetUtcNow().UtcDateTime);
    }
}
