using System;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using Volo.Abp.Authorization;

namespace BuildingManagement.Tests.Security;

/// <summary>
/// TEST ASSEMBLY ONLY. Preserves historical Contract.EndAsync semantics
/// tests while the runtime API is DENIED and remote-disabled pending
/// 2G.3–2G.5 financial settlement orchestration.
/// </summary>
public sealed class LegacyContractTerminationFixtureGate :
    IDirectContractTerminationGate
{
    private static readonly AsyncLocal<int> CurrentAssertionDepth = new();

    public static IDisposable AllowHistoricalEndAssertions()
    {
        CurrentAssertionDepth.Value++;
        return new Reset();
    }

    public Task EnsureDirectTerminationAllowedAsync()
    {
        if (CurrentAssertionDepth.Value > 0)
            return Task.CompletedTask;

        throw new AbpAuthorizationException(
            "Direct contract termination is disabled until CompleteMoveOut.");
    }

    private sealed class Reset : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentAssertionDepth.Value--;
        }
    }
}
