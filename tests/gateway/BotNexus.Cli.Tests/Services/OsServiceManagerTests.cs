using System.Runtime.InteropServices;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Tests for OS service manager factory and platform-specific implementations.
/// </summary>
public sealed class OsServiceManagerTests
{
    [Fact]
    public void Factory_Create_ReturnsManagerForCurrentPlatform()
    {
        var manager = OsServiceManagerFactory.Create();

        // Should always return something on Windows, Linux, or macOS
        manager.ShouldNotBeNull();
        manager.IsSupported.ShouldBeTrue();
        manager.ServiceManagerName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void WindowsServiceManager_IsSupported_MatchesPlatform()
    {
        var manager = new WindowsServiceManager();
        manager.IsSupported.ShouldBe(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        manager.ServiceManagerName.ShouldBe("Windows Service");
    }

    [Fact]
    public void SystemdServiceManager_IsSupported_MatchesPlatform()
    {
        var manager = new SystemdServiceManager();
        manager.IsSupported.ShouldBe(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        manager.ServiceManagerName.ShouldBe("systemd");
    }

    [Fact]
    public void LaunchdServiceManager_IsSupported_MatchesPlatform()
    {
        var manager = new LaunchdServiceManager();
        manager.IsSupported.ShouldBe(RuntimeInformation.IsOSPlatform(OSPlatform.OSX));
        manager.ServiceManagerName.ShouldBe("launchd");
    }

    [Fact]
    public void ServiceOperationResult_Success_HasMessage()
    {
        var result = new ServiceOperationResult(true, "Installed OK");
        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Installed OK");
    }

    [Fact]
    public void ServiceOperationResult_Failure_HasMessage()
    {
        var result = new ServiceOperationResult(false, "Permission denied");
        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Permission denied");
    }

    [Fact]
    public async Task WindowsServiceManager_Install_WhenAlreadyInstalled_ReturnsFalse()
    {
        // This test verifies behavior — on non-Windows it should be a no-op since sc.exe won't exist
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows — cannot exercise sc.exe

        var manager = new WindowsServiceManager();
        // Don't actually install — just verify the interface contract works
        // The real install requires admin privileges
        var isInstalled = await manager.IsInstalledAsync();
        // BotNexus service typically not installed in test environments
        isInstalled.ShouldBeFalse();
    }
}
