using System;
using System.Threading.Tasks;

namespace BuildingManagement.Security;

/// <summary>
/// Mandatory boundary for Building mutations. The AppService checks the
/// fixed operation permission and server-resolved scope BEFORE this call.
/// The writer must additionally lock the effective persisted membership in
/// the SAME database transaction as the mutation, or fail closed.
/// This interface is never a grant/permission token.
/// </summary>
public interface IBuildingScopedWriteBoundary
{
    Task<T> ExecuteBuildingAsync<T>(
        Guid serverResolvedBuildingId, Func<Task<T>> mutation);
}
