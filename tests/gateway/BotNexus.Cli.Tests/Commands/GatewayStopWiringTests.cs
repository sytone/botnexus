using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;

namespace BotNexus.Cli.Tests.Commands;

public sealed class GatewayStopWiringTests
{
    [Fact]
    public async Task Stop_PassesResolvedGatewayBinaryPath_ToProcessManager()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, "Gateway stopped (PID 42)", GatewayStopOutcome.Stopped));
        var command = BuildCommand(processManager);
        var source = Path.Combine(Path.GetTempPath(), $"bn-4126-source-{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"bn-4126-home-{Guid.NewGuid():N}");

        var exitCode = await command.InvokeAsync(new[] { "stop", "--source", source, "--target", target });

        exitCode.ShouldBe(0);
        await processManager.Received(1).StopAsync(
            target,
            GatewayCommand.ResolveGatewayBinaryPath(source),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restart_PassesResolvedGatewayBinaryPath_ToProcessManager()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(false, "still running", GatewayStopOutcome.Failed));
        var command = BuildCommand(processManager);
        var source = Path.Combine(Path.GetTempPath(), $"bn-4126-source-{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"bn-4126-home-{Guid.NewGuid():N}");

        var exitCode = await command.InvokeAsync(new[] { "restart", "--source", source, "--target", target });

        exitCode.ShouldNotBe(0, "restart must not rebuild while a gateway still holds its assemblies");
        await processManager.Received(1).StopAsync(
            target,
            GatewayCommand.ResolveGatewayBinaryPath(source),
            Arg.Any<CancellationToken>());
        await processManager.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_ReturnsFailure_WhenNoGatewayCanBeIdentified()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        processManager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, "Gateway is not running (no PID file)", GatewayStopOutcome.NotRunning));
        var command = BuildCommand(processManager);

        var exitCode = await command.InvokeAsync(new[] { "stop", "--source", "source", "--target", "target" });

        exitCode.ShouldNotBe(0);
    }

    private static Command BuildCommand(IGatewayProcessManager processManager)
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var command = new GatewayCommand(processManager).Build(verbose, target);
        command.AddGlobalOption(verbose);
        command.AddGlobalOption(target);
        return command;
    }
}
