using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Volo.Abp.Authorization;

namespace BuildingManagement.Tests.Security;

/// <summary>
/// TEST-ASSEMBLY ONLY. Preserves legacy room-deletion business-behavior
/// tests by permitting only Room Read/Manage for the duration of a scoped
/// test call. No production module references this class. Authorization
/// integration uses the REAL BuildingCrudAccessPolicy in separate tests.
/// Default is DENY, even within the test assembly.
/// </summary>
public sealed class LegacyRoomBusinessFixturePolicy : IBuildingCrudAccessPolicy
{
    private static readonly AsyncLocal<int> RoomBusinessDepth = new();
    // Visible only in test assemblies. Not a production privilege source.
    public static bool IsWithinRoomBusinessAssertion => RoomBusinessDepth.Value > 0;

    public static IDisposable AllowRoomBusinessAssertions()
    {
        RoomBusinessDepth.Value++;
        return new ResetRoomBusinessContext();
    }

    public Task<IReadOnlyCollection<Guid>> GetAuthorizedBuildingIdsAsync(
        BuildingBusinessCapability capability) => throw Denied();

    public Task EnsureBuildingAsync(
        Guid buildingId, BuildingBusinessCapability capability)
        => throw Denied();

    public Task EnsureResourceAsync(
        BuildingBusinessResourceKind kind,
        Guid resourceId,
        BuildingBusinessCapability capability)
    {
        if (RoomBusinessDepth.Value > 0 &&
            resourceId != Guid.Empty &&
            kind == BuildingBusinessResourceKind.Room &&
            capability is BuildingBusinessCapability.ReadRoom or
                BuildingBusinessCapability.ManageRoom)
            return Task.CompletedTask;
        throw Denied();
    }

    public Task EnsureBuildingCreationAsync() => throw Denied();

    private static AbpAuthorizationException Denied()
        => new("The isolated legacy business fixture has no room permission.");

    private sealed class ResetRoomBusinessContext : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            RoomBusinessDepth.Value--;
            _disposed = true;
        }
    }
}
