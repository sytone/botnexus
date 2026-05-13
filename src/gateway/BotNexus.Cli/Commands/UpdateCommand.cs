using System.CommandLine;
using System.Diagnostics;
using System.Net.Sockets;
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

    public Command Build(Option<bool> verboseOption)
    {
        var sourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var targetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");
        var portOption = new Option<int>("--port", () => 5005, "Gateway port.");

        var command = new Command("update", "Pull latest source, build, and restart the BotNexus gateway.")
        {
            sourceOption,
            targetOption,
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

        return command;
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

    private static async Task<GitPullResult> RunGitPullCoreAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{repoRoot}\" pull origin main",
                UseShellExecute = false,
                RedirectStandardOutput = !verbose,
                RedirectStandardError = !verbose,
                CreateNoWindow = true
            };

            proc = Process.Start(psi);
            if (proc is null)
                return new GitPullResult(1, "Failed to start git process.", false);

            var stdoutTask = verbose
                ? Task.FromResult(string.Empty)
                : proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = verbose
                ? Task.FromResult(string.Empty)
                : proc.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(cancellationToken));

            if (proc.ExitCode == 0)
                return new GitPullResult(0, null, false);

            var stderr = await stderrTask;
            var stdout = await stdoutTask;
            var details = FirstNonEmptyLine(stderr) ?? FirstNonEmptyLine(stdout);
            return new GitPullResult(proc.ExitCode, details, false);
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

            return new GitPullResult(CancelledExitCode, null, true);
        }
        catch (Exception ex)
        {
            return new GitPullResult(1, ex.Message, false);
        }
        finally
        {
            proc?.Dispose();
        }
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
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{repoRoot}\" rev-list --count {from}..{to}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return 0;
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            return int.TryParse(output.Trim(), out var n) ? n : 0;
        }
        catch
        {
            return 0;
        }
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
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{repoRoot}\" log --format=%s --reverse {from}..{to}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<string>();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            if (proc.ExitCode != 0)
                return Array.Empty<string>();

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
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

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

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
    /// Checks if a TCP port is available for binding. Mirrors ServeCommand.IsPortAvailable.
    /// Used to verify the port is free before starting the new gateway process.
    /// </summary>
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

    internal static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected readonly record struct GitPullResult(int ExitCode, string? FailureDetail, bool WasCanceled);
}
