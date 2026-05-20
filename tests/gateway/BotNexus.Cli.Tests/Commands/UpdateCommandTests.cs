using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;
using Spectre.Console;

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

    private sealed class GitPullStepProbeCommand(IGatewayProcessManager processManager)
        : UpdateCommand(processManager)
    {
        public Task<int> RunGitPullStepForTestAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
            => RunGitPullStepAsync(repoRoot, verbose, cancellationToken);
    }

    private sealed class ScriptedGitPullCommand(
        IGatewayProcessManager processManager,
        string beforeSha,
        string afterSha,
        int pullExitCode,
        int commitCount,
        IReadOnlyList<string> commitSubjects,
        Version? runningCliVersion = null,
        Version? sourceCliVersion = null)
        : UpdateCommand(processManager)
    {
        private int _shaReads;
        public bool CliUpdateWarningPrinted { get; private set; }

        protected override string GetCommitSha(string repoRoot)
            => _shaReads++ == 0 ? beforeSha : afterSha;

        protected override Task<GitPullResult> RunGitPullAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
            => Task.FromResult(new GitPullResult(pullExitCode, null, false));

        protected override Task<int> CountCommitsBetweenAsync(string repoRoot, string from, string to, CancellationToken cancellationToken)
            => Task.FromResult(commitCount);

        protected override Task<IReadOnlyList<string>> GetCommitSubjectsBetweenAsync(
            string repoRoot,
            string from,
            string to,
            CancellationToken cancellationToken)
            => Task.FromResult(commitSubjects);

        protected override Version? GetRunningCliVersion()
            => runningCliVersion;

        protected override Version? GetSourceCliVersion(string repoRoot)
            => sourceCliVersion;

        protected override void PrintCliUpdateWarningIfNeeded(string repoRoot)
        {
            var runningVersion = GetRunningCliVersion();
            var sourceVersion = GetSourceCliVersion(repoRoot);
            if (runningVersion is null || sourceVersion is null || sourceVersion <= runningVersion)
                return;

            CliUpdateWarningPrinted = true;
        }

        public Task<int> RunGitPullStepForTestAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
            => RunGitPullStepAsync(repoRoot, verbose, cancellationToken);
    }

    private sealed class ScriptedUpdateCheckCommand(
        IGatewayProcessManager processManager,
        UpdateCommand.GitCommandResult fetchResult,
        UpdateCommand.GitBehindResult behindResult)
        : UpdateCommand(processManager)
    {
        protected override Task<GitCommandResult> RunGitFetchAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
            => Task.FromResult(fetchResult);

        protected override Task<GitBehindResult> GetBehindCountAsync(string repoRoot, CancellationToken cancellationToken)
            => Task.FromResult(behindResult);
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
    public void Update_command_registers_check_subcommand()
    {
        var verbose = new Option<bool>("--verbose");
        var command = BuildCommand().Build(verbose);

        command.Subcommands.Any(c => c.Name == "check").ShouldBeTrue();
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

    [Fact]
    public async Task Update_WhenCancelledBeforeGitPull_ReturnsCancelledExitCodeAndSkipsGatewaySteps()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new UpdateCommand(pm);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-update-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await cmd.ExecuteAsync(
                repoRoot: tempDir,
                home: tempDir,
                port: 5005,
                verbose: false,
                cancellationToken: cts.Token);

            exitCode.ShouldBe(130);
            await pm.DidNotReceive().StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await pm.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenCancellationRequested_ReturnsCancelledExitCode()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new GitPullStepProbeCommand(pm);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-update-git-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await cmd.RunGitPullStepForTestAsync(
                repoRoot: tempDir,
                verbose: false,
                cancellationToken: cts.Token);

            exitCode.ShouldBe(130);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenUpdatesApplied_PrintsConventionalCommitSubjectsInOrder()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedGitPullCommand(
            pm,
            beforeSha: "1111111111111111111111111111111111111111",
            afterSha: "2222222222222222222222222222222222222222",
            pullExitCode: 0,
            commitCount: 2,
            commitSubjects:
            [
                "feat(cli): add update changelog subjects",
                "fix(update)!: suppress changelog noise"
            ]);

        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            var exitCode = await cmd.RunGitPullStepForTestAsync(
                repoRoot: "unused",
                verbose: false,
                cancellationToken: CancellationToken.None);
            exitCode.ShouldBe(0);
        });

        output.ShouldContain("Changes applied:");
        output.ShouldContain("- feat(cli): add update changelog subjects");
        output.ShouldContain("- fix(update)!: suppress changelog noise");
        output.IndexOf("- feat(cli): add update changelog subjects", StringComparison.Ordinal)
            .ShouldBeLessThan(output.IndexOf("- fix(update)!: suppress changelog noise", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenAlreadyUpToDate_DoesNotPrintChangesAppliedSection()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedGitPullCommand(
            pm,
            beforeSha: "3333333333333333333333333333333333333333",
            afterSha: "3333333333333333333333333333333333333333",
            pullExitCode: 0,
            commitCount: 0,
            commitSubjects: []);

        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            var exitCode = await cmd.RunGitPullStepForTestAsync(
                repoRoot: "unused",
                verbose: false,
                cancellationToken: CancellationToken.None);
            exitCode.ShouldBe(0);
        });

        output.ShouldContain("Already up to date");
        output.ShouldNotContain("Changes applied:");
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenSourceCliVersionIsNewer_PrintsToolUpdateWarning()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedGitPullCommand(
            pm,
            beforeSha: "1111111111111111111111111111111111111111",
            afterSha: "1111111111111111111111111111111111111111",
            pullExitCode: 0,
            commitCount: 0,
            commitSubjects: [],
            runningCliVersion: new Version(0, 1, 8),
            sourceCliVersion: new Version(0, 1, 10));

        var exitCode = await cmd.RunGitPullStepForTestAsync(
            repoRoot: "unused",
            verbose: false,
            cancellationToken: CancellationToken.None);

        exitCode.ShouldBe(0);
        cmd.CliUpdateWarningPrinted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0.1.10", "0.1.10")]
    [InlineData("0.1.11", "0.1.10")]
    public async Task RunGitPullStepAsync_WhenSourceCliVersionIsNotNewer_DoesNotPrintToolUpdateWarning(
        string runningVersion,
        string sourceVersion)
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedGitPullCommand(
            pm,
            beforeSha: "1111111111111111111111111111111111111111",
            afterSha: "1111111111111111111111111111111111111111",
            pullExitCode: 0,
            commitCount: 0,
            commitSubjects: [],
            runningCliVersion: Version.Parse(runningVersion),
            sourceCliVersion: Version.Parse(sourceVersion));

        var exitCode = await cmd.RunGitPullStepForTestAsync(
            repoRoot: "unused",
            verbose: false,
            cancellationToken: CancellationToken.None);

        exitCode.ShouldBe(0);
        cmd.CliUpdateWarningPrinted.ShouldBeFalse();
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenSourceCliVersionUnavailable_DoesNotPrintToolUpdateWarning()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedGitPullCommand(
            pm,
            beforeSha: "1111111111111111111111111111111111111111",
            afterSha: "1111111111111111111111111111111111111111",
            pullExitCode: 0,
            commitCount: 0,
            commitSubjects: [],
            runningCliVersion: new Version(0, 1, 10),
            sourceCliVersion: null);

        var exitCode = await cmd.RunGitPullStepForTestAsync(
            repoRoot: "unused",
            verbose: false,
            cancellationToken: CancellationToken.None);

        exitCode.ShouldBe(0);
        cmd.CliUpdateWarningPrinted.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckAsync_WhenRepositoryIsUpToDate_ReturnsZero()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedUpdateCheckCommand(
            pm,
            new UpdateCommand.GitCommandResult(0, null, false),
            new UpdateCommand.GitBehindResult(0, 0, null, false));

        var exitCode = await cmd.CheckAsync("unused", verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task CheckAsync_WhenRepositoryIsBehind_ReturnsOne()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedUpdateCheckCommand(
            pm,
            new UpdateCommand.GitCommandResult(0, null, false),
            new UpdateCommand.GitBehindResult(0, 3, null, false));

        var exitCode = await cmd.CheckAsync("unused", verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task CheckAsync_WhenFetchFails_ReturnsErrorCodeTwo()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        var cmd = new ScriptedUpdateCheckCommand(
            pm,
            new UpdateCommand.GitCommandResult(128, "fatal: not a git repository", false),
            new UpdateCommand.GitBehindResult(0, 0, null, false));

        var exitCode = await cmd.CheckAsync("unused", verbose: false, CancellationToken.None);

        exitCode.ShouldBe(2);
    }

    private static async Task<string> CaptureAnsiConsoleOutputAsync(Func<Task> action)
    {
        var originalConsole = AnsiConsole.Console;
        using var outputWriter = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(outputWriter),
            Interactive = InteractionSupport.No
        });

        try
        {
            await action();
            return outputWriter.ToString();
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

}
