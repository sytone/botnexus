using System.Diagnostics;

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

    /// <summary>Requests termination of the process.</summary>
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

    public void Kill() => process.Kill();

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
