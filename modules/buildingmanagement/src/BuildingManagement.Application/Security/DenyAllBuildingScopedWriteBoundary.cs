using System;
using System.Threading.Tasks;
using Volo.Abp.Authorization;
using Volo.Abp.DependencyInjection;

namespace BuildingManagement.Security;

/// <summary>
/// Production default: DENY even if the preliminary CRUD policy passes.
/// A real EF transaction lock must be reviewed and registered explicitly
/// after production migration, DB permissions and revoke-race signoff.
/// </summary>
[ExposeServices(typeof(IBuildingScopedWriteBoundary))]
public sealed class DenyAllBuildingScopedWriteBoundary
    : IBuildingScopedWriteBoundary, ITransientDependency
{
    public Task<T> ExecuteBuildingAsync<T>(
        Guid serverResolvedBuildingId, Func<Task<T>> mutation)
        => throw new AbpAuthorizationException(
            "Building writes require an active transaction-scoped membership fence.");
}
