using System.CommandLine;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Gateway lifecycle management commands: start, stop, status, restart.
/// </summary>
internal sealed class GatewayCommand
{
    private readonly IGatewayProcessManager _processManager;

    public GatewayCommand(IGatewayProcessManager processManager)
    {
        _processManager = processManager;
    }

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("gateway", "Manage the BotNexus Gateway lifecycle (start detached, stop, status, restart). For foreground/dev mode use 'serve' or 'serve gateway'");

        // Common options for start/restart
        var portOption = new Option<int>("--port", () => 5005, "Port to listen on.");
        var sourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");

        // Start command
        var attachedOption = new Option<bool>("--attached", "Run in foreground instead of detached mode.");
        var skipBuildOption = new Option<bool>("--skip-build", "Skip the implicit solution rebuild before starting. Use this when you've already built (e.g. CI, tests, or `dotnet build` ran moments ago) — otherwise the rebuild step will fight for file locks if any binary is in use.");
        var startCommand = new Command("start", "Start the gateway process")
        {
            portOption,
            sourceOption,
            attachedOption,
            skipBuildOption
        };
        startCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var attached = context.ParseResult.GetValueForOption(attachedOption);
            var skipBuild = context.ParseResult.GetValueForOption(skipBuildOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await StartAsync(repoRoot, home, port, attached, verbose, skipBuild, context.GetCancellationToken());
        });

        // Stop command
        var stopCommand = new Command("stop", "Stop the gateway process");
        stopCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await StopAsync(home, verbose, context.GetCancellationToken());
        });

        // Status command
        var statusCommand = new Command("status", "Check gateway process status");
        statusCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await StatusAsync(home, verbose, context.GetCancellationToken());
        });

        // Restart command
        var restartCommand = new Command("restart", "Restart the gateway process")
        {
            portOption,
            sourceOption
        };
        restartCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await RestartAsync(repoRoot, home, port, verbose, context.GetCancellationToken());
        });

        command.AddCommand(startCommand);
        command.AddCommand(stopCommand);
        command.AddCommand(statusCommand);
        command.AddCommand(restartCommand);

        // Service install/uninstall commands
        var installCommand = new Command("install", "Install the gateway as an OS service (systemd/Windows Service/launchd)")
        {
            portOption,
            sourceOption
        };
        installCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await InstallServiceAsync(repoRoot, home, port, verbose, context.GetCancellationToken());
        });

        var uninstallCommand = new Command("uninstall", "Stop and remove the gateway OS service");
        uninstallCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await UninstallServiceAsync(verbose, context.GetCancellationToken());
        });

        command.AddCommand(installCommand);
        command.AddCommand(uninstallCommand);

        return command;
    }

    private async Task<int> StartAsync(string repoRoot, string home, int port, bool attached, bool verbose, bool skipBuild, CancellationToken cancellationToken)
    {
        if (attached)
        {
            return await StartAttachedAsync(repoRoot, home, port, verbose, skipBuild, cancellationToken);
        }

        if (!skipBuild)
        {
            var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
            if (buildResult != 0)
                return buildResult;
        }

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Release build not found at: [dim]{Markup.Escape(gatewayDll)}[/]");
            return 1;
        }

        var configPath = Path.Combine(home, "config.json");
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[blue][[gateway]][/] No configuration found — creating default config...");
            var init = new InitCommand();
            var initResult = await init.ExecuteAsync(force: false, verbose, cancellationToken);
            if (initResult != 0)
                return initResult;
            AnsiConsole.MarkupLine("[dim]Configure your gateway via the WebUI at the root URL.[/]");
            AnsiConsole.WriteLine();
        }

        ServeCommand.DeployExtensions(repoRoot, home, verbose);

        var gatewayUrl = $"http://localhost:{port}";
        var options = new GatewayStartOptions(
            ExecutablePath: gatewayDll,
            Arguments: $"--urls \"{gatewayUrl}\" --environment Development",
            Attached: false,
            HomePath: home,
            HealthUrl: $"{gatewayUrl}/health"
        );

        GatewayStartResult result;
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        if (interactive)
        {
            GatewayStartResult capturedResult = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Starting gateway...", async ctx =>
                {
                    capturedResult = await _processManager.StartAsync(options, cancellationToken);
                });
            result = capturedResult;
        }
        else
        {
            result = await _processManager.StartAsync(options, cancellationToken);
        }

        if (result.Success && result.Pid.HasValue)
        {
            var logsPath = Path.Combine(home, "logs", "gateway.log");
            AnsiConsole.WriteLine();

            if (interactive)
            {
                var content =
                    $"[green]✓[/] Running  [dim]PID:[/] [yellow]{result.Pid.Value}[/]\n\n" +
                    $"[dim]URL:[/]   [green]{Markup.Escape(gatewayUrl)}[/]\n" +
                    $"[dim]Logs:[/]  [dim]{Markup.Escape(logsPath)}[/]\n" +
                    $"[dim]Stop:[/]  [dim]botnexus gateway stop[/]";
                var panel = new Panel(content)
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader("[bold blue] BotNexus Gateway [/]"),
                    Padding = new Padding(1, 0)
                };
                AnsiConsole.Write(panel);
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Gateway started (PID [yellow]{result.Pid.Value}[/])");
                AnsiConsole.MarkupLine($"  URL:  [green]{Markup.Escape(gatewayUrl)}[/]");
                AnsiConsole.MarkupLine($"  Logs: [dim]{Markup.Escape(logsPath)}[/]");
                AnsiConsole.MarkupLine($"  Stop: [dim]botnexus gateway stop[/]");
            }

            AnsiConsole.WriteLine();
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to start gateway: {Markup.Escape(result.Message ?? "Unknown error")}");
            return 1;
        }
    }

    private async Task<int> StartAttachedAsync(string repoRoot, string home, int port, bool verbose, bool skipBuild, CancellationToken cancellationToken)
    {
        if (!skipBuild)
        {
            var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
            if (buildResult != 0)
                return buildResult;
        }

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Release build not found at: [dim]{Markup.Escape(gatewayDll)}[/]");
            return 1;
        }

        var configPath = Path.Combine(home, "config.json");
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[blue][[gateway]][/] No configuration found — creating default config...");
            var init = new InitCommand();
            var initResult = await init.ExecuteAsync(force: false, verbose, cancellationToken);
            if (initResult != 0)
                return initResult;
            AnsiConsole.MarkupLine("[dim]Configure your gateway via the WebUI at the root URL.[/]");
            AnsiConsole.WriteLine();
        }

        if (!ServeCommand.IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Port [yellow]{port}[/] is already in use.");
            return 1;
        }

        ServeCommand.DeployExtensions(repoRoot, home, verbose);

        var gatewayUrl = $"http://localhost:{port}";
        var lastExitCode = 0;

        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold blue]BotNexus Gateway[/]") { Justification = Justify.Left });
            AnsiConsole.MarkupLine($"  [dim]URL:[/]         [green]{Markup.Escape(gatewayUrl)}[/]");
            AnsiConsole.MarkupLine("  [dim]Environment:[/] Development");
            AnsiConsole.MarkupLine("  Press [yellow]Ctrl+C[/] to stop the gateway.");
            AnsiConsole.WriteLine();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{gatewayDll}\"",
                UseShellExecute = false
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["ASPNETCORE_URLS"] = gatewayUrl;
            psi.Environment["BOTNEXUS_HOME"] = home;
            ServeCommand.ApplyCrashDumpEnvironment(psi, home);

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Gateway process.");

            await process.WaitForExitAsync(cancellationToken);
            lastExitCode = process.ExitCode;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Gateway exited (code [yellow]{lastExitCode}[/]).[/]");

            if (cancellationToken.IsCancellationRequested)
                break;

            if (!await ServeCommand.WaitForRestartOrQuitAsync(5, cancellationToken))
                break;
        }

        return lastExitCode;
    }

    private async Task<int> StopAsync(string home, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        GatewayStopResult result;

        if (interactive)
        {
            GatewayStopResult capturedResult = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Stopping gateway...", async ctx =>
                {
                    capturedResult = await _processManager.StopAsync(home, cancellationToken);
                });
            result = capturedResult;
        }
        else
        {
            result = await _processManager.StopAsync(home, cancellationToken);
        }

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(result.Message ?? "Gateway stopped")}");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Message ?? "Failed to stop gateway")}");
            return 1;
        }
    }

    private async Task<int> StatusAsync(string home, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        GatewayStatus status;

        if (interactive)
        {
            GatewayStatus capturedStatus = default!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Checking gateway status...", async ctx =>
                {
                    capturedStatus = await _processManager.GetStatusAsync(home, cancellationToken);
                });
            status = capturedStatus;
        }
        else
        {
            status = await _processManager.GetStatusAsync(home, cancellationToken);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold blue]Gateway Status[/]") { Justification = Justify.Left });

        switch (status.State)
        {
            case GatewayState.Running when status.Pid.HasValue:
                if (status.ProbeResult == GatewayProbeResult.ReachableNoAuth)
                {
                    // Process is alive but gateway is rejecting requests with 401/403.
                    // Distinguish clearly from healthy -- user needs to fix auth config.
                    if (interactive)
                    {
                        var authContent =
                            $"[yellow]\u25cf Running (auth error)[/]\n\n" +
                            $"[dim]PID:[/]    [yellow]{status.Pid.Value}[/]\n" +
                            (status.Uptime.HasValue ? $"[dim]Uptime:[/] [dim]{FormatUptime(status.Uptime.Value)}[/]\n" : string.Empty) +
                            "[yellow]Warning:[/] Gateway returned HTTP 401/403 -- API token may be missing or invalid.\n" +
                            "[dim]Check your config.json apiKey or set BOTNEXUS_API_KEY if required.[/]";
                        AnsiConsole.Write(new Panel(authContent.TrimEnd())
                        {
                            Border = BoxBorder.Rounded,
                            Padding = new Padding(1, 0)
                        });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]\u25cf[/] Gateway is running (auth error)");
                        AnsiConsole.MarkupLine($"  PID:    [yellow]{status.Pid.Value}[/]");
                        if (status.Uptime.HasValue)
                            AnsiConsole.MarkupLine($"  Uptime: [dim]{FormatUptime(status.Uptime.Value)}[/]");
                        AnsiConsole.MarkupLine("[yellow]Warning:[/] Gateway returned HTTP 401/403 -- API token may be missing or invalid.");
                    }
                    return 2; // distinct exit code: running but not authenticated
                }

                if (interactive)
                {
                    var content = $"[green]\u25cf Running[/]\n\n" +
                        $"[dim]PID:[/]    [yellow]{status.Pid.Value}[/]\n" +
                        (status.Uptime.HasValue ? $"[dim]Uptime:[/] [dim]{FormatUptime(status.Uptime.Value)}[/]" : string.Empty);
                    AnsiConsole.Write(new Panel(content.TrimEnd())
                    {
                        Border = BoxBorder.Rounded,
                        Padding = new Padding(1, 0)
                    });
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]\u25cf[/] Gateway is running");
                    AnsiConsole.MarkupLine($"  PID:    [yellow]{status.Pid.Value}[/]");
                    if (status.Uptime.HasValue)
                        AnsiConsole.MarkupLine($"  Uptime: [dim]{FormatUptime(status.Uptime.Value)}[/]");
                }
                return 0;

            case GatewayState.NotRunning:
                if (interactive)
                {
                    AnsiConsole.Write(new Panel("[dim]● Not running[/]")
                    {
                        Border = BoxBorder.Rounded,
                        Padding = new Padding(1, 0)
                    });
                }
                else
                {
                    AnsiConsole.MarkupLine("[dim]● Gateway is not running[/]");
                    if (verbose && !string.IsNullOrWhiteSpace(status.Message))
                        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(status.Message)}[/]");
                }
                return 0;

            case GatewayState.Unknown:
            default:
                AnsiConsole.MarkupLine($"[yellow]●[/] Gateway state is unknown");
                if (!string.IsNullOrWhiteSpace(status.Message))
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(status.Message)}[/]");
                return 1;
        }
    }

    private async Task<int> RestartAsync(string repoRoot, string home, int port, bool verbose, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        // Stop
        GatewayStopResult stopResult;
        if (interactive)
        {
            GatewayStopResult capturedStop = default!;
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
            AnsiConsole.MarkupLine("[blue][[gateway]][/] Stopping gateway...");
            stopResult = await _processManager.StopAsync(home, cancellationToken);
        }

        if (stopResult.Success)
            AnsiConsole.MarkupLine("[green]✓[/] Gateway stopped");
        else
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Stop result: {Markup.Escape(stopResult.Message ?? "unknown")}");

        await Task.Delay(1000, cancellationToken);

        // Start
        return await StartAsync(repoRoot, home, port, attached: false, verbose, skipBuild: false, cancellationToken);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
        if (uptime.TotalMinutes >= 1)
            return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
        return $"{uptime.Seconds}s";
    }

    private static async Task<int> InstallServiceAsync(string repoRoot, string home, int port, bool verbose, CancellationToken cancellationToken)
    {
        var manager = OsServiceManagerFactory.Create();
        if (manager is null)
        {
            AnsiConsole.MarkupLine("[red]\u2717[/] OS service installation is not supported on this platform.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue][[gateway]][/] Installing as {manager.ServiceManagerName}...");

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");
        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]\u2717[/] Release build not found at: [dim]{Markup.Escape(gatewayDll)}[/]");
            AnsiConsole.MarkupLine("[dim]Run 'dotnet build -c Release' first.[/]");
            return 1;
        }

        var result = await manager.InstallAsync(gatewayDll, home, port, cancellationToken);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]\u2713[/] {Markup.Escape(result.Message)}");
            if (verbose)
            {
                AnsiConsole.MarkupLine($"  [dim]Manager:[/] {manager.ServiceManagerName}");
                AnsiConsole.MarkupLine($"  [dim]Binary:[/]  {Markup.Escape(gatewayDll)}");
                AnsiConsole.MarkupLine($"  [dim]Home:[/]    {Markup.Escape(home)}");
                AnsiConsole.MarkupLine($"  [dim]Port:[/]    {port}");
            }
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]\u2717[/] {Markup.Escape(result.Message)}");
        return 1;
    }

    private static async Task<int> UninstallServiceAsync(bool verbose, CancellationToken cancellationToken)
    {
        var manager = OsServiceManagerFactory.Create();
        if (manager is null)
        {
            AnsiConsole.MarkupLine("[red]\u2717[/] OS service management is not supported on this platform.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue][[gateway]][/] Removing {manager.ServiceManagerName} service...");

        var result = await manager.UninstallAsync(cancellationToken);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]\u2713[/] {Markup.Escape(result.Message)}");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]\u2717[/] {Markup.Escape(result.Message)}");
        return 1;
    }
}
