using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using BotNexus.Cli.Services;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Update command: pull latest source, build, deploy extensions, and restart the gateway.
/// </summary>
internal class UpdateCommand
{
    private const int CancelledExitCode = 130;

    /// <summary>
    /// Distinct exit code for "the deployment repo has uncommitted changes and we refused to touch
    /// them". Deliberately not 1: cron and scripted callers need to tell a dirty deployment tree
    /// apart from an auth or network failure without string-matching git output.
    /// </summary>
    internal const int DirtyWorkingTreeExitCode = 3;

    private readonly IGatewayProcessManager _processManager;

    /// <summary>
    /// How to handle uncommitted changes in the deployment repo before pulling. Set from the
    /// <c>--stash</c> / <c>--force</c> options. Defaults to <see cref="DirtyTreeMode.Abort"/> so
    /// the safe behaviour (never destroy the user's work) is what you get when you say nothing.
    /// </summary>
    internal DirtyTreeMode DirtyTreeHandling { get; set; } = DirtyTreeMode.Abort;

    public UpdateCommand(IGatewayProcessManager processManager)
    {
        _processManager = processManager;
    }

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var sourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var portOption = new Option<int>("--port", () => 5005, "Gateway port.");
        var stashOption = new Option<bool>("--stash", () => false, "If the repo has uncommitted changes, stash them (recoverable) and continue.");
        var forceOption = new Option<bool>("--force", () => false, "If the repo has uncommitted changes, discard tracked-file changes and continue. Destructive.");

        var command = new Command("update", "Pull latest source, build, and restart the BotNexus gateway.")
        {
            sourceOption,
            portOption,
            stashOption,
            forceOption
        };

        command.SetHandler(async context =>
        {
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var port = context.ParseResult.GetValueForOption(portOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var stash = context.ParseResult.GetValueForOption(stashOption);
            var force = context.ParseResult.GetValueForOption(forceOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);

            if (stash && force)
            {
                AnsiConsole.MarkupLine("[red]✗[/] --stash and --force cannot be combined.");
                context.ExitCode = 2;
                return;
            }

            DirtyTreeHandling = force ? DirtyTreeMode.Force : stash ? DirtyTreeMode.Stash : DirtyTreeMode.Abort;
            context.ExitCode = await ExecuteAsync(repoRoot, home, port, verbose, context.GetCancellationToken());
        });

        var checkSourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var checkCommand = new Command("check", "Check whether updates are available from origin/main.")
        {
            checkSourceOption
        };

        checkCommand.SetHandler(async context =>
        {
            var source = context.ParseResult.GetValueForOption(checkSourceOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            context.ExitCode = await CheckAsync(repoRoot, verbose, context.GetCancellationToken());
        });

        command.AddCommand(checkCommand);

        return command;
    }

    internal async Task<int> CheckAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[blue][[update]][/] Checking for updates...");

        var fetchResult = await RunGitFetchAsync(repoRoot, verbose, cancellationToken);
        if (fetchResult.WasCanceled)
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Update check cancelled.");
            return CancelledExitCode;
        }

        if (fetchResult.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Could not fetch updates from origin/main.");
            if (!string.IsNullOrWhiteSpace(fetchResult.FailureDetail))
                AnsiConsole.MarkupLine($"[dim]{CliText.SafeDisplay(fetchResult.FailureDetail)}[/]");
            return 2;
        }

        var behindResult = await GetBehindCountAsync(repoRoot, cancellationToken);
        if (behindResult.WasCanceled)
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Update check cancelled.");
            return CancelledExitCode;
        }

        if (behindResult.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Could not determine update status.");
            if (!string.IsNullOrWhiteSpace(behindResult.FailureDetail))
                AnsiConsole.MarkupLine($"[dim]{CliText.SafeDisplay(behindResult.FailureDetail)}[/]");
            return 2;
        }

        if (behindResult.BehindCount > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]↻[/] Updates available: [bold]{behindResult.BehindCount}[/] commit(s) behind origin/main.");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]✓[/] Already up to date.");
        return 0;
    }

    internal async Task<int> ExecuteAsync(string repoRoot, string home, int port, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Step 1: git pull (safe to do while gateway is running)
        var pullResult = await RunGitPullStepAsync(repoRoot, verbose, cancellationToken);
        if (pullResult != 0)
            return pullResult;

        // Step 1b: if the pull genuinely changed nothing, there is nothing to build and no
        // reason to bounce the gateway. Previously this case still stopped the gateway, ran a
        // full solution build and restarted - minutes of downtime to produce byte-identical
        // output.
        //
        // CONSERVATIVE BY CONSTRUCTION: this is NOT a "did anything change" heuristic over
        // build inputs, and it does not try to reason about which sources are newer than which
        // binaries. It skips ONLY when all four of these hold:
        //   1. HEAD did not move across the pull,
        //   2. the working tree is clean (no uncommitted edit could be waiting to compile),
        //   3. git status was actually readable (an unreadable status is treated as dirty),
        //   4. the gateway binary that would be launched already exists on disk.
        // Anything else - any doubt at all - falls through to the full stop/build/deploy/restart
        // path. A slow update is recoverable; a stale binary after a "successful" update is the
        // silent-failure class this repo has been fighting.
        if (await CanSkipRebuildAsync(repoRoot, cancellationToken))
        {
            // #2772: the old code asserted "gateway left running" from control flow alone. Ask.
            if (IsGatewayRunning(home, repoRoot))
            {
                AnsiConsole.MarkupLine("[green]\u2713[/] Nothing to rebuild; gateway left running.");
                return 0;
            }

            AnsiConsole.MarkupLine("[yellow]\u26a0[/] Nothing to rebuild, but no gateway is running; starting it.");
            return await RunRestartAsync(home, port, repoRoot, cancellationToken);
        }

        // Step 2: Stop gateway BEFORE building — releases file locks on Windows
        var gatewayBinary = ResolveGatewayBinaryPath(repoRoot);
        GatewayStopResult stopResult;
        if (interactive)
        {
            GatewayStopResult capturedStop = new(true, "skipped", GatewayStopOutcome.NotRunning);
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Stopping gateway...", async ctx =>
                {
                    capturedStop = await _processManager.StopAsync(home, gatewayBinary, cancellationToken);
                });
            stopResult = capturedStop;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Stopping gateway...");
            stopResult = await _processManager.StopAsync(home, gatewayBinary, cancellationToken);
        }

        stopResult ??= new GatewayStopResult(false, "no result", GatewayStopOutcome.Failed);

        // #2772: render what was OBSERVED. "Stopped" is only printed when a live gateway process
        // was found and then seen gone; "no gateway found" is a distinct, non-claiming line.
        switch (stopResult.Outcome)
        {
            case GatewayStopOutcome.Stopped:
                AnsiConsole.MarkupLine("[green]\u2713[/] Gateway stopped");
                break;
            case GatewayStopOutcome.NotRunning:
                AnsiConsole.MarkupLine($"[dim]\u2013[/] No running gateway found ({CliText.SafeDisplay(stopResult.Message ?? "not running")}); nothing to stop.");
                break;
            default:
                AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Could not stop gateway ({CliText.SafeDisplay(stopResult.Message ?? "unknown")}). Continuing anyway.");
                break;
        }

        // Wait for the port to be free — confirms the process has fully released file locks.
        // On Windows this can take several seconds after the process exits.
        // #2772: a timeout here is NOT advisory. The port was held by the very process that then
        // locked the DLLs, so proceeding burns a full Release build and fails with misleading
        // guidance. Abort before the build instead.
        bool portFree;
        if (interactive)
        {
            var capturedFree = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("dim"))
                .StartAsync("Waiting for gateway to release file handles...", async ctx =>
                {
                    capturedFree = await WaitForPortFreeAsync(port, cancellationToken);
                });
            portFree = capturedFree;
        }
        else
        {
            portFree = await WaitForPortFreeAsync(port, cancellationToken);
        }

        if (!portFree)
        {
            AnsiConsole.MarkupLine($"[red]\u2717[/] Port {port} is still in use; something is still running the gateway.");
            AnsiConsole.MarkupLine("[yellow]\u26a0[/] Update aborted before building - a build now would fail on locked files.");
            AnsiConsole.MarkupLine("  [dim]Find the owning process and stop it, then re-run botnexus update.[/]");
            return 1;
        }

        // Steps 3 & 4: Build and deploy (gateway is now stopped, no file locks)
        var buildResult = await RunBuildAndDeployAsync(repoRoot, home, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

        // Step 5: Start
        return await RunRestartAsync(home, port, repoRoot, cancellationToken);
    }

    /// <summary>
    /// Runs git pull. Protected virtual so tests can override it.
    /// </summary>
    protected virtual async Task<int> RunGitPullStepAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Step 0: never run `git pull` blind at a deployment repo that has uncommitted work in it.
        // A raw git abort mid-update leaves the gateway on the old build with no guidance (#2492).
        var preflight = await EnsureWorkingTreeReadyAsync(repoRoot, cancellationToken);
        if (preflight != 0)
            return preflight;

        // Step 1: git pull
        string beforeSha;
        string afterSha;
        GitPullResult pullResult;
        int commitCount;
        IReadOnlyList<string> commitSubjects;

        if (interactive)
        {
            string capturedBeforeSha = string.Empty;
            string capturedAfterSha = string.Empty;
            int capturedCount = 0;
            IReadOnlyList<string> capturedSubjects = Array.Empty<string>();
            GitPullResult capturedPullResult = new(1, null, false);

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Checking for updates...", async ctx =>
                {
                    capturedBeforeSha = GetCommitSha(repoRoot);
                    capturedPullResult = await RunGitPullAsync(repoRoot, verbose, cancellationToken);
                    if (capturedPullResult.ExitCode == 0)
                    {
                        capturedAfterSha = GetCommitSha(repoRoot);
                        if (capturedBeforeSha != capturedAfterSha)
                        {
                            capturedCount = await CountCommitsBetweenAsync(repoRoot, capturedBeforeSha, capturedAfterSha, cancellationToken);
                            capturedSubjects = await GetCommitSubjectsBetweenAsync(repoRoot, capturedBeforeSha, capturedAfterSha, cancellationToken);
                        }
                    }
                });

            beforeSha = capturedBeforeSha;
            afterSha = capturedAfterSha;
            pullResult = capturedPullResult;
            commitCount = capturedCount;
            commitSubjects = capturedSubjects;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Checking for updates...");
            beforeSha = GetCommitSha(repoRoot);
            pullResult = await RunGitPullAsync(repoRoot, verbose, cancellationToken);
            afterSha = pullResult.ExitCode == 0 ? GetCommitSha(repoRoot) : string.Empty;
            commitCount = 0;
            commitSubjects = Array.Empty<string>();
            if (pullResult.ExitCode == 0 && beforeSha != afterSha)
            {
                commitCount = await CountCommitsBetweenAsync(repoRoot, beforeSha, afterSha, cancellationToken);
                commitSubjects = await GetCommitSubjectsBetweenAsync(repoRoot, beforeSha, afterSha, cancellationToken);
            }
        }

        if (pullResult.WasCanceled)
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Update cancelled.");
            return CancelledExitCode;
        }

        if (pullResult.ExitCode != 0)
        {
            var kind = ClassifyPullFailure(pullResult.FailureDetail);
            AnsiConsole.MarkupLine($"[red]✗[/] {PullFailureHeadline(kind)}");
            if (!string.IsNullOrWhiteSpace(pullResult.FailureDetail))
                AnsiConsole.MarkupLine($"[dim]{CliText.SafeDisplay(pullResult.FailureDetail)}[/]");
            AnsiConsole.MarkupLine($"[yellow]⚠[/] {PullFailureRemediation(kind, repoRoot)}");

            return pullResult.ExitCode;
        }

        // Record whether this pull actually moved HEAD. Only a successful pull that left HEAD
        // exactly where it was can qualify the update for the no-rebuild fast path.
        LastPullWasNoOp = beforeSha == afterSha && !string.IsNullOrEmpty(beforeSha);

        if (beforeSha == afterSha)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Already up to date ([dim]{CliText.SafeDisplay(Short(beforeSha))}[/])");
        }
        else
        {
            var countStr = commitCount > 0 ? $"{commitCount} new commit(s)" : "new commit(s)";
            AnsiConsole.MarkupLine($"[green]✓[/] Pulled {countStr}: [dim]{CliText.SafeDisplay(Short(beforeSha))}[/] → [dim]{CliText.SafeDisplay(Short(afterSha))}[/]");
            if (commitSubjects.Count > 0)
                PrintChangesApplied(commitSubjects);
        }

        PrintCliUpdateWarningIfNeeded(repoRoot);
        return 0;
    }

    /// <summary>
    /// Runs build and deploy steps. Called after the gateway has been stopped.
    /// Protected virtual so tests can override it.
    /// </summary>
    protected virtual async Task<int> RunBuildAndDeployAsync(string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Build
        int buildResult;
        if (interactive && !verbose)
        {
            int capturedBuild = 0;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Building...", async ctx =>
                {
                    capturedBuild = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
                });
            buildResult = capturedBuild;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Building...");
            buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        }

        if (buildResult != 0)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Build failed.");
            return buildResult;
        }
        AnsiConsole.MarkupLine("[green]✓[/] Build succeeded");

        // Deploy extensions
        int deployed = 0;
        if (interactive)
        {
            int capturedDeployed = 0;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Deploying extensions...", async ctx =>
                {
                    capturedDeployed = ServeCommand.DeployExtensionsSilent(repoRoot, home, verbose);
                    await Task.CompletedTask;
                });
            deployed = capturedDeployed;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Deploying extensions...");
            deployed = ServeCommand.DeployExtensionsSilent(repoRoot, home, verbose);
        }
        AnsiConsole.MarkupLine($"[green]✓[/] {deployed} extension(s) deployed");

        return 0;
    }

    protected virtual async Task<int> RunRestartAsync(string home, int port, string repoRoot, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Verify port is free (gateway was stopped before build in ExecuteAsync)
        if (!IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Port {port} is still in use after stopping gateway.");
            AnsiConsole.MarkupLine("[yellow]⚠[/] Tip: try [dim]botnexus gateway stop[/] manually or kill the process on that port.");
            return 1;
        }

        var gatewayDll = ResolveGatewayBinaryPath(repoRoot);
        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Gateway binary not found: [dim]{CliText.SafeDisplay(gatewayDll)}[/]");
            return 1;
        }

        // The gateway binds gateway.listenUrl when one is configured, overriding the --urls
        // argument below, so probe where it will actually listen rather than where we asked.
        var gatewayUrl = GatewayProbeUrlResolver.ResolveFromConfig(port);
        var options = new GatewayStartOptions(
            ExecutablePath: gatewayDll,
            Arguments: $"--urls \"{gatewayUrl}\" --environment Development",
            Attached: false,
            HomePath: home
        );

        // Start gateway
        GatewayStartResult startResult;
        if (interactive)
        {
            GatewayStartResult capturedStart = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Starting gateway...", async ctx =>
                {
                    capturedStart = await _processManager.StartAsync(options, cancellationToken);
                });
            startResult = capturedStart;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Starting gateway...");
            startResult = await _processManager.StartAsync(options, cancellationToken);
        }

        startResult ??= new GatewayStartResult(false, null, "no result");

        if (startResult.Success && startResult.Pid.HasValue)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Gateway started (PID [yellow]{startResult.Pid.Value}[/])");

            if (interactive)
            {
                var panel = new Panel(
                    $"[green]Update complete![/]\n\n" +
                    $"[dim]URL:[/]  [green]{CliText.SafeDisplay(gatewayUrl)}[/]\n" +
                    $"[dim]PID:[/]  [yellow]{startResult.Pid.Value}[/]")
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader("[bold blue] BotNexus Gateway [/]"),
                    Padding = new Padding(1, 0)
                };
                AnsiConsole.WriteLine();
                AnsiConsole.Write(panel);
            }
            else
            {
                AnsiConsole.MarkupLine($"  URL:  [green]{CliText.SafeDisplay(gatewayUrl)}[/]");
            }
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to start gateway: {CliText.SafeDisplay(startResult.Message ?? "Unknown error")}");
            return 1;
        }
    }

    /// <summary>
    /// Pre-flight guard for the deployment repo working tree. The deployment repo is a deployed
    /// artifact, not a dev worktree, so uncommitted changes there are almost always accidental.
    /// We refuse to run <c>git pull</c> over them and instead report every dirty path plus a
    /// copy-pasteable remediation. Nothing is ever discarded unless the user explicitly asked
    /// (via <c>--force</c> or the interactive prompt), and stashes are named and reported so the
    /// work is always recoverable.
    /// Returns 0 to continue, otherwise the exit code the update should terminate with.
    /// </summary>
    protected virtual async Task<int> EnsureWorkingTreeReadyAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var status = await GetWorkingTreeStatusAsync(repoRoot, cancellationToken);
        if (status.WasCanceled)
        {
            AnsiConsole.MarkupLine("[yellow]\u26a0[/] Update cancelled.");
            return CancelledExitCode;
        }

        if (status.ExitCode != 0)
        {
            // Could not read status (not a repo, git missing). Let the pull path report it.
            return 0;
        }

        if (status.DirtyPaths.Count == 0)
        {
            if (status.UntrackedPaths.Count > 0)
                AnsiConsole.MarkupLine($"[dim]{status.UntrackedPaths.Count} untracked file(s) in the repo; these do not block the update.[/]");
            return 0;
        }

        var mode = DirtyTreeHandling;
        if (mode == DirtyTreeMode.Abort && AnsiConsole.Profile.Capabilities.Interactive)
            mode = PromptDirtyTreeChoice(status.DirtyPaths);

        switch (mode)
        {
            case DirtyTreeMode.Stash:
                return await StashDirtyTreeAsync(repoRoot, status.DirtyPaths, cancellationToken);
            case DirtyTreeMode.Force:
                return await DiscardDirtyTreeAsync(repoRoot, status.DirtyPaths, cancellationToken);
            default:
                PrintDirtyTreeAbort(repoRoot, status.DirtyPaths);
                return DirtyWorkingTreeExitCode;
        }
    }

    private static void PrintDirtyTreeAbort(string repoRoot, IReadOnlyList<string> dirtyPaths)
    {
        AnsiConsole.MarkupLine($"[red]\u2717[/] Update aborted: {dirtyPaths.Count} uncommitted change(s) in the deployment repo.");
        foreach (var path in dirtyPaths)
            AnsiConsole.MarkupLine($"  [yellow]{CliText.SafeDisplay(path)}[/]");
        AnsiConsole.MarkupLine("[yellow]\u26a0[/] Your local changes were left untouched. Choose one:");
        AnsiConsole.MarkupLine("  [dim]botnexus update --stash[/]   keep them (saved to a named stash, recoverable)");
        AnsiConsole.MarkupLine("  [dim]botnexus update --force[/]   discard tracked-file changes and update");
        AnsiConsole.MarkupLine($"  [dim]git -C \"{CliText.SafeDisplay(repoRoot)}\" commit -am \"local changes\"[/]   keep them as a commit");
    }

    private async Task<int> StashDirtyTreeAsync(string repoRoot, IReadOnlyList<string> dirtyPaths, CancellationToken cancellationToken)
    {
        var label = $"botnexus-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var result = await StashChangesAsync(repoRoot, label, cancellationToken);
        if (result.WasCanceled)
        {
            AnsiConsole.MarkupLine("[yellow]\u26a0[/] Update cancelled.");
            return CancelledExitCode;
        }

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]\u2717[/] Could not stash local changes; update aborted.");
            if (!string.IsNullOrWhiteSpace(result.FailureDetail))
                AnsiConsole.MarkupLine($"[dim]{CliText.SafeDisplay(result.FailureDetail)}[/]");
            return DirtyWorkingTreeExitCode;
        }

        AnsiConsole.MarkupLine($"[green]\u2713[/] Stashed {dirtyPaths.Count} local change(s) as [yellow]{CliText.SafeDisplay(label)}[/]");
        AnsiConsole.MarkupLine($"  [dim]git -C \"{CliText.SafeDisplay(repoRoot)}\" stash apply stash^{{/{CliText.SafeDisplay(label)}}}[/]   to restore them");
        return 0;
    }

    private async Task<int> DiscardDirtyTreeAsync(string repoRoot, IReadOnlyList<string> dirtyPaths, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Discarding {dirtyPaths.Count} local change(s) in the deployment repo (--force):");
        foreach (var path in dirtyPaths)
            AnsiConsole.MarkupLine($"  [yellow]{CliText.SafeDisplay(path)}[/]");

        var result = await DiscardChangesAsync(repoRoot, cancellationToken);
        if (result.WasCanceled)
        {
            AnsiConsole.MarkupLine("[yellow]\u26a0[/] Update cancelled.");
            return CancelledExitCode;
        }

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]\u2717[/] Could not discard local changes; update aborted.");
            if (!string.IsNullOrWhiteSpace(result.FailureDetail))
                AnsiConsole.MarkupLine($"[dim]{CliText.SafeDisplay(result.FailureDetail)}[/]");
            return DirtyWorkingTreeExitCode;
        }

        AnsiConsole.MarkupLine($"[green]\u2713[/] Discarded {dirtyPaths.Count} local change(s)");
        return 0;
    }

    /// <summary>
    /// Interactive stash/discard/abort choice. Protected virtual so tests can script the answer
    /// without a TTY. Defaults to abort, which is the non-destructive answer.
    /// </summary>
    protected virtual DirtyTreeMode PromptDirtyTreeChoice(IReadOnlyList<string> dirtyPaths)
    {
        AnsiConsole.MarkupLine($"[yellow]\u26a0[/] The deployment repo has {dirtyPaths.Count} uncommitted change(s):");
        foreach (var path in dirtyPaths)
            AnsiConsole.MarkupLine($"  [yellow]{CliText.SafeDisplay(path)}[/]");

        const string stash = "Stash them (recoverable) and continue";
        const string discard = "Discard local changes and continue";
        const string abort = "Abort the update";

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How do you want to proceed?")
                .AddChoices(stash, discard, abort));

        return choice switch
        {
            stash => DirtyTreeMode.Stash,
            discard => DirtyTreeMode.Force,
            _ => DirtyTreeMode.Abort
        };
    }

    protected virtual Task<GitStatusResult> GetWorkingTreeStatusAsync(string repoRoot, CancellationToken cancellationToken)
        => GetWorkingTreeStatusCoreAsync(repoRoot, cancellationToken);

    private static async Task<GitStatusResult> GetWorkingTreeStatusCoreAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, "status --porcelain", captureOutput: true, cancellationToken);
        if (result.WasCanceled)
            return new GitStatusResult(CancelledExitCode, Array.Empty<string>(), Array.Empty<string>(), null, true);
        if (!result.Started)
            return new GitStatusResult(1, Array.Empty<string>(), Array.Empty<string>(), "Failed to start git process.", false);
        if (result.ExitCode != 0)
        {
            var details = FirstNonEmptyLine(result.Stderr) ?? FirstNonEmptyLine(result.Stdout);
            return new GitStatusResult(result.ExitCode, Array.Empty<string>(), Array.Empty<string>(), details, false);
        }

        return ParsePorcelainStatus(result.Stdout);
    }

    /// <summary>
    /// Splits <c>git status --porcelain</c> output into changes that would block a pull (tracked
    /// modifications, staged changes, deletions, renames) and untracked files, which do not.
    /// </summary>
    internal static GitStatusResult ParsePorcelainStatus(string porcelain)
    {
        var dirty = new List<string>();
        var untracked = new List<string>();

        foreach (var raw in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
                continue;

            var code = raw[..2];
            var path = raw[3..].Trim();
            if (path.Length == 0)
                continue;

            if (code == "??")
                untracked.Add(path);
            else
                dirty.Add(path);
        }

        return new GitStatusResult(0, dirty, untracked, null, false);
    }

    protected virtual Task<GitCommandResult> StashChangesAsync(string repoRoot, string label, CancellationToken cancellationToken)
        => RunGitSimpleAsync(repoRoot, $"stash push -m \"{label}\"", cancellationToken);

    protected virtual Task<GitCommandResult> DiscardChangesAsync(string repoRoot, CancellationToken cancellationToken)
        => RunGitSimpleAsync(repoRoot, "reset --hard HEAD", cancellationToken);

    private static async Task<GitCommandResult> RunGitSimpleAsync(string repoRoot, string arguments, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, arguments, captureOutput: true, cancellationToken);
        if (result.WasCanceled)
            return new GitCommandResult(CancelledExitCode, null, true);
        if (!result.Started)
            return new GitCommandResult(1, "Failed to start git process.", false);
        if (result.ExitCode == 0)
            return new GitCommandResult(0, null, false);

        var details = FirstNonEmptyLine(result.Stderr) ?? FirstNonEmptyLine(result.Stdout);
        return new GitCommandResult(result.ExitCode, details, false);
    }

    /// <summary>
    /// Classifies a git pull failure so the user gets a remediation rather than a raw git line.
    /// A dirty tree, a diverged branch, an auth rejection and a network outage need four different
    /// answers and previously surfaced identically (#2492).
    /// </summary>
    internal static GitPullFailureKind ClassifyPullFailure(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return GitPullFailureKind.Other;

        var text = detail.ToLowerInvariant();

        if (text.Contains("local changes", StringComparison.Ordinal)
            || text.Contains("would be overwritten", StringComparison.Ordinal)
            || text.Contains("commit your changes or stash them", StringComparison.Ordinal))
            return GitPullFailureKind.DirtyTree;

        if (text.Contains("diverged", StringComparison.Ordinal)
            || text.Contains("non-fast-forward", StringComparison.Ordinal)
            || text.Contains("automatic merge failed", StringComparison.Ordinal)
            || text.Contains("fix conflicts", StringComparison.Ordinal))
            return GitPullFailureKind.Diverged;

        if (text.Contains("authentication failed", StringComparison.Ordinal)
            || text.Contains("permission denied", StringComparison.Ordinal)
            || text.Contains("could not read username", StringComparison.Ordinal)
            || text.Contains("invalid username or password", StringComparison.Ordinal)
            || text.Contains("access denied", StringComparison.Ordinal))
            return GitPullFailureKind.Auth;

        if (text.Contains("could not resolve host", StringComparison.Ordinal)
            || text.Contains("connection timed out", StringComparison.Ordinal)
            || text.Contains("connection refused", StringComparison.Ordinal)
            || text.Contains("network is unreachable", StringComparison.Ordinal)
            || text.Contains("failed to connect", StringComparison.Ordinal)
            || text.Contains("unable to access", StringComparison.Ordinal))
            return GitPullFailureKind.Network;

        return GitPullFailureKind.Other;
    }

    internal static string PullFailureHeadline(GitPullFailureKind kind) => kind switch
    {
        GitPullFailureKind.DirtyTree => "git pull failed: local changes would be overwritten.",
        GitPullFailureKind.Diverged => "git pull failed: the local branch has diverged from origin/main.",
        GitPullFailureKind.Auth => "git pull failed: the remote rejected authentication.",
        GitPullFailureKind.Network => "git pull failed: could not reach the remote.",
        _ => "git pull failed."
    };

    internal static string PullFailureRemediation(GitPullFailureKind kind, string repoRoot) => kind switch
    {
        GitPullFailureKind.DirtyTree => "Re-run with botnexus update --stash to keep your changes, or --force to discard them.",
        GitPullFailureKind.Diverged => $"Resolve manually: git -C \"{repoRoot}\" status, then merge or reset to origin/main.",
        GitPullFailureKind.Auth => "Check your git credentials or credential helper for the origin remote.",
        GitPullFailureKind.Network => "Check network connectivity and that the origin remote is reachable.",
        _ => "Check network, auth, or repo path."
    };

    protected virtual Task<GitPullResult> RunGitPullAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
        => RunGitPullCoreAsync(repoRoot, verbose, cancellationToken);

    protected virtual Task<GitCommandResult> RunGitFetchAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
        => RunGitFetchCoreAsync(repoRoot, verbose, cancellationToken);

    private static async Task<GitCommandResult> RunGitFetchCoreAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, "fetch origin main", captureOutput: !verbose, cancellationToken);
        if (result.WasCanceled)
            return new GitCommandResult(CancelledExitCode, null, true);
        if (!result.Started)
            return new GitCommandResult(1, "Failed to start git process.", false);
        if (result.ExitCode == 0)
            return new GitCommandResult(0, null, false);

        var details = FirstNonEmptyLine(result.Stderr) ?? FirstNonEmptyLine(result.Stdout);
        return new GitCommandResult(result.ExitCode, details, false);
    }

    protected virtual Task<GitBehindResult> GetBehindCountAsync(string repoRoot, CancellationToken cancellationToken)
        => GetBehindCountCoreAsync(repoRoot, cancellationToken);

    private static async Task<GitBehindResult> GetBehindCountCoreAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, "rev-list --count HEAD..origin/main", captureOutput: true, cancellationToken);
        if (result.WasCanceled)
            return new GitBehindResult(CancelledExitCode, 0, null, true);
        if (!result.Started)
            return new GitBehindResult(1, 0, "Failed to start git process.", false);
        if (result.ExitCode != 0)
        {
            var details = FirstNonEmptyLine(result.Stderr) ?? FirstNonEmptyLine(result.Stdout);
            return new GitBehindResult(result.ExitCode, 0, details, false);
        }

        if (!int.TryParse(result.Stdout.Trim(), out var count))
            return new GitBehindResult(1, 0, "Unexpected git output while parsing behind count.", false);

        return new GitBehindResult(0, count, null, false);
    }

    private static async Task<GitPullResult> RunGitPullCoreAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, "pull origin main", captureOutput: !verbose, cancellationToken);
        if (result.WasCanceled)
            return new GitPullResult(CancelledExitCode, null, true);
        if (!result.Started)
            return new GitPullResult(1, "Failed to start git process.", false);
        if (result.ExitCode == 0)
            return new GitPullResult(0, null, false);

        var details = FirstNonEmptyLine(result.Stderr) ?? FirstNonEmptyLine(result.Stdout);
        return new GitPullResult(result.ExitCode, details, false);
    }

    protected virtual string GetCommitSha(string repoRoot)
        => GetCommitShaCore(repoRoot);

    private static string GetCommitShaCore(string repoRoot)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{repoRoot}\" rev-parse HEAD",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "unknown";
            var sha = proc.StandardOutput.ReadLine()?.Trim();
            proc.WaitForExit();
            return string.IsNullOrWhiteSpace(sha) ? "unknown" : sha;
        }
        catch
        {
            return "unknown";
        }
    }

    protected virtual Task<int> CountCommitsBetweenAsync(string repoRoot, string from, string to, CancellationToken cancellationToken)
        => CountCommitsBetweenCoreAsync(repoRoot, from, to, cancellationToken);

    private static async Task<int> CountCommitsBetweenCoreAsync(string repoRoot, string from, string to, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, $"rev-list --count {from}..{to}", captureOutput: true, cancellationToken);
        if (result.WasCanceled || !result.Started)
            return 0;
        return int.TryParse(result.Stdout.Trim(), out var n) ? n : 0;
    }

    /// <summary>
    /// Lists commit subjects in the update range so users can see exactly what changed.
    /// </summary>
    protected virtual Task<IReadOnlyList<string>> GetCommitSubjectsBetweenAsync(
        string repoRoot,
        string from,
        string to,
        CancellationToken cancellationToken)
        => GetCommitSubjectsBetweenCoreAsync(repoRoot, from, to, cancellationToken);

    private static async Task<IReadOnlyList<string>> GetCommitSubjectsBetweenCoreAsync(
        string repoRoot,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, $"log --format=%s --reverse {from}..{to}", captureOutput: true, cancellationToken);
        if (result.WasCanceled || !result.Started || result.ExitCode != 0)
            return Array.Empty<string>();

        return result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    /// <summary>
    /// Writes the applied-commit changelog section for update output.
    /// </summary>
    protected virtual void PrintChangesApplied(IReadOnlyList<string> commitSubjects)
    {
        AnsiConsole.MarkupLine("[blue][[update]][/] Changes applied:");
        foreach (var subject in commitSubjects)
            AnsiConsole.MarkupLine($"  - {CliText.SafeDisplay(subject)}");
    }

    /// <summary>
    /// Emits a tool-update recommendation when the source tree version is newer than the running CLI.
    /// </summary>
    protected virtual void PrintCliUpdateWarningIfNeeded(string repoRoot)
    {
        var runningVersion = GetRunningCliVersion();
        var sourceVersion = GetSourceCliVersion(repoRoot);
        if (runningVersion is null || sourceVersion is null || sourceVersion <= runningVersion)
            return;

        AnsiConsole.MarkupLine("[yellow]⚠[/] A newer BotNexus CLI version is available.");
        AnsiConsole.MarkupLine("  [dim]dotnet tool update -g botnexus.cli[/]");
    }

    /// <summary>
    /// Gets the currently running CLI version for comparison against source.
    /// </summary>
    protected virtual Version? GetRunningCliVersion()
    {
        var assembly = typeof(UpdateCommand).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informationalVersion, out var parsedInformationalVersion))
            return parsedInformationalVersion;

        var assemblyVersion = assembly.GetName().Version?.ToString();
        return TryParseVersion(assemblyVersion, out var parsedAssemblyVersion)
            ? parsedAssemblyVersion
            : null;
    }

    /// <summary>
    /// Gets the CLI version declared in the source tree.
    /// </summary>
    protected virtual Version? GetSourceCliVersion(string repoRoot)
    {
        var cliProjectPath = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");

        var versionText = ReadVersionProperty(cliProjectPath, "Version")
            ?? ReadVersionProperty(cliProjectPath, "InformationalVersion")
            ?? ReadVersionProperty(propsPath, "Version")
            ?? ReadVersionProperty(propsPath, "InformationalVersion");

        return TryParseVersion(versionText, out var parsedVersion) ? parsedVersion : null;
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static bool TryParseVersion(string? versionText, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(versionText))
            return false;

        var normalized = versionText.Trim();
        var plusIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
            normalized = normalized[..plusIndex];

        var hyphenIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (hyphenIndex >= 0)
            normalized = normalized[..hyphenIndex];

        if (!Version.TryParse(normalized, out var parsed))
            return false;

        version = parsed;
        return true;
    }

    private static string? ReadVersionProperty(string filePath, string propertyName)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var document = XDocument.Load(filePath);
            return document
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, propertyName, StringComparison.Ordinal))
                ?.Value
                .Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a single <c>git</c> subprocess against <paramref name="repoRoot"/> and returns its
    /// exit code plus captured output. This is the single source of the process-spawn,
    /// output-capture and cancel-kill plumbing shared by every git helper on the update path
    /// (previously copy-pasted five times). When <paramref name="captureOutput"/> is false the
    /// child's stdout/stderr stream straight to the console (interactive/verbose pull/fetch) and
    /// the returned <c>Stdout</c>/<c>Stderr</c> are empty.
    /// </summary>
    /// <remarks>
    /// Getting the cancellation behaviour right matters here: a leaked <c>git</c> child during a
    /// self-update is a bad failure mode, so on cancellation the whole process tree is killed
    /// best-effort before rethrowing as a canceled result.
    /// </remarks>
    private static async Task<GitExec> RunGitAsync(
        string repoRoot,
        string arguments,
        bool captureOutput,
        CancellationToken cancellationToken)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{repoRoot}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput,
                CreateNoWindow = true
            };

            proc = Process.Start(psi);
            if (proc is null)
                return new GitExec(1, string.Empty, string.Empty, WasCanceled: false, Started: false);

            var stdoutTask = captureOutput
                ? proc.StandardOutput.ReadToEndAsync(cancellationToken)
                : Task.FromResult(string.Empty);
            var stderrTask = captureOutput
                ? proc.StandardError.ReadToEndAsync(cancellationToken)
                : Task.FromResult(string.Empty);

            await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(cancellationToken));

            return new GitExec(proc.ExitCode, await stdoutTask, await stderrTask, WasCanceled: false, Started: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (proc is { HasExited: false })
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill to avoid orphaned git processes.
                }
            }

            return new GitExec(CancelledExitCode, string.Empty, string.Empty, WasCanceled: true, Started: proc is not null);
        }
        catch (Exception ex)
        {
            // Surface a process-launch/IO failure (e.g. git not on PATH) as a failed
            // result with the message in Stderr so callers can render a detail line,
            // matching the previous per-helper general catch behaviour.
            return new GitExec(1, string.Empty, ex.Message, WasCanceled: false, Started: proc is not null);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static string? FirstNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var line in text.Split(Environment.NewLine))
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line.Trim();
        }

        return null;
    }

    /// <summary>
    /// Waits until the given port is available (process released it) or timeout elapses.
    /// Polls every 250ms for up to 15 seconds. Returns true when the port became free.
    /// Protected virtual so tests can drive both outcomes without binding a real socket.
    /// </summary>
    /// <remarks>
    /// #3738: the 15-second bound is monotonic. A backwards host clock step would otherwise keep this
    /// poll running past its bound, stalling the update's restart on a port that never frees.
    /// </remarks>
    protected virtual async Task<bool> WaitForPortFreeAsync(int port, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15) && !cancellationToken.IsCancellationRequested)
        {
            if (IsPortAvailable(port))
                return true;
            await Task.Delay(250, cancellationToken);
        }

        return IsPortAvailable(port);
    }

    /// <summary>
    /// Whether a gateway is actually alive for this deployment. Consults the PID file first and
    /// falls back to the same binary-path discovery <c>StopAsync</c> uses, so the skip path can
    /// never claim "gateway left running" about a gateway that is not there (#2772).
    /// Protected virtual so tests can state the answer without a live process.
    /// </summary>
    protected virtual bool IsGatewayRunning(string home, string repoRoot)
        => _processManager.IsRunning(home, ResolveGatewayBinaryPath(repoRoot));

    /// <summary>
    /// Checks if a TCP port is available for binding before starting the new
    /// gateway process. Delegates to <see cref="ServeCommand.IsPortAvailable(int, System.Net.IPAddress?)"/>
    /// so the probe interface stays aligned with the gateway's wildcard bind
    /// (issue #1536) and all CLI call sites share one implementation.
    /// </summary>
    internal static bool IsPortAvailable(int port) => ServeCommand.IsPortAvailable(port);

    /// <summary>
    /// Whether the update can return without stopping, rebuilding, deploying and restarting the
    /// gateway. See the call site in <c>ExecuteAsync</c> for the full reasoning; in short this
    /// returns <c>true</c> only when the repository provably did not move and the artefact that
    /// would be launched already exists. Every uncertain case returns <c>false</c> (= build).
    /// </summary>
    protected virtual async Task<bool> CanSkipRebuildAsync(string repoRoot, CancellationToken cancellationToken)
    {
        // The pull step records whether HEAD moved. If it did, always build.
        if (!LastPullWasNoOp)
            return false;

        // An unreadable or dirty working tree means uncommitted source could be waiting to be
        // compiled into the deployed binary. Build.
        var status = await GetWorkingTreeCleanlinessAsync(repoRoot, cancellationToken);
        if (status != WorkingTreeCleanliness.Clean)
            return false;

        // If the binary we would start is not there, we obviously have to build it.
        return File.Exists(ResolveGatewayBinaryPath(repoRoot));
    }

    /// <summary>
    /// Path of the gateway assembly that <c>RunRestartAsync</c> launches. Centralised so the
    /// skip decision and the start decision can never disagree about which file matters.
    /// </summary>
    internal static string ResolveGatewayBinaryPath(string repoRoot)
        => Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

    /// <summary>
    /// Set by the pull step: true only when <c>git pull</c> succeeded AND HEAD did not move.
    /// Defaults to <c>false</c> so a code path that never ran the pull step cannot accidentally
    /// be treated as "nothing changed".
    /// </summary>
    protected bool LastPullWasNoOp { get; set; }

    /// <summary>Tri-state working-tree result; anything other than Clean forces a build.</summary>
    internal enum WorkingTreeCleanliness
    {
        /// <summary>git status succeeded and reported no tracked modifications.</summary>
        Clean,

        /// <summary>git status succeeded and reported tracked modifications.</summary>
        Dirty,

        /// <summary>git status could not be read at all; treated as dirty.</summary>
        Unknown
    }

    /// <summary>
    /// Reads the tracked-file cleanliness of the repo. Untracked files are ignored - they are
    /// not build inputs for any project and never block a pull - but every tracked modification
    /// and every failure to read status counts against skipping.
    /// Delegates to <see cref="GetWorkingTreeStatusAsync"/> so there is exactly ONE git-status
    /// implementation: a second parser would be free to drift from the dirty/untracked split the
    /// pull path already depends on.
    /// Protected virtual so tests can script the answer without a real repository.
    /// </summary>
    protected virtual async Task<WorkingTreeCleanliness> GetWorkingTreeCleanlinessAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var status = await GetWorkingTreeStatusAsync(repoRoot, cancellationToken);
        if (status.WasCanceled || status.ExitCode != 0)
            return WorkingTreeCleanliness.Unknown;

        return status.DirtyPaths.Count == 0
            ? WorkingTreeCleanliness.Clean
            : WorkingTreeCleanliness.Dirty;
    }

    /// <summary>How uncommitted changes in the deployment repo should be handled before pulling.</summary>
    internal enum DirtyTreeMode
    {
        /// <summary>Do not touch the user's work; report and exit non-zero.</summary>
        Abort,
        /// <summary>Save the work to a named, reported, recoverable stash and continue.</summary>
        Stash,
        /// <summary>Explicitly discard tracked-file changes and continue.</summary>
        Force
    }

    /// <summary>Classification of a <c>git pull</c> failure, used to pick a remediation message.</summary>
    internal enum GitPullFailureKind
    {
        DirtyTree,
        Diverged,
        Auth,
        Network,
        Other
    }

    /// <summary>
    /// Parsed <c>git status --porcelain</c> result. <paramref name="DirtyPaths"/> are changes that
    /// would block a pull; <paramref name="UntrackedPaths"/> are reported but never block.
    /// </summary>
    internal readonly record struct GitStatusResult(
        int ExitCode,
        IReadOnlyList<string> DirtyPaths,
        IReadOnlyList<string> UntrackedPaths,
        string? FailureDetail,
        bool WasCanceled);

    internal readonly record struct GitPullResult(int ExitCode, string? FailureDetail, bool WasCanceled);
    internal readonly record struct GitCommandResult(int ExitCode, string? FailureDetail, bool WasCanceled);
    internal readonly record struct GitBehindResult(int ExitCode, int BehindCount, string? FailureDetail, bool WasCanceled);

    /// <summary>
    /// Result of a single <c>git</c> subprocess invocation run through <see cref="RunGitAsync"/>.
    /// <paramref name="Stdout"/>/<paramref name="Stderr"/> are empty when output capture was
    /// disabled (interactive/verbose pass-through). <paramref name="WasCanceled"/> is set when the
    /// process was killed in response to cancellation. <paramref name="Started"/> is false when the
    /// process failed to start at all.
    /// </summary>
    internal readonly record struct GitExec(int ExitCode, string Stdout, string Stderr, bool WasCanceled, bool Started);
}
