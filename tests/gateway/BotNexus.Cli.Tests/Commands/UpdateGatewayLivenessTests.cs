using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Command-level pins for issue #2772: the update must report what it OBSERVED about the gateway
/// rather than asserting it from control flow, must abort before the build when the port never
/// frees, and must start a gateway that is not running even when there is nothing to rebuild.
///
/// No real process is spawned or signalled and no socket is ever held: liveness and port state are
/// stated through the protected-virtual seams the implementation exposes for exactly that purpose.
/// </summary>
[Collection("AnsiConsole")]
public sealed class UpdateGatewayLivenessTests
{
    /// <summary>
    /// Drives <c>ExecuteAsync</c> with every environmental fact scripted: whether the pull moved
    /// HEAD, whether the tree is clean, and whether the port ever frees. Counts the observable
    /// decisions (build reached, restart reached).
    /// </summary>
    private sealed class LivenessPipelineCommand(
        IGatewayProcessManager processManager,
        bool pullWasNoOp,
        UpdateCommand.WorkingTreeCleanliness cleanliness,
        bool portFree = true)
        : UpdateCommand(processManager)
    {
        public int BuildAndDeployCalls { get; private set; }

        public int RestartCalls { get; private set; }

        protected override Task<int> RunGitPullStepAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
        {
            LastPullWasNoOp = pullWasNoOp;
            return Task.FromResult(0);
        }

        protected override Task<WorkingTreeCleanliness> GetWorkingTreeCleanlinessAsync(
            string repoRoot, CancellationToken cancellationToken)
            => Task.FromResult(cleanliness);

        protected override Task<bool> WaitForPortFreeAsync(int port, CancellationToken cancellationToken)
            => Task.FromResult(portFree);

        protected override Task<int> RunBuildAndDeployAsync(
            string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
        {
            BuildAndDeployCalls++;
            return Task.FromResult(0);
        }

        protected override Task<int> RunRestartAsync(string home, int port, string repoRoot, CancellationToken cancellationToken)
        {
            RestartCalls++;
            return base.RunRestartAsync(home, port, repoRoot, cancellationToken);
        }
    }

    private static IGatewayProcessManager NewProcessManager(
        bool gatewayRunning = true,
        GatewayStopOutcome stopOutcome = GatewayStopOutcome.Stopped,
        string? stopMessage = null)
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, stopMessage, stopOutcome));
        pm.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(true, 4242, null));
        pm.IsRunning(Arg.Any<string?>(), Arg.Any<string?>()).Returns(gatewayRunning);
        return pm;
    }

    /// <summary>
    /// A throwaway directory containing only the gateway binary <c>ResolveGatewayBinaryPath</c>
    /// points at. No git repository is needed: the pull and status steps are scripted.
    /// </summary>
    private static string CreateRootWithGatewayBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bn-2772-cmd-{Guid.NewGuid():N}");
        var binary = UpdateCommand.ResolveGatewayBinaryPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "fake assembly");
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string NormalizeWhitespace(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        var originalConsole = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.No
        });
        try
        {
            await action();
            return NormalizeWhitespace(writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    // -------------------------------------------------------------------------------------
    // AC1: Stopped and NotRunning must render differently.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC1_ExecuteAsync_PrintsGatewayStopped_WhenStopOutcomeIsStopped()
    {
        var root = CreateRootWithGatewayBinary();
        try
        {
            var cmd = new LivenessPipelineCommand(
                NewProcessManager(stopOutcome: GatewayStopOutcome.Stopped),
                pullWasNoOp: true,
                UpdateCommand.WorkingTreeCleanliness.Dirty);

            var output = await CaptureAsync(() =>
                cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None));

            output.ShouldContain("Gateway stopped");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AC1_ExecuteAsync_DoesNotClaimAStopOccurred_WhenStopOutcomeIsNotRunning()
    {
        var root = CreateRootWithGatewayBinary();
        try
        {
            var cmd = new LivenessPipelineCommand(
                NewProcessManager(stopOutcome: GatewayStopOutcome.NotRunning, stopMessage: "no PID file"),
                pullWasNoOp: true,
                UpdateCommand.WorkingTreeCleanliness.Dirty);

            var output = await CaptureAsync(() =>
                cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None));

            output.ShouldContain("No running gateway found");
            output.ShouldNotContain("Gateway stopped");
            // nothing was stopped, so the update must not claim it stopped anything (#2772)
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AC1_ExecuteAsync_WarnsWithoutClaimingAStop_WhenStopOutcomeIsFailed()
    {
        var root = CreateRootWithGatewayBinary();
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(false, "did not exit", GatewayStopOutcome.Failed));
        pm.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(true, 4242, null));
        try
        {
            var cmd = new LivenessPipelineCommand(pm, pullWasNoOp: true, UpdateCommand.WorkingTreeCleanliness.Dirty);

            var output = await CaptureAsync(() =>
                cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None));

            output.ShouldContain("Could not stop gateway");
            output.ShouldNotContain("Gateway stopped");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // AC4: a port that never frees aborts BEFORE the build.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC4_ExecuteAsync_ReturnsNonZeroAndSkipsBuild_WhenPortNeverBecomesFree()
    {
        var root = CreateRootWithGatewayBinary();
        try
        {
            var cmd = new LivenessPipelineCommand(
                NewProcessManager(),
                pullWasNoOp: true,
                UpdateCommand.WorkingTreeCleanliness.Dirty,
                portFree: false);

            var exitCode = await cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None);

            exitCode.ShouldNotBe(0, "a still-held port means the build would fail on locked files");
            cmd.BuildAndDeployCalls.ShouldBe(0,
                "the update must abort BEFORE burning a full Release build (#2772)");
            cmd.RestartCalls.ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AC4_ExecuteAsync_ExplainsTheAbort_WhenPortNeverBecomesFree()
    {
        var root = CreateRootWithGatewayBinary();
        try
        {
            var cmd = new LivenessPipelineCommand(
                NewProcessManager(),
                pullWasNoOp: true,
                UpdateCommand.WorkingTreeCleanliness.Dirty,
                portFree: false);

            var output = await CaptureAsync(() =>
                cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None));

            output.ShouldContain("still in use");
            output.ShouldContain("aborted before building");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AC4_ExecuteAsync_ProceedsToBuild_WhenPortBecomesFree()
    {
        // Guards the AC4 tests against passing for the wrong reason: the abort must be caused by
        // the port state, not by the pipeline never reaching the build at all.
        var root = CreateRootWithGatewayBinary();
        try
        {
            var cmd = new LivenessPipelineCommand(
                NewProcessManager(),
                pullWasNoOp: true,
                UpdateCommand.WorkingTreeCleanliness.Dirty,
                portFree: true);

            var exitCode = await cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None);

            exitCode.ShouldBe(0);
            cmd.BuildAndDeployCalls.ShouldBe(1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // AC5: nothing to rebuild but NO gateway alive => start one.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC5_ExecuteAsync_StartsGateway_WhenNothingToRebuildAndGatewayIsNotRunning()
    {
        var root = CreateRootWithGatewayBinary();
        var pm = NewProcessManager(gatewayRunning: false);
        try
        {
            var cmd = new LivenessPipelineCommand(pm, pullWasNoOp: true, UpdateCommand.WorkingTreeCleanliness.Clean);

            var exitCode = await cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None);

            exitCode.ShouldBe(0);
            cmd.RestartCalls.ShouldBe(1, "a dead gateway must be started even when there is nothing to build");
            await pm.Received(1).StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
            cmd.BuildAndDeployCalls.ShouldBe(0, "starting a gateway is not a reason to rebuild");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AC5_ExecuteAsync_SaysItIsStartingTheGateway_WhenNothingToRebuildAndGatewayIsNotRunning()
    {
        var root = CreateRootWithGatewayBinary();
        try
        {
            var cmd = new LivenessPipelineCommand(
                NewProcessManager(gatewayRunning: false),
                pullWasNoOp: true,
                UpdateCommand.WorkingTreeCleanliness.Clean);

            var output = await CaptureAsync(() =>
                cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None));

            output.ShouldContain("no gateway is running");
            output.ShouldNotContain("gateway left running");
            // claiming the gateway was left running when none exists is exactly bug #2772
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AC5_ExecuteAsync_AsksTheProcessManagerAboutLiveness_UsingTheResolvedGatewayBinary()
    {
        // Pins that liveness is DISCOVERED via the one gateway-path resolver, not asserted from
        // control flow and not re-derived by a second path convention.
        var root = CreateRootWithGatewayBinary();
        var pm = NewProcessManager(gatewayRunning: true);
        try
        {
            var cmd = new LivenessPipelineCommand(pm, pullWasNoOp: true, UpdateCommand.WorkingTreeCleanliness.Clean);

            await cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None);

            pm.Received(1).IsRunning(root, UpdateCommand.ResolveGatewayBinaryPath(root));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // AC6 is covered by UpdateNoOpRebuildSkipTests.ExecuteAsync_ReturnsZeroWithoutTouchingGateway_
    // WhenNothingChanged (build skipped AND StartAsync not received). Only the RENDERED claim is
    // added here, since that is the sentence #2772 says must be earned rather than assumed.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AC6_ExecuteAsync_SaysGatewayLeftRunning_OnlyWhenAGatewayIsActuallyAlive()
    {
        var root = CreateRootWithGatewayBinary();
        var pm = NewProcessManager(gatewayRunning: true);
        try
        {
            var cmd = new LivenessPipelineCommand(pm, pullWasNoOp: true, UpdateCommand.WorkingTreeCleanliness.Clean);

            var output = await CaptureAsync(() =>
                cmd.ExecuteAsync(root, root, FreePort(), verbose: false, CancellationToken.None));

            output.ShouldContain("gateway left running");
            cmd.RestartCalls.ShouldBe(0);
            await pm.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
