using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BuildingManagement.Security;

/// <summary>
/// Internal-only capability-neutral read store: receives ONLY a set of
/// server-verified building IDs. It is NOT an HTTP contract, credential,
/// or authorization decision. The application layer must authenticate,
/// check a dedicated read permission and validate live memberships first.
/// </summary>
public enum BuildingScopedCollectionKind
{
    Building = 1,
    Floor = 2,
    Room = 3,
    Contract = 4
}

public sealed record BuildingScopedListPage(
    int TotalCount,
    IReadOnlyList<Guid> ResourceIds);

public interface IBuildingScopedCollectionStore
{
    Task<BuildingScopedListPage> ReadPageAsync(
        BuildingScopedCollectionKind kind,
        IReadOnlyCollection<Guid> trustedBuildingIds,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken = default);
}
