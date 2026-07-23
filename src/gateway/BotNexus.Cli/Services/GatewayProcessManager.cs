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
    // Default health URL used for status probing when no override is provided.
    internal const string DefaultHealthUrl = "http://localhost:5005/health";

    public GatewayProcessManager(
        IHealthChecker healthChecker,
        ILogger<GatewayProcessManager> logger,
        TimeSpan? waitForExitTimeout = null,
        Func<Process, int, bool>? waitForExitOverride = null,
        HttpClient? probeClient = null)
    {
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
    /// Checks whether the gateway process is currently running by reading the PID file
    /// and verifying the process exists.
    /// </summary>
    public bool IsRunning(string? homePath = null)
    {
        var pid = ReadPidAsync(ResolvePidFilePath(homePath)).GetAwaiter().GetResult();
        if (pid is null)
            return false;

        try
        {
            var process = Process.GetProcessById(pid.Value);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the gateway process in detached or attached mode, writes the PID file,
    /// and waits for the health endpoint to become responsive.
    /// </summary>
    public async Task<GatewayStartResult> StartAsync(GatewayStartOptions options, CancellationToken cancellationToken = default)
    {
        var pidFilePath = ResolvePidFilePath(options.HomePath);
        // Check if already running
        var existingPid = await ReadPidAsync(pidFilePath);
        if (existingPid is not null)
        {
            try
            {
                var existingProcess = Process.GetProcessById(existingPid.Value);
                if (!existingProcess.HasExited)
                {
                    _logger.LogWarning("Gateway is already running with PID {Pid}", existingPid.Value);
                    return new GatewayStartResult(
                        Success: false,
                        Pid: existingPid.Value,
                        Message: $"Gateway is already running (PID {existingPid.Value})");
                }
            }
            catch
            {
                // Process no longer exists (ArgumentException from GetProcessById) - clean up stale PID.
                // This happens when the gateway crashed without cleaning up its PID file, or when the
                // system has rebooted since the gateway last ran.
                _logger.LogDebug("Cleaning up stale PID {Pid}", existingPid.Value);
                await CleanupPidFileAsync(pidFilePath);
            }
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

        // Write PID file
        await WritePidAsync(pidFilePath, pid);

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
    /// </summary>
    public async Task<GatewayStopResult> StopAsync(string? homePath = null, CancellationToken cancellationToken = default)
    {
        var pidFilePath = ResolvePidFilePath(homePath);
        var pid = await ReadPidAsync(pidFilePath);
        if (pid is null)
        {
            _logger.LogInformation("Gateway is not running (no PID file)");
            return new GatewayStopResult(
                Success: true,
                Message: "Gateway is not running");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid.Value);
        }
        catch
        {
            _logger.LogInformation("Gateway process {Pid} no longer exists (cleaned stale PID)", pid.Value);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway was not running (cleaned stale PID {pid.Value})");
        }

        if (process.HasExited)
        {
            _logger.LogInformation("Gateway process {Pid} has already exited (cleaned stale PID)", pid.Value);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway was not running (cleaned stale PID {pid.Value})");
        }

        _logger.LogInformation("Killing gateway process {Pid}", pid.Value);

        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to kill gateway process {Pid}", pid.Value);
            return new GatewayStopResult(
                Success: false,
                Message: $"Failed to kill gateway process {pid.Value}: {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning("Gateway process {Pid} already exited", pid.Value);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway process {pid.Value} already exited");
        }

        // Wait for process to exit after kill
        var timeoutMs = (int)_waitForExitTimeout.TotalMilliseconds;
        var exited = _waitForExitOverride is not null
            ? _waitForExitOverride(process, timeoutMs)
            : await Task.Run(() => process.WaitForExit(timeoutMs), cancellationToken);
        if (!exited)
        {
            _logger.LogWarning("Gateway process {Pid} did not exit within {Timeout}s", pid.Value, _waitForExitTimeout.TotalSeconds);
            // Do NOT clean up the PID file — the process is still running.
            // Returning success here would be incorrect and would allow StartAsync
            // to launch a second gateway that conflicts on the same port.
            return new GatewayStopResult(
                Success: false,
                Message: $"Gateway process {pid.Value} did not exit within {_waitForExitTimeout.TotalSeconds}s. It may still be running.");
        }
        else
        {
            _logger.LogInformation("Gateway process {Pid} exited", pid.Value);
        }

        await CleanupPidFileAsync(pidFilePath);

        return new GatewayStopResult(
            Success: true,
            Message: $"Gateway stopped (PID {pid.Value})");
    }

    /// <summary>
    /// Queries the current status of the gateway by reading the PID file,
    /// checking if the process is alive, and computing uptime.
    /// </summary>
    public async Task<GatewayStatus> GetStatusAsync(string? homePath = null, CancellationToken cancellationToken = default)
    {
        var pidFilePath = ResolvePidFilePath(homePath);
        var pid = await ReadPidAsync(pidFilePath);
        if (pid is null)
        {
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: "No PID file found");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid.Value);
        }
        catch
        {
            _logger.LogDebug("Gateway process {Pid} no longer exists (cleaning stale PID)", pid.Value);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: $"Process {pid.Value} no longer exists (cleaned stale PID)");
        }

        if (process.HasExited)
        {
            _logger.LogDebug("Gateway process {Pid} has exited (cleaning stale PID)", pid.Value);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: $"Process {pid.Value} has exited (cleaned stale PID)");
        }

        // Guard against PID recycling: Windows may reuse a PID for an unrelated process after the
        // original process exits. Without this check, we could incorrectly report "gateway running"
        // when the PID now belongs to, say, notepad.exe. The gateway may run as:
        //   - Self-contained executable: process name contains "BotNexus"
        //   - Framework-dependent app: process name is "dotnet", main module path contains "BotNexus"
        // We verify using both process name and main module path for maximum robustness.
        string processName;
        try
        {
            processName = process.ProcessName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            _logger.LogWarning("Cannot read process name for PID {Pid}", pid.Value);
            return new GatewayStatus(
                State: GatewayState.Unknown,
                Pid: pid.Value,
                Uptime: null,
                Message: $"Process {pid.Value} exists but name cannot be read");
        }

        // Check if this is actually our gateway process (PID recycling guard)
        // The gateway may run as a self-contained exe (process name contains "BotNexus")
        // or as a framework-dependent app (process name is "dotnet", but main module path contains "BotNexus")
        bool isGatewayProcess = processName.Contains("BotNexus", StringComparison.OrdinalIgnoreCase);
        if (!isGatewayProcess)
        {
            try
            {
                var mainModulePath = process.MainModule?.FileName ?? string.Empty;
                // For framework-dependent apps, check if the dotnet host is running a BotNexus assembly
                // We can't easily check the arguments, so accept any dotnet process that we intentionally started
                // The PID file itself is the primary guard — we only write it when we start the process
                isGatewayProcess = processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    || mainModulePath.Contains("BotNexus", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                // Can't read MainModule (e.g. 32-bit process on 64-bit OS, or insufficient permissions)
                // Trust the PID file if we can't verify — false positives are better than false negatives
                // since we only write the PID file when we spawn the gateway ourselves
                isGatewayProcess = processName.Contains("dotnet", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!isGatewayProcess)
        {
            _logger.LogWarning("PID {Pid} recycled (process name: {ProcessName}), cleaning stale PID", pid.Value, processName);
            await CleanupPidFileAsync(pidFilePath);
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: $"PID {pid.Value} recycled (process is '{processName}', not gateway)");
        }

        TimeSpan? uptime = null;
        try
        {
            uptime = DateTime.Now - process.StartTime;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            _logger.LogDebug("Cannot read start time for PID {Pid}", pid.Value);
        }

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
            Pid: pid.Value,
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
    /// Reads the PID file and returns the PID if valid, or null if the file doesn't exist
    /// or contains invalid data. Automatically cleans up stale PIDs.
    /// </summary>
    private async Task<int?> ReadPidAsync(string pidFilePath)
    {
        if (!File.Exists(pidFilePath))
            return null;

        try
        {
            var content = await File.ReadAllTextAsync(pidFilePath);
            if (int.TryParse(content.Trim(), out var pid) && pid > 0)
                return pid;

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
    /// Writes the PID to the PID file, creating the directory if necessary.
    /// </summary>
    private async Task WritePidAsync(string pidFilePath, int pid)
    {
        var directory = Path.GetDirectoryName(pidFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Created directory {Directory}", directory);
        }

        await File.WriteAllTextAsync(pidFilePath, pid.ToString());
        _logger.LogDebug("Wrote PID {Pid} to {Path}", pid, pidFilePath);
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
