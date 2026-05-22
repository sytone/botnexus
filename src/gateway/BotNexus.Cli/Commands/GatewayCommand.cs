using System.CommandLine;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Gateway lifecycle management commands: start, stop, status, restart, install, uninstall.
/// </summary>
internal sealed class GatewayCommand
{
    private readonly IGatewayProcessManager _processManager;
    private readonly IGatewayServiceInstaller _serviceInstaller;

    public GatewayCommand(
        IGatewayProcessManager processManager,
        IGatewayServiceInstaller serviceInstaller)
    {
        _processManager = processManager;
        _serviceInstaller = serviceInstaller;
    }

    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("gateway", "Manage the BotNexus Gateway lifecycle");

        // Common options for start/restart
        var portOption = new Option<int>("--port", () => 5005, "Port to listen on.");
        var sourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var targetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus.");

        // Start command
        var attachedOption = new Option<bool>("--attached", "Run in foreground instead of detached mode.");
        var startCommand = new Command("start", "Start the gateway process")
        {
            portOption,
            sourceOption,
            targetOption,
            attachedOption
        };
        startCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var attached = context.ParseResult.GetValueForOption(attachedOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await StartAsync(repoRoot, home, port, attached, verbose, context.GetCancellationToken());
        });

        // Stop command
        var stopTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory. Defaults to ~/.botnexus.");
        var stopCommand = new Command("stop", "Stop the gateway process")
        {
            stopTargetOption
        };
        stopCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(stopTargetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await StopAsync(home, verbose, context.GetCancellationToken());
        });

        // Status command
        var statusTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory. Defaults to ~/.botnexus.");
        var statusCommand = new Command("status", "Check gateway process status")
        {
            statusTargetOption
        };
        statusCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(statusTargetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await StatusAsync(home, verbose, context.GetCancellationToken());
        });

        // Restart command
        var restartTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory. Defaults to ~/.botnexus.");
        var restartCommand = new Command("restart", "Restart the gateway process")
        {
            portOption,
            sourceOption,
            restartTargetOption
        };
        restartCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var source = context.ParseResult.GetValueForOption(sourceOption);
            var target = context.ParseResult.GetValueForOption(restartTargetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await RestartAsync(repoRoot, home, port, verbose, context.GetCancellationToken());
        });

        // Install command
        var installPortOption = new Option<int>("--port", () => 5005, "Port to listen on.");
        var installSourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var installTargetOption = new Option<string?>("--target", () => null, "BotNexus home directory. Defaults to ~/.botnexus.");
        var installCommand = new Command("install", "Install the gateway as an OS service (systemd / Windows Service / launchd)")
        {
            installPortOption,
            installSourceOption,
            installTargetOption
        };
        installCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(installPortOption);
            var source = context.ParseResult.GetValueForOption(installSourceOption);
            var target = context.ParseResult.GetValueForOption(installTargetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await InstallServiceAsync(repoRoot, home, port, verbose, context.GetCancellationToken());
        });

        // Uninstall command
        var uninstallCommand = new Command("uninstall", "Remove the gateway OS service registration");
        uninstallCommand.SetHandler(async context =>
        {
            context.ExitCode = await UninstallServiceAsync(context.GetCancellationToken());
        });

        command.AddCommand(startCommand);
        command.AddCommand(stopCommand);
        command.AddCommand(statusCommand);
        command.AddCommand(restartCommand);
        command.AddCommand(installCommand);
        command.AddCommand(uninstallCommand);

        return command;
    }

    private async Task<int> StartAsync(string repoRoot, string home, int port, bool attached, bool verbose, CancellationToken cancellationToken)
    {
        if (attached)
        {
            return await StartAttachedAsync(repoRoot, home, port, verbose, cancellationToken);
        }

        var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

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
            HomePath: home
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

    private async Task<int> StartAttachedAsync(string repoRoot, string home, int port, bool verbose, CancellationToken cancellationToken)
    {
        var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

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
                if (interactive)
                {
                    var content = $"[green]● Running[/]\n\n" +
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
                    AnsiConsole.MarkupLine($"[green]●[/] Gateway is running");
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
        return await StartAsync(repoRoot, home, port, attached: false, verbose, cancellationToken);
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

    private async Task<int> InstallServiceAsync(
        string repoRoot, string home, int port, bool verbose, CancellationToken cancellationToken)
    {
        var status = await _serviceInstaller.GetStatusAsync(cancellationToken);
        if (status.IsInstalled)
        {
            AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Gateway service is already installed ({status.Platform}).");
            return 0;
        }

        var gatewayDll = Path.Combine(
            repoRoot, "src", "gateway", "BotNexus.Gateway.Api",
            "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]\u2715[/] Release build not found. Run [dim]botnexus build[/] first.");
            AnsiConsole.MarkupLine($"  Expected: [dim]{Markup.Escape(gatewayDll)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue][[install]][/] Installing gateway as OS service ({status.Platform})...");

        var result = await _serviceInstaller.InstallAsync(gatewayDll, home, port, cancellationToken);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]\u2713[/] {Markup.Escape(result.Message)}");
            AnsiConsole.MarkupLine($"  [dim]Use [/][yellow]botnexus gateway status[/][dim] to verify.[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]\u2715[/] {Markup.Escape(result.Message)}");
            return 1;
        }
    }

    private async Task<int> UninstallServiceAsync(CancellationToken cancellationToken)
    {
        var status = await _serviceInstaller.GetStatusAsync(cancellationToken);
        if (!status.IsInstalled)
        {
            AnsiConsole.MarkupLine($"[dim]\u25cf Gateway service is not installed.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[blue][[uninstall]][/] Removing gateway OS service...");

        var result = await _serviceInstaller.UninstallAsync(cancellationToken);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]\u2713[/] {Markup.Escape(result.Message)}");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]\u2715[/] {Markup.Escape(result.Message)}");
            return 1;
        }
    }
}
