using System;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Volo.Abp.Authorization;

namespace BuildingManagement.Tests.Security;

/// <summary>
/// Strictly test-only: historical Room lifecycle unit tests opted in to
/// this fixture before Slice A added transaction-scoped write fences.
/// Allows Room mutation only inside that existing AsyncLocal opt-in.
/// Never registered outside disposable test modules; no Building/Floor
/// mutation or unscoped operation can use this boundary.
/// </summary>
public sealed class LegacyRoomBusinessFixtureWriteBoundary
    : IBuildingScopedWriteBoundary
{
    public Task<T> ExecuteBuildingAsync<T>(
        Guid serverResolvedBuildingId, Func<Task<T>> mutation,
        BuildingScopedMutationKind kind = BuildingScopedMutationKind.Building)
    {
        if (!LegacyRoomBusinessFixturePolicy.IsWithinRoomBusinessAssertion ||
            kind != BuildingScopedMutationKind.Room ||
            serverResolvedBuildingId == Guid.Empty)
            throw new AbpAuthorizationException(
                "Legacy Room business test fixture is not authorized.");

        return mutation();
    }
}
