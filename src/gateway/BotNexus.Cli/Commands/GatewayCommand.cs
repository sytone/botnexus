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

    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("gateway", "Manage the BotNexus Gateway lifecycle");

        // Common options
        var portOption = new Option<int>("--port", () => 5005, "Port to listen on.");
        var pathOption = new Option<string?>("--path", () => null, "Path to the repository root. Defaults to the current directory.");
        var devOption = new Option<bool>("--dev", "Serve from a development repo clone instead of the install location.");

        // Start command
        var attachedOption = new Option<bool>("--attached", "Run in foreground instead of detached mode.");
        var startCommand = new Command("start", "Start the gateway process")
        {
            portOption,
            pathOption,
            devOption,
            attachedOption
        };
        startCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var path = context.ParseResult.GetValueForOption(pathOption);
            var dev = context.ParseResult.GetValueForOption(devOption);
            var attached = context.ParseResult.GetValueForOption(attachedOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = BuildCommand.ResolveRepoRoot(path, dev);
            context.ExitCode = await StartAsync(repoRoot, port, attached, verbose, context.GetCancellationToken());
        });

        // Stop command
        var stopCommand = new Command("stop", "Stop the gateway process");
        stopCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await StopAsync(verbose, context.GetCancellationToken());
        });

        // Status command
        var statusCommand = new Command("status", "Check gateway process status");
        statusCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await StatusAsync(verbose, context.GetCancellationToken());
        });

        // Restart command
        var restartCommand = new Command("restart", "Restart the gateway process")
        {
            portOption,
            pathOption,
            devOption
        };
        restartCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var path = context.ParseResult.GetValueForOption(pathOption);
            var dev = context.ParseResult.GetValueForOption(devOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = BuildCommand.ResolveRepoRoot(path, dev);
            context.ExitCode = await RestartAsync(repoRoot, port, verbose, context.GetCancellationToken());
        });

        command.AddCommand(startCommand);
        command.AddCommand(stopCommand);
        command.AddCommand(statusCommand);
        command.AddCommand(restartCommand);

        return command;
    }

    private async Task<int> StartAsync(string repoRoot, int port, bool attached, bool verbose, CancellationToken cancellationToken)
    {
        // If attached mode, delegate to the old foreground behavior
        if (attached)
        {
            return await StartAttachedAsync(repoRoot, port, verbose, cancellationToken);
        }

        // Build the solution first
        var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Release build not found at: [dim]{Markup.Escape(gatewayDll)}[/]");
            return 1;
        }

        // Auto-initialize ~/.botnexus/ with a default config if none exists
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[blue][[gateway]][/] No configuration found — creating default config...");
            var init = new InitCommand();
            var initResult = await init.ExecuteAsync(force: false, verbose, cancellationToken);
            if (initResult != 0)
                return initResult;
            AnsiConsole.MarkupLine("[blue][[gateway]][/] Configure your gateway via the WebUI at the root URL.");
            AnsiConsole.WriteLine();
        }

        // Deploy extensions
        ServeCommand.DeployExtensions(repoRoot, verbose);

        // Start the gateway using the process manager
        var gatewayUrl = $"http://localhost:{port}";
        var options = new GatewayStartOptions(
            ExecutablePath: gatewayDll,
            Arguments: $"--urls \"{gatewayUrl}\" --environment Development",
            Attached: false
        );

        var result = await _processManager.StartAsync(options, cancellationToken);

        if (result.Success && result.Pid.HasValue)
        {
            var logsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".botnexus", "logs", "gateway.log");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]✓[/] Gateway started (PID [yellow]{result.Pid.Value}[/])");
            AnsiConsole.MarkupLine($"  URL:  [green]{Markup.Escape(gatewayUrl)}[/]");
            AnsiConsole.MarkupLine($"  Logs: [dim]{Markup.Escape(logsPath)}[/]");
            AnsiConsole.MarkupLine($"  Stop: [dim]botnexus gateway stop[/]");
            AnsiConsole.WriteLine();
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to start gateway: {Markup.Escape(result.Message ?? "Unknown error")}");
            return 1;
        }
    }

    private async Task<int> StartAttachedAsync(string repoRoot, int port, bool verbose, CancellationToken cancellationToken)
    {
        // This is the original foreground behavior from ServeCommand
        var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Release build not found at: [dim]{Markup.Escape(gatewayDll)}[/]");
            return 1;
        }

        // Auto-initialize ~/.botnexus/ with a default config if none exists
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[blue][[gateway]][/] No configuration found — creating default config...");
            var init = new InitCommand();
            var initResult = await init.ExecuteAsync(force: false, verbose, cancellationToken);
            if (initResult != 0)
                return initResult;
            AnsiConsole.MarkupLine("[blue][[gateway]][/] Configure your gateway via the WebUI at the root URL.");
            AnsiConsole.WriteLine();
        }

        if (!ServeCommand.IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Port [green]{port}[/] is already in use.");
            return 1;
        }

        ServeCommand.DeployExtensions(repoRoot, verbose);

        var gatewayUrl = $"http://localhost:{port}";
        var lastExitCode = 0;

        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue][[gateway]][/] Starting Gateway (attached mode)");
            AnsiConsole.MarkupLine($"   URL:         [green]{Markup.Escape(gatewayUrl)}[/]");
            AnsiConsole.MarkupLine("   Environment: [dim]Development[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Press [yellow]Ctrl+C[/] to stop the gateway.");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{gatewayDll}\"",
                UseShellExecute = false
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["ASPNETCORE_URLS"] = gatewayUrl;

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Gateway process.");

            await process.WaitForExitAsync(cancellationToken);
            lastExitCode = process.ExitCode;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[blue][[gateway]][/] Gateway exited (code [yellow]{lastExitCode}[/]).");

            if (cancellationToken.IsCancellationRequested)
                break;

            if (!await ServeCommand.WaitForRestartOrQuitAsync(5, cancellationToken))
                break;
        }

        return lastExitCode;
    }

    private async Task<int> StopAsync(bool verbose, CancellationToken cancellationToken)
    {
        var result = await _processManager.StopAsync(cancellationToken);

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

    private async Task<int> StatusAsync(bool verbose, CancellationToken cancellationToken)
    {
        var status = await _processManager.GetStatusAsync(cancellationToken);

        switch (status.State)
        {
            case GatewayState.Running when status.Pid.HasValue:
                AnsiConsole.MarkupLine($"[green]●[/] Gateway is running");
                AnsiConsole.MarkupLine($"  PID:    [yellow]{status.Pid.Value}[/]");
                if (status.Uptime.HasValue)
                {
                    AnsiConsole.MarkupLine($"  Uptime: [dim]{FormatUptime(status.Uptime.Value)}[/]");
                }
                return 0;

            case GatewayState.NotRunning:
                AnsiConsole.MarkupLine("[dim]●[/] Gateway is not running");
                if (verbose && !string.IsNullOrWhiteSpace(status.Message))
                {
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(status.Message)}[/]");
                }
                return 0;

            case GatewayState.Unknown:
                AnsiConsole.MarkupLine($"[yellow]●[/] Gateway state is unknown");
                if (!string.IsNullOrWhiteSpace(status.Message))
                {
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(status.Message)}[/]");
                }
                return 1;

            default:
                AnsiConsole.MarkupLine("[yellow]●[/] Gateway state is unknown");
                return 1;
        }
    }

    private async Task<int> RestartAsync(string repoRoot, int port, bool verbose, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[blue][[gateway]][/] Stopping gateway...");
        var stopResult = await StopAsync(verbose, cancellationToken);

        // Wait a moment for cleanup
        await Task.Delay(1000, cancellationToken);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue][[gateway]][/] Starting gateway...");
        return await StartAsync(repoRoot, port, attached: false, verbose, cancellationToken);
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
}
