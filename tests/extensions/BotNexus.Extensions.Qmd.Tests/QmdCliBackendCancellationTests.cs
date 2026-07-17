using System.Diagnostics;

namespace BotNexus.Extensions.Qmd.Tests;

/// <summary>
/// Verifies that <see cref="QmdCliBackend.RunAsync"/> never leaves an orphaned <c>qmd</c>
/// child process behind when the call is aborted. The internal-timeout path already killed
/// the process tree; the caller-cancellation path used to skip the kill (the catch was guarded
/// with <c>when (!ct.IsCancellationRequested)</c>), orphaning a still-running child. These tests
/// spawn a real long-lived child via the backend, abort it, and assert the child actually exits.
/// Mirrors the OpenClaw "abort orphaned qmd subprocess on caller signal" fix whose fixture child
/// "never closes on its own", so the only way the call can settle cleanly is the abort killing it.
/// </summary>
public sealed class QmdCliBackendCancellationTests
{
    // A long-lived child that first writes its own PID to <paramref name="pidFile"/> then sleeps,
    // so the test can poll that exact PID for exit after aborting. The sleep child belongs to the
    // process the backend starts, so killing the process tree reaps it. Cross-platform.
    private static (string exe, List<string> args) PidWritingSleeper(string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            // powershell writes $PID (the process QmdCliBackend starts) then sleeps.
            return ("powershell.exe", new List<string>
            {
                "-NoProfile", "-NonInteractive", "-Command",
                $"$PID | Out-File -FilePath '{pidFile}' -Encoding ascii; Start-Sleep -Seconds 120"
            });
        }

        // bash writes $$ (its own PID) then sleeps.
        return ("/bin/bash", new List<string>
        {
            "-c", $"echo $$ > '{pidFile}'; sleep 120"
        });
    }

    private static async Task<int> WaitForPidFileAsync(string pidFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    await using var stream = new FileStream(
                        pidFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 128,
                        useAsync: true);
                    using var reader = new StreamReader(stream);
                    var text = (await reader.ReadToEndAsync()).Trim();
                    if (int.TryParse(text, out var pid) && pid > 0)
                        return pid;
                }
                catch (IOException)
                {
                    // The child may still be replacing/flushing the file. Retry until the
                    // existing deadline rather than turning a normal writer race into a flake.
                }
            }
            await Task.Delay(25);
        }

        throw new TimeoutException($"Child never wrote its PID to {pidFile} within {timeout.TotalSeconds}s.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id is running -> it exited (was killed).
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(pid))
                return true;
            await Task.Delay(25);
        }

        return !IsProcessAlive(pid);
    }

    [Fact]
    public async Task RunAsync_WhenCallerTokenCancelled_KillsChildProcessTree_NoOrphan()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"qmd-cancel-test-{Guid.NewGuid():N}.pid");
        var (exe, args) = PidWritingSleeper(pidFile);

        // Generous internal timeout so it is the CALLER cancellation -- not the internal timeout --
        // that aborts the wait. This is the path that previously leaked the child.
        var backend = new QmdCliBackend(exe, timeout: TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();

        var run = backend.RunAsync(args, cts.Token);

        // Wait until the child is actually running (it has written its PID), then cancel the caller.
        var childPid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(20));
        Assert.True(IsProcessAlive(childPid), "child should be running before cancellation");

        cts.Cancel();

        // Caller cancellation must surface as an OperationCanceledException (not a TimeoutException).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // And, crucially, the child must have been killed -- no orphaned qmd subprocess.
        var exited = await WaitForExitAsync(childPid, TimeSpan.FromSeconds(20));
        Assert.True(exited, $"child process {childPid} should have been killed on caller cancellation, but it is still running (orphaned)");

        try { File.Delete(pidFile); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RunAsync_WhenInternalTimeoutFires_StillKillsChildProcessTree()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"qmd-timeout-test-{Guid.NewGuid():N}.pid");
        var (exe, args) = PidWritingSleeper(pidFile);

        // Leave enough startup time for a cold PowerShell process to write its PID on loaded
        // Windows hosts while still exercising the backend's internal-timeout kill path.
        var backend = new QmdCliBackend(exe, timeout: TimeSpan.FromSeconds(5));

        var run = backend.RunAsync(args, CancellationToken.None);

        var childPid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(20));

        // The internal-timeout path surfaces a TimeoutException.
        await Assert.ThrowsAsync<TimeoutException>(() => run);

        var exited = await WaitForExitAsync(childPid, TimeSpan.FromSeconds(20));
        Assert.True(exited, $"child process {childPid} should have been killed on internal timeout, but it is still running (orphaned)");

        try { File.Delete(pidFile); } catch { /* best effort */ }
    }
}
