using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;

namespace BotNexus.Cli.Tests.Commands;

public class UpdateCommandTests
{
    /// <summary>
    /// Test subclass that bypasses git pull and build steps so unit tests
    /// can focus on the stop/restart logic without running real git/dotnet build.
    /// </summary>
    private sealed class NoOpPreStopUpdateCommand(IGatewayProcessManager processManager)
        : UpdateCommand(processManager)
    {
        protected override Task<int> RunGitPullStepAsync(
            string repoRoot, bool verbose, CancellationToken cancellationToken)
            => Task.FromResult(0);

        protected override Task<int> RunBuildAndDeployAsync(
            string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
            => Task.FromResult(0);
    }

    private static UpdateCommand BuildCommand()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, null));
        pm.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(true, 99999, null));
        return new UpdateCommand(pm);
    }

    [Fact]
    public void Update_command_is_registered_on_root()
    {
        var verbose = new Option<bool>("--verbose");
        var command = BuildCommand().Build(verbose);

        command.Name.ShouldBe("update");
    }

    [Fact]
    public void Update_command_has_expected_options()
    {
        var verbose = new Option<bool>("--verbose");
        var command = BuildCommand().Build(verbose);

        var names = command.Options.Select(o => o.Name).ToList();
        names.ShouldContain("source");
        names.ShouldContain("target");
        names.ShouldContain("port");
    }

    [Fact]
    public async Task Update_WhenStopFails_ReturnsNonZeroAndDoesNotStartGateway()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(false, "Kill failed"));
        var cmd = new NoOpPreStopUpdateCommand(pm);

        var tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-update-stopfail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await cmd.ExecuteAsync(
                repoRoot: tempDir,
                home: tempDir,
                port: 5005,
                verbose: false,
                cancellationToken: CancellationToken.None);

            exitCode.ShouldNotBe(0);
            await pm.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Update_WhenGatewayStillRunningAfterStop_DoesNotStartGateway()
    {
        // Bind a real TCP port so IsPortAvailable(port) returns false.
        // This simulates the gateway surviving stop (port still in use).
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var busyPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, "Stopped"));
        var cmd = new NoOpPreStopUpdateCommand(pm);

        var tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-update-portbusy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await cmd.ExecuteAsync(
                repoRoot: tempDir,
                home: tempDir,
                port: busyPort,
                verbose: false,
                cancellationToken: CancellationToken.None);

            // Should fail because port is still in use after stop
            exitCode.ShouldNotBe(0);
            await pm.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            listener.Stop();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Update_with_non_git_directory_returns_nonzero()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, null));
        pm.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(false, null, "not expected in this test"));
        var cmd = new UpdateCommand(pm);

        var tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await cmd.ExecuteAsync(
                repoRoot: tempDir,
                home: tempDir,
                port: 5005,
                verbose: false,
                cancellationToken: CancellationToken.None);

            exitCode.ShouldNotBe(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
