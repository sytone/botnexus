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
    private readonly IGatewayProcessManager _processManager;

    public UpdateCommand(IGatewayProcessManager processManager)
    {
        _processManager = processManager;
    }

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var sourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var portOption = new Option<int>("--port", () => 5005, "Gateway port.");

        var command = new Command("update", "Pull latest source, build, and restart the BotNexus gateway.")
        {
            sourceOption,
            portOption
        };

        command.SetHandler(async context =>
        {
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var port = context.ParseResult.GetValueForOption(portOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
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
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(fetchResult.FailureDetail)}[/]");
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
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(behindResult.FailureDetail)}[/]");
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

        // Step 2: Stop gateway BEFORE building — releases file locks on Windows
        GatewayStopResult stopResult;
        if (interactive)
        {
            GatewayStopResult capturedStop = new(true, "skipped");
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Stopping gateway...", async ctx =>
                {
                    capturedStop = await _processManager.StopAsync(home, cancellationToken);
                });
            stopResult = capturedStop;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Stopping gateway...");
            stopResult = await _processManager.StopAsync(home, cancellationToken);
        }

        stopResult ??= new GatewayStopResult(false, "no result");

        if (!stopResult.Success)
            AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Could not stop gateway ({Markup.Escape(stopResult.Message ?? "not running")}). Continuing anyway.");
        else
            AnsiConsole.MarkupLine("[green]\u2713[/] Gateway stopped");

        // Wait for the port to be free — confirms the process has fully released file locks.
        // On Windows this can take several seconds after the process exits.
        if (interactive)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("dim"))
                .StartAsync("Waiting for gateway to release file handles...", async ctx =>
                {
                    await WaitForPortFreeAsync(port, cancellationToken);
                });
        }
        else
        {
            await WaitForPortFreeAsync(port, cancellationToken);
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
            AnsiConsole.MarkupLine("[red]✗[/] git pull failed.");
            if (!string.IsNullOrWhiteSpace(pullResult.FailureDetail))
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(pullResult.FailureDetail)}[/]");
            else
                AnsiConsole.MarkupLine("[yellow]⚠[/] Check network, auth, or repo path.");

            return pullResult.ExitCode;
        }

        if (beforeSha == afterSha)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Already up to date ([dim]{Markup.Escape(Short(beforeSha))}[/])");
        }
        else
        {
            var countStr = commitCount > 0 ? $"{commitCount} new commit(s)" : "new commit(s)";
            AnsiConsole.MarkupLine($"[green]✓[/] Pulled {countStr}: [dim]{Markup.Escape(Short(beforeSha))}[/] → [dim]{Markup.Escape(Short(afterSha))}[/]");
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

    private async Task<int> RunRestartAsync(string home, int port, string repoRoot, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Verify port is free (gateway was stopped before build in ExecuteAsync)
        if (!IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Port {port} is still in use after stopping gateway.");
            AnsiConsole.MarkupLine("[yellow]⚠[/] Tip: try [dim]botnexus gateway stop[/] manually or kill the process on that port.");
            return 1;
        }

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");
        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Gateway binary not found: [dim]{Markup.Escape(gatewayDll)}[/]");
            return 1;
        }

        var gatewayUrl = $"http://localhost:{port}";
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
                    $"[dim]URL:[/]  [green]{Markup.Escape(gatewayUrl)}[/]\n" +
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
                AnsiConsole.MarkupLine($"  URL:  [green]{Markup.Escape(gatewayUrl)}[/]");
            }
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to start gateway: {Markup.Escape(startResult.Message ?? "Unknown error")}");
            return 1;
        }
    }

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
            AnsiConsole.MarkupLine($"  - {Markup.Escape(subject)}");
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
    /// Polls every 250ms for up to 15 seconds.
    /// </summary>
    private static async Task WaitForPortFreeAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (IsPortAvailable(port))
                return;
            await Task.Delay(250, cancellationToken);
        }
        // If still not free, proceed anyway — build may still succeed if it's a different process
    }

    /// <summary>
    /// Checks if a TCP port is available for binding before starting the new
    /// gateway process. Delegates to <see cref="ServeCommand.IsPortAvailable(int, System.Net.IPAddress?)"/>
    /// so the probe interface stays aligned with the gateway's wildcard bind
    /// (issue #1536) and all CLI call sites share one implementation.
    /// </summary>
    internal static bool IsPortAvailable(int port) => ServeCommand.IsPortAvailable(port);

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
