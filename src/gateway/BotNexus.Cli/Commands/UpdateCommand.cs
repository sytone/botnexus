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

        // Step 1: git pull
        var pullResult = await RunPreStopStepsAsync(repoRoot, home, verbose, cancellationToken);
        if (pullResult != 0)
            return pullResult;

        // Step 2: Stop gateway BEFORE building to release file locks on Windows
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

        if (!stopResult.Success)
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Could not stop gateway ({Markup.Escape(stopResult.Message ?? "not running")}). Continuing anyway.");
        else
            AnsiConsole.MarkupLine("[green]✓[/] Gateway stopped");

        // Small grace period for file handles to release on Windows
        await Task.Delay(500, cancellationToken);

        // Steps 3–5: build, deploy, start
        return await RunRestartAsync(home, port, repoRoot, cancellationToken);
    }

    /// <summary>
    /// Runs git pull, build, and extension deploy steps before the gateway restart.
    /// Protected virtual so tests can override it to skip real git/build operations.
    /// </summary>
    protected virtual async Task<int> RunPreStopStepsAsync(string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Step 1: git pull
        string beforeSha;
        string afterSha;
        int pullResult;
        int commitCount;

        if (interactive)
        {
            string capturedBeforeSha = string.Empty;
            string capturedAfterSha = string.Empty;
            int capturedCount = 0;
            int capturedPullResult = 0;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Checking for updates...", async ctx =>
                {
                    capturedBeforeSha = GetCommitSha(repoRoot);
                    capturedPullResult = await RunGitPullAsync(repoRoot, verbose, cancellationToken);
                    if (capturedPullResult == 0)
                    {
                        capturedAfterSha = GetCommitSha(repoRoot);
                        if (capturedBeforeSha != capturedAfterSha)
                            capturedCount = await CountCommitsBetweenAsync(repoRoot, capturedBeforeSha, capturedAfterSha, cancellationToken);
                    }
                });

            beforeSha = capturedBeforeSha;
            afterSha = capturedAfterSha;
            pullResult = capturedPullResult;
            commitCount = capturedCount;
        }
        else
        {
            AnsiConsole.MarkupLine("[blue][[update]][/] Checking for updates...");
            beforeSha = GetCommitSha(repoRoot);
            pullResult = await RunGitPullAsync(repoRoot, verbose, cancellationToken);
            afterSha = pullResult == 0 ? GetCommitSha(repoRoot) : string.Empty;
            commitCount = 0;
            if (pullResult == 0 && beforeSha != afterSha)
                commitCount = await CountCommitsBetweenAsync(repoRoot, beforeSha, afterSha, cancellationToken);
        }

        if (pullResult != 0)
        {
            AnsiConsole.MarkupLine("[red]✗[/] git pull failed. Check network or repo path.");
            return pullResult;
        }

        if (beforeSha == afterSha)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Already up to date ([dim]{Markup.Escape(Short(beforeSha))}[/])");
        }
        else
        {
            var countStr = commitCount > 0 ? $"{commitCount} new commit(s)" : "new commit(s)";
            AnsiConsole.MarkupLine($"[green]✓[/] Pulled {countStr}: [dim]{Markup.Escape(Short(beforeSha))}[/] → [dim]{Markup.Escape(Short(afterSha))}[/]");
        }

        // Step 2: Build
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

        // Step 3: Deploy extensions
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

    private static async Task<int> RunGitPullAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
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

            using var proc = Process.Start(psi);
            if (proc is null) return 1;

            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] git pull error: {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static string GetCommitSha(string repoRoot)
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

    private static async Task<int> CountCommitsBetweenAsync(string repoRoot, string from, string to, CancellationToken cancellationToken)
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

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    /// <summary>
    /// Checks if a TCP port is available for binding. Mirrors ServeCommand.IsPortAvailable.
    /// Used to verify the port is free before starting the new gateway process.
    /// </summary>
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
}
