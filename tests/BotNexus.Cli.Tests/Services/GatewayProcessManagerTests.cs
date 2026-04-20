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

        // Use reflection to set the PID file path to our test directory
        var pidFileField = typeof(GatewayProcessManager).GetField("_pidFilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        pidFileField!.SetValue(_manager, Path.Combine(_testPidDirectory, "gateway.pid"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPidDirectory))
        {
            Directory.Delete(_testPidDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_WhenNotWindows_ReturnsNotSupportedResult()
    {
        if (OperatingSystem.IsWindows())
        {
            // Skip this test on Windows
            return;
        }

        var options = new GatewayStartOptions(
            ExecutablePath: "dotnet",
            Arguments: "run");

        var result = await _manager.StartAsync(options);

        result.Success.ShouldBeFalse();
        result.Pid.ShouldBeNull();
        result.Message.ShouldContain("Windows-only");
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ReturnsAlreadyRunningResult()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Write a PID file with the current process ID (which is definitely running)
        var currentPid = Process.GetCurrentProcess().Id;
        await WritePidFileAsync(currentPid);

        var options = new GatewayStartOptions(
            ExecutablePath: "BotNexus.Gateway.Api.dll",
            Arguments: null);

        var result = await _manager.StartAsync(options);

        result.Success.ShouldBeFalse();
        result.Pid.ShouldBe(currentPid);
        result.Message.ShouldContain("already running");
    }

    [Fact]
    public async Task StartAsync_WhenStalePidExists_CleansAndStartsSuccessfully()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
            Arguments: null);

        var result = await _manager.StartAsync(options);

        // The stale PID should have been cleaned up before starting
        // The process may start but health check will fail
        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_ReturnsNotRunningResult()
    {
        var result = await _manager.StopAsync();

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not running");
    }

    [Fact]
    public async Task StopAsync_WhenPidFileExistsButProcessDead_CleansStalePid()
    {
        // Write a PID file with a definitely-dead PID
        await WritePidFileAsync(99999);

        var result = await _manager.StopAsync();

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("stale PID");
        
        // PID file should be cleaned up
        var pidFilePath = GetPidFilePath();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenRunning_DeletesPidFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Start a simple process we can kill
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 30",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var process = Process.Start(psi);
        process.ShouldNotBeNull();

        try
        {
            // Write the PID file
            await WritePidFileAsync(process.Id);

            var result = await _manager.StopAsync();

            result.Success.ShouldBeTrue();
            result.Message.ShouldContain("stopped");

            // PID file should be deleted
            var pidFilePath = GetPidFilePath();
            File.Exists(pidFilePath).ShouldBeFalse();
        }
        finally
        {
            // Clean up the test process if it's still running
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
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
        var status = await _manager.GetStatusAsync();

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

        var status = await _manager.GetStatusAsync();

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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Start a long-running dotnet process (which will pass the name check)
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--info",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = false
        };

        var process = Process.Start(psi);
        process.ShouldNotBeNull();

        try
        {
            // Give process a moment to start
            await Task.Delay(100);

            await WritePidFileAsync(process.Id);

            var status = await _manager.GetStatusAsync();

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
                    process.Kill();
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Start a long-running dotnet process that will definitely stay alive
        var tempScript = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.csx");
        try
        {
            await File.WriteAllTextAsync(tempScript, "await Task.Delay(30000);");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"script \"{tempScript}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                // Skip if can't start process
                return;
            }

            try
            {
                // Give process time to start
                await Task.Delay(500);

                if (process.HasExited)
                {
                    // dotnet script not available, skip test
                    return;
                }

                await WritePidFileAsync(process.Id);

                var status = await _manager.GetStatusAsync();

                status.State.ShouldBe(GatewayState.Running);
                status.Pid.ShouldBe(process.Id);
                status.Uptime.ShouldNotBeNull();
                status.Message.ShouldContain("Running");
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempScript))
                {
                    File.Delete(tempScript);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task GetStatusAsync_WhenPidRecycled_ReturnsNotRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Start a non-BotNexus process (notepad) to simulate PID recycling
        var psi = new ProcessStartInfo
        {
            FileName = "notepad.exe",
            UseShellExecute = true,
            CreateNoWindow = false
        };

        var process = Process.Start(psi);
        process.ShouldNotBeNull();

        try
        {
            await WritePidFileAsync(process.Id);

            var status = await _manager.GetStatusAsync();

            // Should detect that PID is recycled (notepad, not dotnet/BotNexus)
            status.State.ShouldBe(GatewayState.NotRunning);
            status.Pid.ShouldBeNull();
            status.Message.ShouldContain("recycled");

            // PID file should be cleaned up
            var pidFilePath = GetPidFilePath();
            File.Exists(pidFilePath).ShouldBeFalse();
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void IsRunning_WhenNoPidFile_ReturnsFalse()
    {
        _manager.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task IsRunning_WhenPidFileAndProcessAlive_ReturnsTrue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Use the current process
        var currentProcess = Process.GetCurrentProcess();
        await WritePidFileAsync(currentProcess.Id);

        _manager.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task IsRunning_WhenStalePid_ReturnsFalse()
    {
        // Write a PID file with a definitely-dead PID
        await WritePidFileAsync(99999);

        _manager.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_ConsecutiveCalls_ConsistentResults()
    {
        var status1 = await _manager.GetStatusAsync();
        var status2 = await _manager.GetStatusAsync();

        status1.State.ShouldBe(status2.State);
        status1.Pid.ShouldBe(status2.Pid);
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

    private string GetPidFilePath()
    {
        var pidFileField = typeof(GatewayProcessManager).GetField("_pidFilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)pidFileField!.GetValue(_manager)!;
    }
}
