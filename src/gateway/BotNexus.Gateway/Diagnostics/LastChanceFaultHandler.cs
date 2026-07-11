using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Optional context probes the fault handler queries at fault time to enrich the breadcrumb.
/// Each returns null when the corresponding subsystem is not reachable during a hard exit.
/// </summary>
/// <param name="ActiveAgentCount">Returns the current agent count, or null if unavailable.</param>
/// <param name="ActiveSessionCount">Returns the current active session count, or null if unavailable.</param>
public sealed record FaultContextProbes(
    Func<int?>? ActiveAgentCount = null,
    Func<int?>? ActiveSessionCount = null);

/// <summary>
/// Installs the last-chance fault handler that flushes a structured <c>[FTL]</c> breadcrumb to
/// the log the instant the process is about to die — on an unhandled exception, an unobserved
/// task exception, or an abrupt <see cref="AppDomain.ProcessExit"/>. This guarantees that even a
/// dump-less hard exit leaves an investigable trail, closing the "silent death" gap.
/// <para>
/// The handler is deliberately allocation-light and fully defensive: every path swallows its own
/// failures, because a fault handler that throws would replace one silent death with another.
/// </para>
/// </summary>
public sealed class LastChanceFaultHandler
{
    private readonly ILogger _logger;
    private readonly FaultContextProbes _probes;
    private int _emitted;

    /// <summary>
    /// Creates the handler. Call <see cref="Install"/> once during host startup to attach it to
    /// the AppDomain and TaskScheduler fault events.
    /// </summary>
    public LastChanceFaultHandler(ILogger logger, FaultContextProbes? probes = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _probes = probes ?? new FaultContextProbes();
    }

    /// <summary>
    /// Attaches the handler to <see cref="AppDomain.UnhandledException"/>,
    /// <see cref="TaskScheduler.UnobservedTaskException"/>, and <see cref="AppDomain.ProcessExit"/>.
    /// Safe to call once; subsequent process-exit is de-duplicated against unhandled-exception so a
    /// crash that both throws and exits does not double-log the fatal record.
    /// </summary>
    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Emit("UnhandledException", ex?.ToString() ?? e.ExceptionObject?.ToString(), e.IsTerminating, force: true);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Emit("UnobservedTaskException", e.Exception?.ToString(), isTerminating: false, force: true);
        // Observing prevents the escalation policy from tearing the process down on our account.
        e.SetObserved();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        // Only emit here if no fatal breadcrumb was already written by an unhandled-exception
        // path — a clean shutdown still fires ProcessExit and should not log an [FTL] line as
        // fatal. We record it at a lower severity so the exit is still traceable.
        if (Volatile.Read(ref _emitted) != 0)
            return;
        Emit("ProcessExit", detail: null, isTerminating: true, force: false);
    }

    private void Emit(string reason, string? detail, bool isTerminating, bool force)
    {
        try
        {
            var breadcrumb = new FaultBreadcrumb
            {
                Reason = reason,
                Detail = detail,
                ExitCode = TryGetExitCode(),
                ActiveAgentCount = SafeProbe(_probes.ActiveAgentCount),
                ActiveSessionCount = SafeProbe(_probes.ActiveSessionCount),
                ThreadCount = SafeThreadCount(),
                WorkingSetBytes = SafeWorkingSet(),
                IsTerminating = isTerminating
            };

            var line = FaultBreadcrumbFormatter.Format(breadcrumb);

            if (force)
            {
                Interlocked.Exchange(ref _emitted, 1);
                _logger.LogCritical("{FaultBreadcrumb}", line);
            }
            else
            {
                // ProcessExit without a preceding fatal: informational trace, not a fatal claim.
                _logger.LogInformation("{FaultBreadcrumb}", line);
            }
        }
        catch
        {
            // A fault handler must never throw — that would replace one silent death with another.
        }
    }

    private static int? TryGetExitCode()
    {
        try
        {
            return Environment.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static int? SafeProbe(Func<int?>? probe)
    {
        if (probe is null)
            return null;
        try
        {
            return probe();
        }
        catch
        {
            return null;
        }
    }

    private static int SafeThreadCount()
    {
        try
        {
            return Process.GetCurrentProcess().Threads.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeWorkingSet()
    {
        try
        {
            return Environment.WorkingSet;
        }
        catch
        {
            return 0;
        }
    }
}
