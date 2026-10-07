using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Configuration;
using NSubstitute;

namespace BotNexus.Cli.Tests.Commands;

public sealed class GatewayStopWiringTests
{
    [Fact]
    public async Task Stop_PassesResolvedGatewayBinaryPath_ToProcessManager()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new GatewayStopResult(true, "Gateway stopped (PID 42)", GatewayStopOutcome.Stopped));
        var command = BuildCommand(processManager);
        var source = Path.Combine(Path.GetTempPath(), $"bn-4126-source-{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"bn-4126-home-{Guid.NewGuid():N}");

        var exitCode = await command.InvokeAsync(new[] { "stop", "--source", source, "--target", target });

        exitCode.ShouldBe(0);
        await processManager.Received(1).StopAsync(
            target,
            GatewayCommand.ResolveGatewayBinaryPath(source),
            Arg.Any<CancellationToken>(),
            GatewayDefaults.LoopbackListenUrl);
    }

    [Fact]
    public async Task Restart_PassesResolvedGatewayBinaryPath_ToProcessManager()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new GatewayStopResult(false, "still running", GatewayStopOutcome.Failed));
        var command = BuildCommand(processManager);
        var source = Path.Combine(Path.GetTempPath(), $"bn-4126-source-{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"bn-4126-home-{Guid.NewGuid():N}");

        var exitCode = await command.InvokeAsync(new[] { "restart", "--source", source, "--target", target });

        exitCode.ShouldNotBe(0, "restart must not rebuild while a gateway still holds its assemblies");
        await processManager.Received(1).StopAsync(
            target,
            GatewayCommand.ResolveGatewayBinaryPath(source),
            Arg.Any<CancellationToken>(),
            GatewayDefaults.LoopbackListenUrl);
        await processManager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_ReturnsFailure_WhenNoGatewayCanBeIdentified()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new GatewayStopResult(true, "Gateway is not running (no PID file)", GatewayStopOutcome.NotRunning));
        var command = BuildCommand(processManager);

        var exitCode = await command.InvokeAsync(new[] { "stop", "--source", "source", "--target", "target" });

        exitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task Uninstall_RemovesDefinition_WhenServiceIsAlreadyStopped()
    {
        var calls = new List<string>();
        var processManager = Substitute.For<IGatewayProcessManager>();
        var serviceManager = new FakeOsServiceManager
        {
            IsInstalled = true,
            IsRunning = false,
            UninstallResult = new ServiceOperationResult(true, "removed"),
            OnUninstall = () => calls.Add("uninstall")
        };
        var command = BuildCommand(processManager, () => serviceManager);

        var exitCode = await command.InvokeAsync(["uninstall", "--source", "source", "--target", "target"]);

        exitCode.ShouldBe(0);
        calls.ShouldBe(["uninstall"]);
        await processManager.DidNotReceive().StopAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Uninstall_DoesNotRemoveDefinition_WhenNativeFallbackCannotStopService()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.RequestPlannedShutdownAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new GatewayStopResult(true, "no handle", GatewayStopOutcome.NotRunning));
        var serviceManager = new FakeOsServiceManager
        {
            IsInstalled = true,
            IsRunning = true,
            StopResult = new ServiceOperationResult(false, "still running")
        };
        var command = BuildCommand(processManager, () => serviceManager);

        var exitCode = await command.InvokeAsync(["uninstall", "--source", "source", "--target", "target"]);

        exitCode.ShouldNotBe(0);
        await processManager.Received(1).RequestPlannedShutdownAsync(
            "target", GatewayDefaults.LoopbackListenUrl, Arg.Any<CancellationToken>());
        serviceManager.UninstallCalls.ShouldBe(0);
    }

    private sealed class FakeOsServiceManager : IOsServiceManager
    {
        public bool IsSupported => true;
        public string ServiceManagerName => "test service";
        public bool IsInstalled { get; init; }
        public bool IsRunning { get; init; }
        public ServiceOperationResult StopResult { get; init; } = new(true, "stopped");
        public ServiceOperationResult UninstallResult { get; init; } = new(true, "removed");
        public Action? OnUninstall { get; init; }
        public int UninstallCalls { get; private set; }

        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsInstalled);

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsRunning);

        public Task<ServiceOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StopResult);

        public Task<ServiceOperationResult> InstallAsync(
            string executablePath,
            string homePath,
            int port,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServiceOperationResult(true, "installed"));

        public Task<ServiceOperationResult> UninstallAsync(CancellationToken cancellationToken = default)
        {
            UninstallCalls++;
            OnUninstall?.Invoke();
            return Task.FromResult(UninstallResult);
        }
    }

    private static Command BuildCommand(
        IGatewayProcessManager processManager,
        Func<IOsServiceManager?>? serviceManagerFactory = null)
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var command = new GatewayCommand(
            processManager,
            serviceManagerFactory ?? OsServiceManagerFactory.Create).Build(verbose, target);
        command.AddGlobalOption(verbose);
        command.AddGlobalOption(target);
        return command;
    }
}
