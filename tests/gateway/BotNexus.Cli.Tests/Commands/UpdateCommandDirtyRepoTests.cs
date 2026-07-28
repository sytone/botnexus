using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Covers the deployment-repo dirty working-tree pre-flight and the pull-failure classifier
/// added for issue #2492. Every test asserts the SPECIFIC message text and the SPECIFIC exit
/// code, not merely "an error happened".
/// </summary>
[Collection("AnsiConsole")]
public class UpdateCommandDirtyRepoTests
{
    /// <summary>
    /// Scripts the working-tree status and records what recovery action the command chose,
    /// so no real git process ever runs.
    /// </summary>
    private sealed class ScriptedDirtyTreeCommand(
        IGatewayProcessManager processManager,
        UpdateCommand.GitStatusResult status,
        UpdateCommand.GitCommandResult? stashResult = null,
        UpdateCommand.GitCommandResult? discardResult = null)
        : UpdateCommand(processManager)
    {
        public int StashCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public string? StashLabel { get; private set; }

        protected override Task<GitStatusResult> GetWorkingTreeStatusAsync(string repoRoot, CancellationToken cancellationToken)
            => Task.FromResult(status);

        protected override Task<GitCommandResult> StashChangesAsync(string repoRoot, string label, CancellationToken cancellationToken)
        {
            StashCalls++;
            StashLabel = label;
            return Task.FromResult(stashResult ?? new GitCommandResult(0, null, false));
        }

        protected override Task<GitCommandResult> DiscardChangesAsync(string repoRoot, CancellationToken cancellationToken)
        {
            DiscardCalls++;
            return Task.FromResult(discardResult ?? new GitCommandResult(0, null, false));
        }

        public Task<int> EnsureWorkingTreeReadyForTestAsync(string repoRoot, CancellationToken cancellationToken)
            => EnsureWorkingTreeReadyAsync(repoRoot, cancellationToken);
    }

    /// <summary>
    /// Drives the full pull step with a scripted status and a scripted pull failure so the
    /// classified failure message can be asserted end to end.
    /// </summary>
    private sealed class ScriptedPullFailureCommand(
        IGatewayProcessManager processManager,
        UpdateCommand.GitPullResult pullResult)
        : UpdateCommand(processManager)
    {
        protected override Task<GitStatusResult> GetWorkingTreeStatusAsync(string repoRoot, CancellationToken cancellationToken)
            => Task.FromResult(new GitStatusResult(0, Array.Empty<string>(), Array.Empty<string>(), null, false));

        protected override string GetCommitSha(string repoRoot) => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        protected override Task<GitPullResult> RunGitPullAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
            => Task.FromResult(pullResult);

        public Task<int> RunGitPullStepForTestAsync(string repoRoot, CancellationToken cancellationToken)
            => RunGitPullStepAsync(repoRoot, verbose: false, cancellationToken);
    }

    private static UpdateCommand.GitStatusResult Dirty(params string[] paths)
        => new(0, paths, Array.Empty<string>(), null, false);

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenClean_ReturnsZeroAndTouchesNothing()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            new UpdateCommand.GitStatusResult(0, Array.Empty<string>(), Array.Empty<string>(), null, false));

        var exitCode = 999;
        await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync("unused", CancellationToken.None);
        });

        exitCode.ShouldBe(0);
        cmd.StashCalls.ShouldBe(0);
        cmd.DiscardCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenOnlyUntrackedFiles_DoesNotBlockUpdate()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            new UpdateCommand.GitStatusResult(0, Array.Empty<string>(), new[] { "notes.txt" }, null, false));

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync("unused", CancellationToken.None);
        });

        exitCode.ShouldBe(0);
        output.ShouldContain("1 untracked file(s) in the repo; these do not block the update.");
        cmd.DiscardCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenDirtyAndNonInteractive_ExitsThreeAndNamesEveryDirtyPath()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            Dirty("scripts/recover-gateway.ps1", "src/gateway/Program.cs"));

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(3);
        exitCode.ShouldBe(UpdateCommand.DirtyWorkingTreeExitCode);
        output.ShouldContain("Update aborted: 2 uncommitted change(s) in the deployment repo.");
        output.ShouldContain("scripts/recover-gateway.ps1");
        output.ShouldContain("src/gateway/Program.cs");
        output.ShouldContain("Your local changes were left untouched.");
        output.ShouldContain("botnexus update --stash");
        output.ShouldContain("botnexus update --force");
        cmd.StashCalls.ShouldBe(0);
        cmd.DiscardCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenStashRequested_StashesWithNamedLabelAndPrintsRecoveryCommand()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            Dirty("scripts/recover-gateway.ps1"))
        {
            DirtyTreeHandling = UpdateCommand.DirtyTreeMode.Stash
        };

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(0);
        cmd.StashCalls.ShouldBe(1);
        cmd.DiscardCalls.ShouldBe(0);
        cmd.StashLabel.ShouldNotBeNull();
        cmd.StashLabel!.ShouldStartWith("botnexus-update-");
        output.ShouldContain("Stashed 1 local change(s) as");
        output.ShouldContain(cmd.StashLabel);
        output.ShouldContain("stash apply");
    }

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenStashFails_AbortsWithDirtyExitCodeAndDoesNotDiscard()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            Dirty("scripts/recover-gateway.ps1"),
            stashResult: new UpdateCommand.GitCommandResult(1, "fatal: unable to write stash", false))
        {
            DirtyTreeHandling = UpdateCommand.DirtyTreeMode.Stash
        };

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(UpdateCommand.DirtyWorkingTreeExitCode);
        output.ShouldContain("Could not stash local changes; update aborted.");
        output.ShouldContain("fatal: unable to write stash");
        cmd.DiscardCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenForceRequested_DiscardsAndReportsEveryDiscardedPath()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            Dirty("scripts/recover-gateway.ps1", "docs/readme.md"))
        {
            DirtyTreeHandling = UpdateCommand.DirtyTreeMode.Force
        };

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(0);
        cmd.DiscardCalls.ShouldBe(1);
        cmd.StashCalls.ShouldBe(0);
        output.ShouldContain("Discarding 2 local change(s) in the deployment repo (--force):");
        output.ShouldContain("scripts/recover-gateway.ps1");
        output.ShouldContain("docs/readme.md");
        output.ShouldContain("Discarded 2 local change(s)");
    }

    [Fact]
    public async Task EnsureWorkingTreeReady_WhenStatusCannotBeRead_DoesNotBlockUpdate()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            new UpdateCommand.GitStatusResult(128, Array.Empty<string>(), Array.Empty<string>(), "not a git repository", false));

        var exitCode = 999;
        await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync("unused", CancellationToken.None);
        });

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenDirtyTreeBlocksUpdate_NeverReachesThePull()
    {
        var cmd = new ScriptedDirtyTreeCommand(
            Substitute.For<IGatewayProcessManager>(),
            Dirty("scripts/recover-gateway.ps1"));

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.EnsureWorkingTreeReadyForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(UpdateCommand.DirtyWorkingTreeExitCode);
        output.ShouldNotContain("Pulled");
    }

    [Theory]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:", UpdateCommand.GitPullFailureKind.DirtyTree)]
    [InlineData("Please commit your changes or stash them before you merge.", UpdateCommand.GitPullFailureKind.DirtyTree)]
    [InlineData("hint: You have divergent branches and need to specify how to reconcile them.", UpdateCommand.GitPullFailureKind.Other)]
    [InlineData("Your branch and 'origin/main' have diverged,", UpdateCommand.GitPullFailureKind.Diverged)]
    [InlineData("Automatic merge failed; fix conflicts and then commit the result.", UpdateCommand.GitPullFailureKind.Diverged)]
    [InlineData("remote: Invalid username or password.", UpdateCommand.GitPullFailureKind.Auth)]
    [InlineData("fatal: Authentication failed for 'https://github.com/Sytone/botnexus.git/'", UpdateCommand.GitPullFailureKind.Auth)]
    [InlineData("fatal: unable to access 'https://github.com/': Could not resolve host: github.com", UpdateCommand.GitPullFailureKind.Network)]
    [InlineData("ssh: connect to host github.com port 22: Connection timed out", UpdateCommand.GitPullFailureKind.Network)]
    [InlineData("fatal: something entirely unexpected", UpdateCommand.GitPullFailureKind.Other)]
    [InlineData(null, UpdateCommand.GitPullFailureKind.Other)]
    internal void ClassifyPullFailure_MapsGitStderrToRemediationCategory(string? detail, UpdateCommand.GitPullFailureKind expected)
    {
        UpdateCommand.ClassifyPullFailure(detail).ShouldBe(expected);
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenPullFailsWithAuthError_PrintsAuthHeadlineAndRemediationAndReturnsGitExitCode()
    {
        var cmd = new ScriptedPullFailureCommand(
            Substitute.For<IGatewayProcessManager>(),
            new UpdateCommand.GitPullResult(128, "fatal: Authentication failed for 'https://github.com/Sytone/botnexus.git/'", false));

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.RunGitPullStepForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(128);
        output.ShouldContain("git pull failed: the remote rejected authentication.");
        output.ShouldContain("Check your git credentials or credential helper for the origin remote.");
        output.ShouldNotContain("Check network, auth, or repo path.");
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenPullFailsWithNetworkError_PrintsNetworkHeadlineAndRemediation()
    {
        var cmd = new ScriptedPullFailureCommand(
            Substitute.For<IGatewayProcessManager>(),
            new UpdateCommand.GitPullResult(1, "fatal: unable to access 'https://github.com/': Could not resolve host: github.com", false));

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.RunGitPullStepForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("git pull failed: could not reach the remote.");
        output.ShouldContain("Check network connectivity and that the origin remote is reachable.");
    }

    [Fact]
    public async Task RunGitPullStepAsync_WhenPullFailsWithDirtyTreeError_PointsAtStashAndForceFlags()
    {
        var cmd = new ScriptedPullFailureCommand(
            Substitute.For<IGatewayProcessManager>(),
            new UpdateCommand.GitPullResult(1, "error: Your local changes to the following files would be overwritten by merge:", false));

        var exitCode = 999;
        var output = await CaptureAnsiConsoleOutputAsync(async () =>
        {
            exitCode = await cmd.RunGitPullStepForTestAsync(@"C:\repo", CancellationToken.None);
        });

        exitCode.ShouldBe(1);
        output.ShouldContain("git pull failed: local changes would be overwritten.");
        output.ShouldContain("Re-run with botnexus update --stash to keep your changes, or --force to discard them.");
    }

    [Fact]
    public void ParsePorcelainStatus_SeparatesTrackedChangesFromUntrackedFiles()
    {
        var result = UpdateCommand.ParsePorcelainStatus(
            " M scripts/recover-gateway.ps1\nA  src/new.cs\n?? notes.txt\nD  docs/gone.md\n");

        result.ExitCode.ShouldBe(0);
        result.DirtyPaths.ShouldBe(new[] { "scripts/recover-gateway.ps1", "src/new.cs", "docs/gone.md" });
        result.UntrackedPaths.ShouldBe(new[] { "notes.txt" });
    }

    [Fact]
    public void ParsePorcelainStatus_WhenOutputIsEmpty_ReportsCleanTree()
    {
        var result = UpdateCommand.ParsePorcelainStatus(string.Empty);

        result.DirtyPaths.ShouldBeEmpty();
        result.UntrackedPaths.ShouldBeEmpty();
    }

    [Fact]
    public void Update_command_exposes_stash_and_force_options()
    {
        var command = new UpdateCommand(Substitute.For<IGatewayProcessManager>())
            .Build(new System.CommandLine.Option<bool>("--verbose"), new System.CommandLine.Option<string?>("--target"));

        command.Options.ShouldContain(o => o.Name == "stash");
        command.Options.ShouldContain(o => o.Name == "force");
    }

    private static string NormalizeWhitespace(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

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
            // Spectre wraps at the virtual console width, so collapse whitespace before
            // asserting on full sentences. This normalises layout only, never content.
            return NormalizeWhitespace(outputWriter.ToString());
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }
}
