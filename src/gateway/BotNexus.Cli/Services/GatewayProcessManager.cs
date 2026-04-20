using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cli.Services;

/// <summary>
/// Manages the lifecycle of the BotNexus Gateway process, including PID file tracking,
/// process spawning (detached or attached), health checking, and cleanup.
/// Windows-only for v1.
/// </summary>
public sealed class GatewayProcessManager : IGatewayProcessManager
{
    private readonly IHealthChecker _healthChecker;
    private readonly ILogger<GatewayProcessManager> _logger;
    private readonly string _pidFilePath;

    public GatewayProcessManager(IHealthChecker healthChecker, ILogger<GatewayProcessManager> logger)
    {
        _healthChecker = healthChecker;
        _logger = logger;

        var botnexusHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".botnexus");

        _pidFilePath = Path.Combine(botnexusHome, "gateway.pid");
    }

    /// <summary>
    /// Checks whether the gateway process is currently running by reading the PID file
    /// and verifying the process exists.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            var pid = ReadPidAsync().GetAwaiter().GetResult();
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
    }

    /// <summary>
    /// Starts the gateway process in detached or attached mode, writes the PID file,
    /// and waits for the health endpoint to become responsive.
    /// </summary>
    public async Task<GatewayStartResult> StartAsync(GatewayStartOptions options, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogError("Gateway process manager is Windows-only for v1");
            return new GatewayStartResult(
                Success: false,
                Pid: null,
                Message: "Gateway process manager is Windows-only for v1. Run the gateway manually with 'dotnet run'.");
        }

        // Check if already running
        var existingPid = await ReadPidAsync();
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
                // Process no longer exists - clean up stale PID
                _logger.LogDebug("Cleaning up stale PID {Pid}", existingPid.Value);
                await CleanupPidFileAsync();
            }
        }

        // Spawn the process
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{options.ExecutablePath}\" {options.Arguments ?? ""}".TrimEnd(),
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        _logger.LogInformation("Starting gateway process: {FileName} {Arguments}", psi.FileName, psi.Arguments);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Win32Exception ex)
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
        await WritePidAsync(pid);

        // Perform health check
        // Default health URL is http://localhost:5005/health - gateway uses this by default
        var healthUrl = "http://localhost:5005/health";
        var healthTimeout = TimeSpan.FromSeconds(10);

        _logger.LogInformation("Waiting for gateway to become healthy at {HealthUrl}...", healthUrl);

        var isHealthy = await _healthChecker.WaitForHealthyAsync(healthUrl, healthTimeout, cancellationToken);

        if (isHealthy)
        {
            _logger.LogInformation("Gateway is healthy (PID {Pid})", pid);
            return new GatewayStartResult(
                Success: true,
                Pid: pid,
                Message: $"Gateway started successfully (PID {pid})");
        }
        else
        {
            _logger.LogWarning("Gateway did not become healthy within {Timeout}s (PID {Pid})", healthTimeout.TotalSeconds, pid);
            return new GatewayStartResult(
                Success: false,
                Pid: pid,
                Message: $"Gateway started (PID {pid}) but did not become healthy within {healthTimeout.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Stops the gateway process by sending a hard kill signal, waiting up to 5 seconds
    /// for exit, then cleaning up the PID file.
    /// </summary>
    public async Task<GatewayStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var pid = await ReadPidAsync();
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
            await CleanupPidFileAsync();
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway was not running (cleaned stale PID {pid.Value})");
        }

        if (process.HasExited)
        {
            _logger.LogInformation("Gateway process {Pid} has already exited (cleaned stale PID)", pid.Value);
            await CleanupPidFileAsync();
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway was not running (cleaned stale PID {pid.Value})");
        }

        _logger.LogInformation("Killing gateway process {Pid}", pid.Value);

        try
        {
            process.Kill();
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Failed to kill gateway process {Pid}", pid.Value);
            return new GatewayStopResult(
                Success: false,
                Message: $"Failed to kill gateway process {pid.Value}: {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning("Gateway process {Pid} already exited", pid.Value);
            await CleanupPidFileAsync();
            return new GatewayStopResult(
                Success: true,
                Message: $"Gateway process {pid.Value} already exited");
        }

        // Wait up to 5 seconds for exit
        var exited = await Task.Run(() => process.WaitForExit(5000), cancellationToken);
        if (!exited)
        {
            _logger.LogWarning("Gateway process {Pid} did not exit within 5 seconds", pid.Value);
        }
        else
        {
            _logger.LogInformation("Gateway process {Pid} exited", pid.Value);
        }

        await CleanupPidFileAsync();

        return new GatewayStopResult(
            Success: true,
            Message: $"Gateway stopped (PID {pid.Value})");
    }

    /// <summary>
    /// Queries the current status of the gateway by reading the PID file,
    /// checking if the process is alive, and computing uptime.
    /// </summary>
    public async Task<GatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var pid = await ReadPidAsync();
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
            await CleanupPidFileAsync();
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: $"Process {pid.Value} no longer exists (cleaned stale PID)");
        }

        if (process.HasExited)
        {
            _logger.LogDebug("Gateway process {Pid} has exited (cleaning stale PID)", pid.Value);
            await CleanupPidFileAsync();
            return new GatewayStatus(
                State: GatewayState.NotRunning,
                Pid: null,
                Uptime: null,
                Message: $"Process {pid.Value} has exited (cleaned stale PID)");
        }

        // Guard against PID recycling: check process name contains "BotNexus" or "dotnet"
        string processName;
        try
        {
            processName = process.ProcessName;
        }
        catch (Win32Exception)
        {
            _logger.LogWarning("Cannot read process name for PID {Pid}", pid.Value);
            return new GatewayStatus(
                State: GatewayState.Unknown,
                Pid: pid.Value,
                Uptime: null,
                Message: $"Process {pid.Value} exists but name cannot be read");
        }

        if (!processName.Contains("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !processName.Contains("BotNexus", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PID {Pid} recycled (process name: {ProcessName}), cleaning stale PID", pid.Value, processName);
            await CleanupPidFileAsync();
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
        catch (Win32Exception)
        {
            _logger.LogDebug("Cannot read start time for PID {Pid}", pid.Value);
        }

        return new GatewayStatus(
            State: GatewayState.Running,
            Pid: pid.Value,
            Uptime: uptime,
            Message: uptime.HasValue
                ? $"Running for {uptime.Value:hh\\:mm\\:ss}"
                : "Running (uptime unknown)");
    }

    /// <summary>
    /// Reads the PID file and returns the PID if valid, or null if the file doesn't exist
    /// or contains invalid data. Automatically cleans up stale PIDs.
    /// </summary>
    private async Task<int?> ReadPidAsync()
    {
        if (!File.Exists(_pidFilePath))
            return null;

        try
        {
            var content = await File.ReadAllTextAsync(_pidFilePath);
            if (int.TryParse(content.Trim(), out var pid) && pid > 0)
                return pid;

            _logger.LogWarning("PID file contains invalid data: {Content}", content);
            await CleanupPidFileAsync();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read PID file at {Path}", _pidFilePath);
            return null;
        }
    }

    /// <summary>
    /// Writes the PID to the PID file, creating the directory if necessary.
    /// </summary>
    private async Task WritePidAsync(int pid)
    {
        var directory = Path.GetDirectoryName(_pidFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Created directory {Directory}", directory);
        }

        await File.WriteAllTextAsync(_pidFilePath, pid.ToString());
        _logger.LogDebug("Wrote PID {Pid} to {Path}", pid, _pidFilePath);
    }

    /// <summary>
    /// Deletes the PID file if it exists.
    /// </summary>
    private async Task CleanupPidFileAsync()
    {
        if (File.Exists(_pidFilePath))
        {
            try
            {
                File.Delete(_pidFilePath);
                _logger.LogDebug("Deleted PID file at {Path}", _pidFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete PID file at {Path}", _pidFilePath);
            }
        }

        await Task.CompletedTask;
    }
}
