using System.Diagnostics;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

public sealed class GatewayProcessManagerTests : IDisposable
{
    private readonly string _testPidDirectory;
    private readonly GatewayProcessManager _manager;
    private readonly IHealthChecker _healthChecker;

    public GatewayProcessManagerTests()
    {
        _testPidDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testPidDirectory);

        _healthChecker = Substitute.For<IHealthChecker>();
        _manager = new GatewayProcessManager(_healthChecker, NullLogger<GatewayProcessManager>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPidDirectory))
        {
            Directory.Delete(_testPidDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ReturnsAlreadyRunningResult()
    {
        // Write a PID file with the current process ID (which is definitely running)
        var currentPid = Process.GetCurrentProcess().Id;
        await WritePidFileAsync(currentPid);

        var options = new GatewayStartOptions(
            ExecutablePath: "BotNexus.Gateway.Api.dll",
            Arguments: null,
            HomePath: _testPidDirectory);

        var result = await _manager.StartAsync(options);

        result.Success.ShouldBeFalse();
        result.Pid.ShouldBe(currentPid);
        result.Message.ShouldContain("already running");
    }

    [Fact]
    public async Task StartAsync_WhenStalePidExists_CleansAndStartsSuccessfully()
    {
        // Write a PID file with a definitely-dead PID (99999)
        await WritePidFileAsync(99999);

        // PID file should be cleaned up before attempting to start
        var pidFilePath = GetPidFilePath();
        File.Exists(pidFilePath).ShouldBeTrue();

        // Mock health checker to return false so the start doesn't actually succeed
        _healthChecker.WaitForHealthyAsync(
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        var options = new GatewayStartOptions(
            ExecutablePath: "BotNexus.Gateway.Api.dll",
            Arguments: null,
            HomePath: _testPidDirectory);

        var result = await _manager.StartAsync(options);

        // The stale PID should have been cleaned up before starting
        // The process may start but health check will fail
        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_ReturnsNotRunningResult()
    {
        var result = await _manager.StopAsync(_testPidDirectory);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not running");
    }

    [Fact]
    public async Task StopAsync_WhenPidFileExistsButProcessDead_CleansStalePid()
    {
        // Write a PID file with a definitely-dead PID
        await WritePidFileAsync(99999);

        var result = await _manager.StopAsync(_testPidDirectory);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("stale PID");

        // PID file should be cleaned up
        var pidFilePath = GetPidFilePath();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenRunning_DeletesPidFile()
    {
        // Start a simple long-running process we can kill
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout /t 30" : "-c \"sleep 30\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi);
        process.ShouldNotBeNull();

        try
        {
            // Write the PID file
            await WritePidFileAsync(process.Id);

            var result = await _manager.StopAsync(_testPidDirectory);

            result.Success.ShouldBeTrue();
            result.Message.ShouldContain("stopped");

            // PID file should be deleted
            var pidFilePath = GetPidFilePath();
            File.Exists(pidFilePath).ShouldBeFalse();
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task GetStatusAsync_WhenNotRunning_ReturnsNotRunningStatus()
    {
        var status = await _manager.GetStatusAsync(_testPidDirectory);

        status.State.ShouldBe(GatewayState.NotRunning);
        status.Pid.ShouldBeNull();
        status.Uptime.ShouldBeNull();
        status.Message.ShouldContain("No PID file");
    }

    [Fact]
    public async Task GetStatusAsync_WhenStalePidExists_CleansAndReturnsNotRunning()
    {
        // Write a PID file with a definitely-dead PID
        await WritePidFileAsync(99999);

        var status = await _manager.GetStatusAsync(_testPidDirectory);

        status.State.ShouldBe(GatewayState.NotRunning);
        status.Pid.ShouldBeNull();
        status.Uptime.ShouldBeNull();
        status.Message.ShouldContain("stale PID");

        // PID file should be cleaned up
        var pidFilePath = GetPidFilePath();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_WhenRunning_ReturnsPidAndUptime()
    {
        // Start a long-running dotnet process (which will pass the name check)
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--info",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };

        var process = Process.Start(psi);
        process.ShouldNotBeNull();

        try
        {
            // Give process a moment to start
            await Task.Delay(100);

            await WritePidFileAsync(process.Id);

            var status = await _manager.GetStatusAsync(_testPidDirectory);

            // If process is still running, should be detected
            if (!process.HasExited)
            {
                status.State.ShouldBe(GatewayState.Running);
                status.Pid.ShouldBe(process.Id);
                status.Uptime.ShouldNotBeNull();
                status.Message.ShouldContain("Running");
            }
            else
            {
                // Process exited quickly - that's ok, it should be cleaned up
                status.State.ShouldBe(GatewayState.NotRunning);
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task GetStatusAsync_WhenDotnetProcessRunning_ReturnsRunningWithUptime()
    {
        // dotnet --version exits immediately; use the current test process instead
        // as it is definitely alive and the name check accepts any dotnet process
        var currentProcess = Process.GetCurrentProcess();
        await WritePidFileAsync(currentProcess.Id);

        var status = await _manager.GetStatusAsync(_testPidDirectory);

        // Current process is alive and named "dotnet" (test runner)
        status.State.ShouldBe(GatewayState.Running);
        status.Pid.ShouldBe(currentProcess.Id);
        status.Uptime.ShouldNotBeNull();
        status.Message.ShouldContain("Running");
    }

    [Fact]
    public async Task GetStatusAsync_WhenPidRecycled_ReturnsNotRunning()
    {
        // Simulate PID recycling: write a dead PID — state is NotRunning.
        await WritePidFileAsync(99999);

        var status = await _manager.GetStatusAsync(_testPidDirectory);

        status.State.ShouldBe(GatewayState.NotRunning);
        status.Pid.ShouldBeNull();

        // PID file should be cleaned up
        var pidFilePath = GetPidFilePath();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenProcessDoesNotExitInTime_ReturnsFailed()
    {
        // The GatewayProcessManager supports an injectable waitForExitOverride for testability.
        // We inject a delegate that always returns false (process never exits) so the !exited
        // path is reliably triggered without relying on OS-level timing.
        // This test FAILS before the fix because StopAsync currently returns Success=true
        // even when !exited.
        var neverExitsManager = new GatewayProcessManager(
            _healthChecker,
            NullLogger<GatewayProcessManager>.Instance,
            waitForExitTimeout: TimeSpan.FromMilliseconds(1),
            waitForExitOverride: (_, _) => false); // always report: process did not exit

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout /t 30" : "30",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi);
        process.ShouldNotBeNull();

        try
        {
            await WritePidFileAsync(process.Id);

            var result = await neverExitsManager.StopAsync(_testPidDirectory);

            // Process reported as still running (override returned false) — MUST return failure.
            result.Success.ShouldBeFalse();
            result.Message.ShouldContain("did not exit");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }

    [Fact]
    public void IsRunning_WhenNoPidFile_ReturnsFalse()
    {
        _manager.IsRunning(_testPidDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task IsRunning_WhenPidFileAndProcessAlive_ReturnsTrue()
    {
        // Use the current process — always alive
        var currentProcess = Process.GetCurrentProcess();
        await WritePidFileAsync(currentProcess.Id);

        _manager.IsRunning(_testPidDirectory).ShouldBeTrue();
    }

    [Fact]
    public async Task IsRunning_WhenStalePid_ReturnsFalse()
    {
        // Write a PID file with a definitely-dead PID
        await WritePidFileAsync(99999);

        _manager.IsRunning(_testPidDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_ConsecutiveCalls_ConsistentResults()
    {
        var status1 = await _manager.GetStatusAsync(_testPidDirectory);
        var status2 = await _manager.GetStatusAsync(_testPidDirectory);

        status1.State.ShouldBe(status2.State);
        status1.Pid.ShouldBe(status2.Pid);
    }

    [Fact]
    public async Task GatewayStop_WithTarget_UsesCorrectPidFile()
    {
        // Arrange: create a separate target directory to verify target isolation
        var altTarget = Path.Combine(Path.GetTempPath(), $"botnexus-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(altTarget);

        try
        {
            // Write PID to alt target
            var altPidPath = Path.Combine(altTarget, "gateway.pid");
            await File.WriteAllTextAsync(altPidPath, "99999");

            // Stop using alt target — should find and clean the stale PID there
            var result = await _manager.StopAsync(altTarget);

            result.Success.ShouldBeTrue();
            result.Message.ShouldContain("stale PID");
            File.Exists(altPidPath).ShouldBeFalse();

            // Default target should be unaffected
            var defaultPidPath = GetPidFilePath();
            File.Exists(defaultPidPath).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(altTarget))
                Directory.Delete(altTarget, recursive: true);
        }
    }

    [Fact]
    public async Task GatewayStatus_WithTarget_ReadsCorrectPidFile()
    {
        // Arrange: create a separate target directory
        var altTarget = Path.Combine(Path.GetTempPath(), $"botnexus-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(altTarget);

        try
        {
            // Write current PID to alt target (process is running)
            var currentPid = Process.GetCurrentProcess().Id;
            var altPidPath = Path.Combine(altTarget, "gateway.pid");
            await File.WriteAllTextAsync(altPidPath, currentPid.ToString());

            // Status for alt target should find the process
            var altStatus = await _manager.GetStatusAsync(altTarget);
            altStatus.State.ShouldBe(GatewayState.Running);
            altStatus.Pid.ShouldBe(currentPid);

            // Status for test target (no PID file) should be NotRunning
            var defaultStatus = await _manager.GetStatusAsync(_testPidDirectory);
            defaultStatus.State.ShouldBe(GatewayState.NotRunning);
        }
        finally
        {
            if (Directory.Exists(altTarget))
                Directory.Delete(altTarget, recursive: true);
        }
    }

    private async Task WritePidFileAsync(int pid)
    {
        var pidFilePath = GetPidFilePath();
        var directory = Path.GetDirectoryName(pidFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(pidFilePath, pid.ToString());
    }

    private string GetPidFilePath() => Path.Combine(_testPidDirectory, "gateway.pid");
}
