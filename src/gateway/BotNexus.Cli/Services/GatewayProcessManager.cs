using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cli.Services;

/// <summary>
/// Manages the lifecycle of the BotNexus Gateway process, including PID file tracking,
/// process spawning (detached or attached), health checking, and cleanup.
/// Supports Windows and Unix (Linux/macOS).
/// </summary>
public sealed class GatewayProcessManager : IGatewayProcessManager
{
    private readonly IHealthChecker _healthChecker;
    private readonly ILogger<GatewayProcessManager> _logger;
    // Timeout for WaitForExit after Kill(). Defaults to 5 seconds in production;
    // injectable for tests to simulate the timeout path without actually waiting.
    private readonly TimeSpan _waitForExitTimeout;
    // Allows tests to inject a custom WaitForExit implementation to simulate timeout scenarios
    // without relying on OS-level process termination timing.
    private readonly Func<Process, int, bool>? _waitForExitOverride;
    // Injectable HttpClient for status probe -- allows tests to mock HTTP responses.
    private readonly HttpClient _probeClient;
    // Injectable process enumeration for PID-file-less discovery (#2772). Tests supply fakes so no
    // real process is ever inspected or signalled.
    private readonly Func<IEnumerable<IGatewayProcessHandle>> _processEnumerator;
    // Default health URL used for status probing when no override is provided.
    internal const string DefaultHealthUrl = "http://localhost:5005/health";

    public GatewayProcessManager(
        IHealthChecker healthChecker,
        ILogger<GatewayProcessManager> logger,
        TimeSpan? waitForExitTimeout = null,
        Func<Process, int, bool>? waitForExitOverride = null,
        HttpClient? probeClient = null,
        Func<IEnumerable<IGatewayProcessHandle>>? processEnumerator = null)
    {
        _processEnumerator = processEnumerator ?? LiveProcessHandle.EnumerateAll;
        _healthChecker = healthChecker;
        _logger = logger;
        _waitForExitTimeout = waitForExitTimeout ?? TimeSpan.FromSeconds(5);
        _waitForExitOverride = waitForExitOverride;
        _probeClient = probeClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// Resolves the PID file path from the given home directory, BOTNEXUS_HOME env var, or default ~/.botnexus.
    /// </summary>
    private static string ResolvePidFilePath(string? homePath = null)
    {
        var home = string.IsNullOrWhiteSpace(homePath)
            ? (Environment.GetEnvironmentVariable("BOTNEXUS_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus"))
            : homePath;
        return Path.Combine(home, "gateway.pid");
    }

    /// <summary>
    /// Checks whether the gateway process is currently running by reading the PID file and verifying
    /// that the recorded process identity still matches the live process (see
    /// <see cref="ResolveVerifiedProcessAsync"/>). An unverifiable or recycled PID counts as NOT running.
    /// </summary>
    public bool IsRunning(string? homePath = null, string? gatewayBinaryPath = null)
    {
        var pidFilePath = ResolvePidFilePath(homePath);
        var (process, _, _) = ResolveVerifiedProcessAsync(pidFilePath).GetAwaiter().GetResult();
        if (process is not null)
            return true;

        // #2772: a live gateway with no (or an unverifiable) PID file is exactly the state that
        // made `update` claim "gateway left running" about nothing. Same path-identity check as
        // StopAsync; no process is signalled here at all.
        return FindProcessByBinaryPath(gatewayBinaryPath) is not null;
    }

    /// <summary>
    /// Starts the gateway process in detached or attached mode, writes the PID file,
    /// and waits for the health endpoint to become responsive.
    /// </summary>
    public async Task<GatewayStartResult> StartAsync(GatewayStartOptions options, CancellationToken cancellationToken = default)
    {
        var pidFilePath = ResolvePidFilePath(options.HomePath);

        // Check if already running. Anything that cannot be positively identified as our gateway is
        // treated as a stale PID file and cleaned up by ResolveVerifiedProcessAsync — we never assume
        // a live-but-unverified PID is the gateway, and we never signal it.
        var (existingProcess, existingRecord, _) = await ResolveVerifiedProcessAsync(pidFilePath);
        if (existingProcess is not null && existingRecord is not null)
        {
            _logger.LogWarning("Gateway is already running with PID {Pid}", existingRecord.Pid);
            return new GatewayStartResult(
                Success: false,
                Pid: existingRecord.Pid,
                Message: $"Gateway is already running (PID {existingRecord.Pid})");
        }

        // Spawn the process — cross-platform detached launch.
        //
        // Prefer launching the native apphost executable (e.g. BotNexus.Gateway.Api.exe) that the
        // publish step emits next to the target DLL. Doing so gives the gateway a DISTINCT process
        // name ("BotNexus.Gateway.Api") instead of the generic "dotnet". Autonomous-maintenance
        // workers spawn 15-18 build/test dotnet processes and their recovery logic force-kills
        // orphaned/hung ones by name; a name-based `Get-Process dotnet | Stop-Process` would take
        // out a `dotnet <dll>`-launched gateway as collateral (confirmed root cause of repeated
        // gateway crashes, see issue #2199). Launching the apphost makes the gateway immune to that.
        //
        // Fall back to `dotnet <dll>` when no apphost is present (framework-dependent layouts that
        // ship only the managed DLL, and cross-platform builds without a native host).
        var (launchFileName, launchArguments) = ResolveLaunchTarget(options);
        var psi = new ProcessStartInfo
        {
            FileName = launchFileName,
            Arguments = launchArguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        // Set BOTNEXUS_HOME so the gateway reads config from the correct home directory
        if (!string.IsNullOrWhiteSpace(options.HomePath))
            psi.Environment["BOTNEXUS_HOME"] = options.HomePath;

        // Enable minidump-on-crash for the spawned gateway. These DOTNET_* vars are honoured by
        // the CLR only when present in the process environment at startup, so they MUST be set on
        // the child's ProcessStartInfo here (the parent launcher) rather than from inside the
        // already-running gateway. This guarantees a dump even for a stack overflow or FailFast,
        // neither of which raises a catchable managed exception. Defensive: never break launch.
        ConfigureCrashDumps(psi, options.HomePath);

        _logger.LogInformation("Starting gateway process: {FileName} {Arguments}", psi.FileName, psi.Arguments);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is System.IO.IOException)
        {
            _logger.LogError(ex, "Failed to start gateway process");
            return new GatewayStartResult(
                Success: false,
                Pid: null,
                Message: $"Failed to start gateway: {ex.Message}");
        }

        var pid = process.Id;
        _logger.LogInformation("Gateway process started with PID {Pid}", pid);

        // Write PID file WITH process identity so a later stop/status can prove the PID is still ours.
        await WritePidAsync(pidFilePath, process);

        var healthUrl = options.HealthUrl ?? DefaultHealthUrl;
        var healthTimeout = options.ReadinessTimeout ?? TimeSpan.FromSeconds(60);
        var readinessStopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Waiting for gateway readiness: endpoint={HealthUrl}, timeout={Timeout}",
            healthUrl,
            healthTimeout);

        using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var healthTask = _healthChecker.WaitForHealthyAsync(
            healthUrl,
            healthTimeout,
            readinessCancellation.Token);
        var exitTask = process.WaitForExitAsync(readinessCancellation.Token);
        var completedTask = await Task.WhenAny(healthTask, exitTask);

        if (completedTask == exitTask || process.HasExited)
        {
            readinessCancellation.Cancel();
            await ObserveReadinessCancellationAsync(healthTask, cancellationToken);
            readinessStopwatch.Stop();
            var exitCode = process.ExitCode;
            _logger.LogWarning(
                "Gateway readiness failed: endpoint={HealthUrl}, timeout={Timeout}, elapsed={Elapsed}, finalState=process exited, exitCode={ExitCode}",
                healthUrl,
                healthTimeout,
                readinessStopwatch.Elapsed,
                exitCode);
            return new GatewayStartResult(
                Success: false,
                Pid: pid,
                Message: $"Gateway process exited during readiness after {readinessStopwatch.Elapsed.TotalSeconds:F1}s (PID {pid}, exit code {exitCode}, endpoint {healthUrl})");
        }

        var isHealthy = await healthTask;
        readinessCancellation.Cancel();
        await ObserveReadinessCancellationAsync(exitTask, cancellationToken);
        readinessStopwatch.Stop();

        if (isHealthy)
        {
            _logger.LogInformation(
                "Gateway readiness succeeded: endpoint={HealthUrl}, timeout={Timeout}, elapsed={Elapsed}, finalState=healthy and process alive, pid={Pid}",
                healthUrl,
                healthTimeout,
                readinessStopwatch.Elapsed,
                pid);
            return new GatewayStartResult(
                Success: true,
                Pid: pid,
                Message: $"Gateway started successfully (PID {pid})");
        }

        _logger.LogWarning(
            "Gateway readiness timed out: endpoint={HealthUrl}, timeout={Timeout}, elapsed={Elapsed}, finalState=process alive but not healthy",
            healthUrl,
            healthTimeout,
            readinessStopwatch.Elapsed);
        return new GatewayStartResult(
            Success: false,
            Pid: pid,
            Message: $"Gateway process is alive (PID {pid}) but not healthy after {readinessStopwatch.Elapsed.TotalSeconds:F1}s (timeout {healthTimeout.TotalSeconds:F0}s, endpoint {healthUrl}); it may still be starting");
    }

    private static async Task ObserveReadinessCancellationAsync(Task task, CancellationToken callerCancellation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            // The competing readiness operation was cancelled after a final state was established.
        }
    }

    /// <summary>
    /// Resolves the executable and argument string used to launch the gateway.
    /// <para>
    /// Prefers a native apphost executable published next to the target DLL (e.g.
    /// <c>BotNexus.Gateway.Api.exe</c>). Launching the apphost gives the gateway a distinct process
    /// name rather than the generic <c>dotnet</c>, so name-based process kills aimed at build/test
    /// <c>dotnet</c> processes cannot terminate it as collateral (issue #2199).
    /// </para>
    /// <para>
    /// Falls back to <c>dotnet &lt;dll&gt;</c> when no apphost is found next to the DLL.
    /// </para>
    /// </summary>
    internal (string FileName, string Arguments) ResolveLaunchTarget(GatewayStartOptions options)
    {
        var extraArgs = options.Arguments ?? string.Empty;

        try
        {
            var dllPath = options.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(dllPath)
                && dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // The apphost sits beside the DLL with the same base name. On Windows it carries a
                // .exe suffix; on Unix it is extension-less. Probe both so this works cross-platform.
                var dir = Path.GetDirectoryName(dllPath) ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(dllPath);
                var candidates = OperatingSystem.IsWindows()
                    ? new[] { Path.Combine(dir, baseName + ".exe") }
                    : new[] { Path.Combine(dir, baseName) };

                foreach (var apphost in candidates)
                {
                    if (File.Exists(apphost))
                    {
                        _logger.LogDebug("Launching gateway via apphost executable {Apphost}", apphost);
                        return (apphost, extraArgs.Trim());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            // Probing the filesystem must never block launch; fall through to the dotnet host.
            _logger.LogDebug(ex, "Apphost probe failed; falling back to dotnet host launch");
        }

        // Framework-dependent fallback: run the managed DLL through the shared dotnet host.
        return ("dotnet", $"\"{options.ExecutablePath}\" {extraArgs}".TrimEnd());
    }

    /// <summary>
    /// Applies the .NET crash-dump environment variables to the child gateway process so any hard
    /// exit leaves a minidump under <c>{home}/dumps</c>. Best-effort: a failure here never blocks
    /// the gateway from starting.
    /// </summary>
    private void ConfigureCrashDumps(ProcessStartInfo psi, string? homePath)
    {
        try
        {
            var home = string.IsNullOrWhiteSpace(homePath)
                ? (Environment.GetEnvironmentVariable("BOTNEXUS_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus"))
                : homePath;
            var dumpsDir = Path.Combine(home, "dumps");
            Directory.CreateDirectory(dumpsDir);
            BotNexus.Gateway.Diagnostics.CrashDumpEnvironment.Apply(
                dumpsDir,
                (key, value) => psi.Environment[key] = value);
            _logger.LogInformation("Crash dumps enabled for gateway process -> {DumpsDir}", dumpsDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure crash dumps for gateway process (continuing without minidumps)");
        }
    }

    /// <summary>
    /// Stops the gateway process by sending a hard kill signal, waiting up to 5 seconds
    /// for exit, then cleaning up the PID file.
    /// <para>
    /// The PID is only signalled after its recorded identity has been verified against the live
    /// process (issue #2369). A recycled or unverifiable PID is cleaned up and reported as
    /// not-running — it is NEVER killed.
    /// </para>
    /// </summary>
    public async Task<GatewayStopResult> StopAsync(string? homePath = null, string? gatewayBinaryPath = null, CancellationToken cancellationToken = default)
    {
        var pidFilePath = ResolvePidFilePath(homePath);
        var (verifiedProcess, record, staleReason) = await ResolveVerifiedProcessAsync(pidFilePath);

        IGatewayProcessHandle? handle = verifiedProcess is null
            ? null
            : new LiveProcessHandle(verifiedProcess, _waitForExitOverride);
        var discoveredByPath = false;
        if (handle is null)
        {
            // No usable PID file (absent, stale, or deliberately deleted by
            // ResolveVerifiedProcessAsync when identity could not be verified) - but the gateway
            // may still be very much alive and holding file locks (issue #2772). Fall back to
            // discovery by executable path.
            //
            // SECURITY (issue #2369): this does NOT weaken the never-signal-an-unverified-process
            // guarantee. That guarantee is about a bare PID being no proof of identity. Here we do
            // not trust any PID at all: we require the live process's own main-module path to equal
            // the gateway binary this deployment would launch. Path identity is STRICTLY STRONGER
            // than a recorded PID, so a foreign process can never be selected by this path.
            handle = FindProcessByBinaryPath(gatewayBinaryPath);
            discoveredByPath = handle is not null;
        }

        if (handle is null)
        {
            var reason = staleReason ?? "no PID file";
            _logger.LogInformation("Gateway is not running ({Reason})", reason);
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway is not running ({reason})",
                Outcome: GatewayStopOutcome.NotRunning);
        }

        var pid = discoveredByPath ? handle.Id : record!.Pid;
        _logger.LogInformation(
            "Killing gateway process {Pid} ({Source})", pid, discoveredByPath ? "discovered by binary path" : "from PID file");

        try
        {
            handle.Kill();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Gateway process {Pid} already exited", pid);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway process {pid} already exited",
                Outcome: GatewayStopOutcome.Stopped);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Failed to kill gateway process {Pid}", pid);
            return new GatewayStopResult(
                Success: false,
                Message: $"Failed to kill gateway process {pid}: {ex.Message}",
                Outcome: GatewayStopOutcome.Failed);
        }

        // Wait for process to exit after kill
        var timeoutMs = (int)_waitForExitTimeout.TotalMilliseconds;
        var exited = await Task.Run(() => handle.WaitForExit(timeoutMs), cancellationToken);
        if (!exited)
        {
            _logger.LogWarning("Gateway process {Pid} did not exit within {Timeout}s", pid, _waitForExitTimeout.TotalSeconds);
            // Do NOT clean up the PID file — the process is still running.
            // Returning success here would be incorrect and would allow StartAsync
            // to launch a second gateway that conflicts on the same port.
            return new GatewayStopResult(
                Success: false,
                Message: $"Gateway process {pid} did not exit within {_waitForExitTimeout.TotalSeconds}s. It may still be running.",
                Outcome: GatewayStopOutcome.Failed);
        }
        else
        {
            _logger.LogInformation("Gateway process {Pid} exited", pid);
        }

        await CleanupPidFileAsync(pidFilePath);

        return new GatewayStopResult(
            Success: true,
            Message: $"Gateway stopped (PID {pid})",
            Outcome: GatewayStopOutcome.Stopped);
    }

    /// <summary>
    /// Finds a live process whose main module path equals <paramref name="gatewayBinaryPath"/> or the
    /// apphost executable sitting beside it. Returns null when no path was supplied or nothing matches.
    /// <para>
    /// Only an EXACT (case-insensitive on Windows) full-path match counts. Any process whose module
    /// path cannot be read - a common outcome for processes owned by another user - is skipped, never
    /// assumed to be the gateway. That keeps the #2369 guarantee intact: we never signal a process we
    /// have not positively identified.
    /// </para>
    /// </summary>
    internal IGatewayProcessHandle? FindProcessByBinaryPath(string? gatewayBinaryPath)
    {
        if (string.IsNullOrWhiteSpace(gatewayBinaryPath))
            return null;

        var candidates = BuildGatewayPathCandidates(gatewayBinaryPath);

        foreach (var candidate in _processEnumerator())
        {
            string? modulePath;
            try
            {
                modulePath = candidate.ExecutablePath;
            }
            catch
            {
                // Access denied / exited between enumeration and inspection: unidentifiable,
                // therefore never a stop target.
                continue;
            }

            if (string.IsNullOrWhiteSpace(modulePath))
                continue;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(modulePath);
            }
            catch
            {
                continue;
            }

            foreach (var expected in candidates)
            {
                if (string.Equals(resolved, expected, PathComparison))
                {
                    _logger.LogInformation(
                        "Discovered live gateway process {Pid} by binary path {Path} (no usable PID file)",
                        candidate.Id,
                        modulePath);
                    return candidate;
                }
            }
        }

        return null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The set of executable paths that count as "running the gateway binary": the managed DLL
    /// itself, and the native apphost emitted next to it (which is what <c>StartAsync</c> prefers).
    /// </summary>
    internal static IReadOnlyList<string> BuildGatewayPathCandidates(string gatewayBinaryPath)
    {
        var full = Path.GetFullPath(gatewayBinaryPath);
        var directory = Path.GetDirectoryName(full);
        var stem = Path.GetFileNameWithoutExtension(full);
        var list = new List<string> { full };
        if (directory is not null && !string.IsNullOrEmpty(stem))
        {
            list.Add(Path.Combine(directory, stem + ".exe"));
            list.Add(Path.Combine(directory, stem));
        }
        return list;
    }

    /// <summary>
    /// Queries the current status of the gateway by reading the PID file, verifying that the recorded
    /// process identity still matches the live process, and computing uptime. A PID that has been
    /// recycled onto a foreign process, or a legacy PID file with no identity, reports NotRunning
    /// rather than falsely claiming the gateway is alive (issue #2369).
    /// </summary>
    public async Task<GatewayStatus> GetStatusAsync(string? homePath = null, CancellationToken cancellationToken = default)
    {
        var pidFilePath = ResolvePidFilePath(homePath);
        var (process, record, staleReason) = await ResolveVerifiedProcessAsync(pidFilePath);

        if (record is null)
        {
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: "No PID file found");
        }

        if (process is null)
        {
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: staleReason ?? $"Process {record.Pid} is not the gateway (cleaned stale PID)");
        }

        // Uptime comes from the verified identity record, which is by definition the real start time.
        TimeSpan? uptime = record.StartTimeUtc is null
            ? null
            : DateTime.UtcNow - record.StartTimeUtc.Value;

        // Probe the gateway HTTP endpoint to distinguish running+authenticated vs
        // running+no-auth (returns 401/403) vs running+unreachable (wrong port/not bound).
        var probeResult = await ProbeGatewayAsync(DefaultHealthUrl, CancellationToken.None);

        var message = probeResult switch
        {
            GatewayProbeResult.Healthy => uptime.HasValue
                ? $"Running for {uptime.Value:hh\\:mm\\:ss}"
                : "Running (uptime unknown)",
            GatewayProbeResult.ReachableNoAuth =>
                "Running but authentication is not configured or token is invalid (HTTP 401/403)",
            GatewayProbeResult.Unreachable =>
                "Running (process alive) but HTTP endpoint is not reachable at the default port",
            _ => "Running (probe inconclusive)"
        };

        return new GatewayStatus(
            State: GatewayState.Running,
            Pid: record.Pid,
            Uptime: uptime,
            Message: message,
            ProbeResult: probeResult);
    }

    /// <summary>
    /// Probes the gateway HTTP health endpoint and classifies the response.
    /// Returns <see cref="GatewayProbeResult.Healthy"/> on 2xx,
    /// <see cref="GatewayProbeResult.ReachableNoAuth"/> on 401/403,
    /// and <see cref="GatewayProbeResult.Unreachable"/> on connection failure or timeout.
    /// </summary>
    internal async Task<GatewayProbeResult> ProbeGatewayAsync(string healthUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _probeClient.GetAsync(healthUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
                return GatewayProbeResult.Healthy;

            var status = (int)response.StatusCode;
            if (status == 401 || status == 403)
            {
                _logger.LogDebug("Gateway health probe returned {StatusCode} -- auth not configured", status);
                return GatewayProbeResult.ReachableNoAuth;
            }

            _logger.LogDebug("Gateway health probe returned unexpected status {StatusCode}", status);
            return GatewayProbeResult.Healthy; // reachable, treat as healthy for status purposes
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("Gateway health probe connection failed: {Message}", ex.Message);
            return GatewayProbeResult.Unreachable;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Gateway health probe timed out");
            return GatewayProbeResult.Unreachable;
        }
    }

    /// <summary>
    /// Resolves the live gateway process for a PID file, verifying that the PID still belongs to the
    /// exact process we recorded when we started it.
    /// <para>
    /// Security (issue #2369): a PID alone is not proof of identity. The OS recycles PIDs, so a stale
    /// <c>gateway.pid</c> left by a crash, power loss or container restart can name an unrelated
    /// process owned by the same user. Every caller that might kill or report on that PID goes
    /// through here, and anything short of a positive identity match is treated as "stale, not
    /// running": the PID file is removed and no signal is ever sent.
    /// </para>
    /// <para>
    /// Legacy bare-PID files (just <c>1234</c>) carry no identity and are therefore deliberately
    /// treated as unverifiable — never killed. The worst case is one manual gateway restart after
    /// upgrading; the alternative is terminating a stranger's process.
    /// </para>
    /// </summary>
    /// <returns>
    /// The live, positively-identified process, the parsed record (null when there was no PID file),
    /// and a human-readable reason when the PID file was considered stale.
    /// </returns>
    private async Task<(Process? Process, GatewayPidRecord? Record, string? StaleReason)> ResolveVerifiedProcessAsync(string pidFilePath)
    {
        var record = await ReadPidRecordAsync(pidFilePath);
        if (record is null)
            return (null, null, null);

        Process process;
        try
        {
            process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
                throw new InvalidOperationException("Process has exited.");
        }
        catch
        {
            _logger.LogDebug("Gateway process {Pid} no longer exists (cleaning stale PID)", record.Pid);
            await CleanupPidFileAsync(pidFilePath);
            return (null, record, $"process {record.Pid} no longer exists (cleaned stale PID)");
        }

        var verification = GatewayPidFile.Verify(record, process);
        switch (verification)
        {
            case GatewayIdentityMatch.Match:
                return (process, record, null);

            case GatewayIdentityMatch.Mismatch:
                // The PID is alive but is NOT our gateway. Never signal it.
                _logger.LogWarning(
                    "PID {Pid} was recycled onto a different process; refusing to signal it and cleaning the stale PID file",
                    record.Pid);
                await CleanupPidFileAsync(pidFilePath);
                return (null, record, $"PID {record.Pid} was recycled onto a different process (cleaned stale PID)");

            default:
                // Legacy bare-PID file, or the OS would not disclose the live process identity.
                _logger.LogWarning(
                    "PID file for {Pid} carries no verifiable process identity; refusing to signal it and cleaning the unverifiable PID file",
                    record.Pid);
                await CleanupPidFileAsync(pidFilePath);
                return (null, record, $"PID {record.Pid} could not be verified as the gateway (cleaned unverifiable stale PID)");
        }
    }

    /// <summary>
    /// Reads and parses the PID file. Accepts both the identity-bearing JSON form and the legacy
    /// bare-PID form. Returns null when the file is missing or its contents are unusable.
    /// </summary>
    private async Task<GatewayPidRecord?> ReadPidRecordAsync(string pidFilePath)
    {
        if (!File.Exists(pidFilePath))
            return null;

        try
        {
            var content = await File.ReadAllTextAsync(pidFilePath);
            if (GatewayPidFile.TryParse(content, out var record) && record is not null)
                return record;

            _logger.LogWarning("PID file contains invalid data: {Content}", content);
            await CleanupPidFileAsync(pidFilePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read PID file at {Path}", pidFilePath);
            return null;
        }
    }

    /// <summary>
    /// Writes the PID file with the spawned process's identity (PID, start time, process name and
    /// main module path), creating the directory if necessary. The identity is what later allows
    /// stop/status to prove the PID has not been recycled.
    /// </summary>
    private async Task WritePidAsync(string pidFilePath, Process process)
    {
        var directory = Path.GetDirectoryName(pidFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Created directory {Directory}", directory);
        }

        var record = GatewayPidFile.Capture(process);
        await File.WriteAllTextAsync(pidFilePath, GatewayPidFile.Serialize(record));
        _logger.LogDebug("Wrote PID {Pid} (with process identity) to {Path}", record.Pid, pidFilePath);
    }

    /// <summary>
    /// Deletes the PID file if it exists.
    /// </summary>
    private async Task CleanupPidFileAsync(string pidFilePath)
    {
        if (File.Exists(pidFilePath))
        {
            try
            {
                File.Delete(pidFilePath);
                _logger.LogDebug("Deleted PID file at {Path}", pidFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete PID file at {Path}", pidFilePath);
            }
        }

        await Task.CompletedTask;
    }
}
