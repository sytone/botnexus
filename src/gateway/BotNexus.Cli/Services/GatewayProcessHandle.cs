using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Minimal view of a live OS process used by the PID-file-less gateway discovery introduced for
/// issue #2772. Exists so the discovery and stop path can be tested deterministically WITHOUT ever
/// enumerating, inspecting or signalling a real process: <see cref="System.Diagnostics.Process"/> is
/// sealed-in-practice for test purposes (its identity members are not virtual and cannot be faked).
/// </summary>
public interface IGatewayProcessHandle
{
    /// <summary>Operating-system process id.</summary>
    int Id { get; }

    /// <summary>
    /// Full path of the executable image backing this process, or null when it cannot be read
    /// (access denied, or the process exited). Null is ALWAYS treated as "not identifiable" and
    /// therefore never as the gateway.
    /// </summary>
    string? ExecutablePath { get; }

    /// <summary>
    /// Asks the process to shut down rather than killing it, and reports whether such a signal was
    /// actually delivered.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a termination request reached the process, so the caller should
    /// wait before escalating. <see langword="false"/> when this platform offers no graceful
    /// signal, or the process is already gone - in both cases waiting would buy nothing.
    /// </returns>
    /// <remarks>
    /// Separate from <see cref="Kill"/> so the escalation is the caller's decision and is
    /// observable in tests, rather than hidden inside a handle that "sometimes" kills politely.
    /// </remarks>
    bool RequestGracefulStop();

    /// <summary>Terminates the process immediately. No opportunity to run shutdown work.</summary>
    void Kill();

    /// <summary>Waits up to <paramref name="milliseconds"/> for exit; true when it exited.</summary>
    bool WaitForExit(int milliseconds);
}

/// <summary>
/// Production <see cref="IGatewayProcessHandle"/> backed by a real <see cref="Process"/>.
/// </summary>
internal sealed class LiveProcessHandle(Process process, Func<Process, int, bool>? waitForExitOverride = null)
    : IGatewayProcessHandle
{
    public int Id => process.Id;

    public string? ExecutablePath
    {
        get
        {
            try
            {
                return process.HasExited ? null : process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool RequestGracefulStop()
    {
        // Windows has no SIGTERM. CloseMainWindow posts WM_CLOSE, which a console host or a
        // service does not act on, so it would return true having done nothing - worse than
        // admitting there is no graceful path and letting the caller kill immediately.
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            if (process.HasExited)
                return false;

            // 0 means the signal was accepted. Anything else (ESRCH: gone between the check and
            // here; EPERM: not ours to signal) means no request is pending, so do not make the
            // caller wait out a timeout for an exit that was never asked for.
            return NativeKill(process.Id, Sigterm) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                      or InvalidOperationException or NotSupportedException)
        {
            // No libc, no such entry point, or the process object cannot answer. Fall back to the
            // kill path rather than failing the stop outright.
            return false;
        }
    }

    public void Kill() => process.Kill();

    /// <summary>SIGTERM. 15 on every Unix platform .NET runs on.</summary>
    private const int Sigterm = 15;

    // DllImport rather than LibraryImport, matching HostSuspendDetector: the source-generated
    // marshaller wants <AllowUnsafeBlocks> across the project, which is a disproportionate trade
    // for one blittable two-int call.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int signal);

    public bool WaitForExit(int milliseconds)
        => waitForExitOverride is not null
            ? waitForExitOverride(process, milliseconds)
            : process.WaitForExit(milliseconds);

    /// <summary>
    /// Enumerates every live process on the machine as a handle. Wrapping happens lazily so a
    /// process that dies mid-enumeration simply reports a null executable path.
    /// </summary>
    public static IEnumerable<IGatewayProcessHandle> EnumerateAll()
    {
        foreach (var process in Process.GetProcesses())
            yield return new LiveProcessHandle(process);
    }
}
