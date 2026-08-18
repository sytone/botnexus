using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Supplies a clock that measures time the process was actually <b>running</b>, so a policy budget
/// can distinguish a slow component from a suspended host (#3356).
/// </summary>
/// <remarks>
/// <para>
/// The <c>BeforeToolCall</c> budget is enforced with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>,
/// which is wall-clock. When the workstation slept for 4h41m mid-hook, the hook was declared to have
/// overrun a 15s budget by 16945s and the tool call was denied fail-closed. Nothing ran slowly — the
/// whole process was frozen. Measuring the breach against wall clock therefore misclassifies a host
/// suspend as a wedged policy provider, and names an innocent component in the diagnostic.
/// </para>
/// <para>
/// The fix is a second, <i>unbiased</i> reading rather than a bigger constant. Enlarging the budget
/// would not help — no finite budget survives an arbitrary suspend — and would weaken the liveness
/// bound the budget exists to provide.
/// </para>
/// </remarks>
public interface IHostSuspendDetector
{
    /// <summary>
    /// Captures an opaque starting timestamp on the active-time clock.
    /// </summary>
    long GetTimestamp();

    /// <summary>
    /// Returns the time the process spent actually running since <paramref name="startTimestamp"/>,
    /// excluding any interval the host was suspended.
    /// </summary>
    /// <param name="startTimestamp">A value previously returned by <see cref="GetTimestamp"/>.</param>
    TimeSpan GetElapsedActiveTime(long startTimestamp);
}

/// <summary>
/// The platform <see cref="IHostSuspendDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// On Windows this reads <c>QueryUnbiasedInterruptTime</c>, whose entire purpose is to exclude time
/// spent in sleep or hibernation — the exact quantity that corrupted the measurement in #3356.
/// <see cref="Stopwatch"/> (QueryPerformanceCounter) does <b>not</b> exclude it, which is why the
/// existing elapsed value tracked the 4h41m suspend window to within seconds.
/// </para>
/// <para>
/// On every other platform .NET's monotonic clock is already <c>CLOCK_MONOTONIC</c>, which by
/// definition does not advance across a suspend, so <see cref="Stopwatch"/> is itself unbiased and
/// is used directly. The fallback is therefore correct rather than merely tolerable: there is no
/// suspend time to subtract because it was never added.
/// </para>
/// </remarks>
public sealed class HostSuspendDetector : IHostSuspendDetector
{
    /// <summary>The shared platform instance. The type is stateless, so a singleton is safe.</summary>
    public static readonly HostSuspendDetector Instance = new();

    /// <summary>
    /// True when this platform can distinguish suspended time from running time. On platforms where
    /// it is false the monotonic clock already excludes suspend, so no distinction is needed.
    /// </summary>
    private static readonly bool UseUnbiasedInterruptTime = OperatingSystem.IsWindows();

    /// <summary>Unbiased interrupt time is reported in 100-nanosecond units, matching <see cref="TimeSpan"/> ticks.</summary>
    /// <inheritdoc />
    public long GetTimestamp()
    {
        if (UseUnbiasedInterruptTime && TryQueryUnbiasedInterruptTime(out var unbiased))
        {
            return unbiased;
        }

        return Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public TimeSpan GetElapsedActiveTime(long startTimestamp)
    {
        if (UseUnbiasedInterruptTime && TryQueryUnbiasedInterruptTime(out var now))
        {
            // Both readings come from the same clock; the value is monotonic, but clamp anyway so a
            // pathological reading can never produce a negative "active time" that would silently
            // excuse a genuinely slow hook.
            var ticks = now - startTimestamp;
            return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        }

        return Stopwatch.GetElapsedTime(startTimestamp);
    }

    /// <summary>
    /// Reads the unbiased interrupt time, returning <see langword="false"/> if the platform call is
    /// unavailable or fails. A failed read must degrade to the wall-clock reading — never to
    /// "assume suspended", which would disable the budget entirely.
    /// </summary>
    private static bool TryQueryUnbiasedInterruptTime(out long ticks)
    {
        ticks = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!QueryUnbiasedInterruptTime(out var value))
            {
                return false;
            }

            ticks = unchecked((long)value);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    // Declared with DllImport rather than LibraryImport deliberately: the source-generated marshaller
    // requires <AllowUnsafeBlocks> on the whole project, and turning unsafe code on across
    // BotNexus.Agent.Core to obtain one blittable bool/ulong call is a disproportionate trade.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
