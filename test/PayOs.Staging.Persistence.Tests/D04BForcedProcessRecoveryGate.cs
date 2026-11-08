using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Runs the actual production D04 receipt ACK class in a NEW .NET OS process
/// after forcibly killing a different process whose read of the committed
/// legacy journal had passed. No mock ACK, test cookie, live provider or
/// real tenant database. Invoked only from the guarded disposable 19a suite.
/// </summary>
internal static class D04BForcedProcessRecoveryGate
{
    internal static async Task VerifyAsync(Guid intentId)
    {
        Assert.NotEqual(Guid.Empty, intentId);
        var probe = Environment.GetEnvironmentVariable(
            "BM_PAYOS_D04_PROCESS_PROBE_DLL");
        Assert.False(string.IsNullOrWhiteSpace(probe));
        Assert.True(Path.IsPathFullyQualified(probe));
        Assert.True(File.Exists(probe));

        using var interrupted = NewProbe(probe!, "hold", intentId);
        Assert.True(interrupted.Start());
        // Drain stderr ASYNCHRONOUSLY from the moment the child starts:
        // PostgreSQL/.NET can write an exception before printing the
        // expected stdout barrier. Otherwise a null ReadLine obscures the
        // true failure and a full stderr pipe can deadlock the child.
        var childErrors = interrupted.StandardError.ReadToEndAsync();
        try
        {
            // The child must first read the durable journal and prove
            // staging has not been ACKed. Null means the process exited
            // prematurely, NOT that the crash-recovery gate succeeded.
            var barrier = await interrupted.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(25));
            if (!string.Equals(barrier,
                    "D04_PROCESS_BARRIER_COMMITTED_UNACKNOWLEDGED",
                    StringComparison.Ordinal))
            {
                if (!interrupted.HasExited)
                {
                    // A non-barrier line is never a valid proof. Stop ONLY
                    // our own child instead of allowing it to run indefinitely.
                    interrupted.Kill(entireProcessTree: true);
                }
                await interrupted.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20));
                var stderr = await childErrors.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(false,
                    "D04 crash-probe hold did not reach committed barrier. " +
                    $"exit={interrupted.ExitCode}; firstStdout={barrier ?? "<EOF>"}; " +
                    $"stderr={stderr}");
            }

            // Terminate exactly the process WE created. This is an actual
            // OS kill rather than Dispose/rollback within the same test.
            interrupted.Kill(entireProcessTree: true);
            await interrupted.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotEqual(0, interrupted.ExitCode);
        }
        finally
        {
            if (!interrupted.HasExited)
            {
                interrupted.Kill(entireProcessTree: true);
                await interrupted.WaitForExitAsync();
            }
            // Do not leave a pending stderr reader (or leak process handles).
            await childErrors.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // No process memory survives. A different .NET process reconstructs
        // the real Acknowledger and SQL receipt store using persisted DBs.
        await RunToCompletionAsync(probe!, "recover", intentId,
            "D04_FRESH_PROCESS_RECOVERY_PASS");
        await RunToCompletionAsync(probe!, "replay", intentId,
            "D04_FRESH_PROCESS_REPLAY_READ_ONLY_PASS");
    }

    private static Process NewProbe(string dll, string action, Guid intent)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(action);
        start.ArgumentList.Add(intent.ToString("D"));
        return new Process { StartInfo = start };
    }

    private static async Task RunToCompletionAsync(
        string dll, string action, Guid intent, string expectedOutput)
    {
        using var process = NewProbe(dll, action, intent);
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0,
            $"D04 fresh process {action} failed: exit={process.ExitCode}, stderr={error}");
        Assert.Contains(expectedOutput, output, StringComparison.Ordinal);
    }
}
