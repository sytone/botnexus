using System.CommandLine;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class ServeCommand
{
    private readonly GatewayCommand _gatewayCommand;

    public ServeCommand(GatewayCommand gatewayCommand)
    {
        _gatewayCommand = gatewayCommand;
    }

    public Command Build(Option<bool> verboseOption)
    {
        // Gateway lifecycle management
        var gatewayCommand = _gatewayCommand.Build(verboseOption);

        var probePortOption = new Option<int>("--port", () => 5050, "Port for the Probe web UI.");
        var probeRepoOption = new Option<string?>("--path", () => null, "Path to the repository root. Defaults to the current directory.");
        var probeDevOption = new Option<bool>("--dev", "Serve from a development repo clone instead of the install location.");
        var gatewayUrlOption = new Option<string>("--gateway-url", () => "http://localhost:5005", "URL of a running BotNexus Gateway.");

        var probeCommand = new Command("probe", "Start the BotNexus Probe diagnostic tool.")
        {
            probePortOption,
            probeRepoOption,
            probeDevOption,
            gatewayUrlOption
        };
        probeCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(probePortOption);
            var path = context.ParseResult.GetValueForOption(probeRepoOption);
            var dev = context.ParseResult.GetValueForOption(probeDevOption);
            var gatewayUrl = context.ParseResult.GetValueForOption(gatewayUrlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = BuildCommand.ResolveRepoRoot(path, dev);
            context.ExitCode = await ServeProbeAsync(repoRoot, port, gatewayUrl, verbose, context.GetCancellationToken());
        });

        // serve (default = gateway)
        var servePortOption = new Option<int>("--port", () => 5005, "Port to listen on.");
        var serveRepoOption = new Option<string?>("--path", () => null, "Path to the repository root. Defaults to the current directory.");
        var serveDevOption = new Option<bool>("--dev", "Serve from a development repo clone instead of the install location.");

        var command = new Command("serve", "Start a BotNexus service. Defaults to the gateway.")
        {
            servePortOption,
            serveRepoOption,
            serveDevOption
        };

        command.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(servePortOption);
            var path = context.ParseResult.GetValueForOption(serveRepoOption);
            var dev = context.ParseResult.GetValueForOption(serveDevOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = BuildCommand.ResolveRepoRoot(path, dev);
            context.ExitCode = await ServeGatewayAsync(repoRoot, port, verbose, context.GetCancellationToken());
        });

        command.AddCommand(gatewayCommand);
        command.AddCommand(probeCommand);

        return command;
    }

    private static async Task<int> ServeGatewayAsync(string repoRoot, int port, bool verbose, CancellationToken cancellationToken)
    {
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
            AnsiConsole.MarkupLine("[blue][[serve]][/] No configuration found — creating default config...");
            var init = new InitCommand();
            var initResult = await init.ExecuteAsync(force: false, verbose, cancellationToken);
            if (initResult != 0)
                return initResult;
            AnsiConsole.MarkupLine("[blue][[serve]][/] Configure your gateway via the WebUI at the root URL.");
            AnsiConsole.WriteLine();
        }

        if (!IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Port [green]{port}[/] is already in use.");
            return 1;
        }

        DeployExtensions(repoRoot, verbose);

        var gatewayUrl = $"http://localhost:{port}";
        var lastExitCode = 0;

        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue][[serve]][/] Starting Gateway");
            AnsiConsole.MarkupLine($"   URL:         [green]{Markup.Escape(gatewayUrl)}[/]");
            AnsiConsole.MarkupLine("   Environment: [dim]Development[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Press [yellow]Ctrl+C[/] to stop the gateway.");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{gatewayDll}\"",
                UseShellExecute = false
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["ASPNETCORE_URLS"] = gatewayUrl;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Gateway process.");

            await process.WaitForExitAsync(cancellationToken);
            lastExitCode = process.ExitCode;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[blue][[serve]][/] Gateway exited (code [yellow]{lastExitCode}[/]).");

            if (cancellationToken.IsCancellationRequested)
                break;

            if (!await WaitForRestartOrQuitAsync(5, cancellationToken))
                break;
        }

        return lastExitCode;
    }

    private static async Task<int> ServeProbeAsync(string repoRoot, int port, string gatewayUrl, bool verbose, CancellationToken cancellationToken)
    {
        var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

        var probeDll = Path.Combine(repoRoot, "tools", "BotNexus.Probe", "src", "BotNexus.Probe", "bin", "Release", "net10.0", "BotNexus.Probe.dll");

        if (!File.Exists(probeDll))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Release build not found at: [dim]{Markup.Escape(probeDll)}[/]");
            return 1;
        }

        if (!IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Port [green]{port}[/] is already in use.");
            return 1;
        }

        var probeUrl = $"http://localhost:{port}";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue][[serve]][/] Starting Probe");
        AnsiConsole.MarkupLine($"   URL:         [green]{Markup.Escape(probeUrl)}[/]");
        AnsiConsole.MarkupLine($"   Gateway:     [dim]{Markup.Escape(gatewayUrl)}[/]");
        AnsiConsole.WriteLine();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{probeDll}\"",
            UseShellExecute = false
        };
        psi.Environment["ASPNETCORE_URLS"] = probeUrl;
        psi.Environment["Gateway__Url"] = gatewayUrl;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Probe process.");

        await process.WaitForExitAsync(cancellationToken);
        AnsiConsole.MarkupLine($"[blue][[serve]][/] Probe exited (code [yellow]{process.ExitCode}[/]).");
        return process.ExitCode;
    }

    /// <summary>
    /// Deploys built extensions from the repository to ~/.botnexus/extensions.
    /// Public to allow GatewayCommand to use it.
    /// </summary>
    public static void DeployExtensions(string repoRoot, bool verbose)
    {
        var extensionsRoot = Path.Combine(repoRoot, "src", "extensions");
        if (!Directory.Exists(extensionsRoot))
        {
            if (verbose)
                AnsiConsole.MarkupLine("[blue][[deploy]][/] [dim]No extensions directory found \u2014 skipping.[/]");
            return;
        }

        var destRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".botnexus", "extensions");

        var projects = Directory.GetFiles(extensionsRoot, "*.csproj", SearchOption.AllDirectories);
        var deployed = 0;
        var deployedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var projectDir = Path.GetDirectoryName(project)!;
            var projectName = Path.GetFileNameWithoutExtension(project);
            var manifestPath = Path.Combine(projectDir, "botnexus-extension.json");

            if (!File.Exists(manifestPath))
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"[blue][[deploy]][/] [dim]Skipped {Markup.Escape(projectName)} (no manifest)[/]");
                continue;
            }

            string? extId;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                extId = doc.RootElement.GetProperty("id").GetString();
            }
            catch
            {
                AnsiConsole.MarkupLine($"[yellow][[deploy]] WARNING:[/] Could not read manifest for {Markup.Escape(projectName)}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(extId))
                continue;

            deployedIds.Add(extId);

            var srcDir = Path.Combine(projectDir, "bin", "Release");
            if (!Directory.Exists(srcDir))
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"[blue][[deploy]][/] [dim]No Release build for {Markup.Escape(projectName)} \u2014 skipping.[/]");
                continue;
            }

            // Find TFM folder (net10.0, net9.0, etc.)
            var tfmDir = Directory.GetDirectories(srcDir)
                .Where(d => Path.GetFileName(d).StartsWith("net", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d)
                .FirstOrDefault();

            if (tfmDir is null)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"[blue][[deploy]][/] [dim]No TFM folder in {Markup.Escape(srcDir)} \u2014 skipping {Markup.Escape(projectName)}.[/]");
                continue;
            }

            var extDest = Path.Combine(destRoot, extId);
            Directory.CreateDirectory(extDest);

            foreach (var file in Directory.GetFiles(tfmDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(tfmDir, file);
                var destFile = Path.Combine(extDest, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(file, destFile, overwrite: true);
            }

            File.Copy(manifestPath, Path.Combine(extDest, "botnexus-extension.json"), overwrite: true);
            AnsiConsole.MarkupLine($"[blue][[deploy]][/] Deployed [green]{Markup.Escape(extId)}[/]");
            deployed++;
        }

        // Clean stale extensions
        if (Directory.Exists(destRoot))
        {
            foreach (var dir in Directory.GetDirectories(destRoot))
            {
                var dirName = Path.GetFileName(dir);
                if (!deployedIds.Contains(dirName))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        AnsiConsole.MarkupLine($"[blue][[deploy]][/] Removed stale: [dim]{Markup.Escape(dirName)}[/]");
                    }
                    catch
                    {
                        AnsiConsole.MarkupLine($"[yellow][[deploy]][/] Could not remove {Markup.Escape(dirName)} (files locked)");
                    }
                }
            }
        }

        AnsiConsole.MarkupLine($"[blue][[deploy]][/] [green]{deployed}[/] extension(s) deployed to [dim]{Markup.Escape(destRoot)}[/]");
    }

    /// <summary>
    /// Waits for a restart countdown or user quit input.
    /// Public to allow GatewayCommand to use it for attached mode.
    /// </summary>
    public static async Task<bool> WaitForRestartOrQuitAsync(int seconds, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue][[restart]][/] Gateway will restart in [yellow]{seconds}[/] seconds. Press [yellow]q[/] to quit.");

        for (var i = seconds; i > 0; i--)
        {
            Console.Write($"\r   Restarting in {i}... ");
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("\r   Cancelled.                ");
                    return false;
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.KeyChar is 'q' or 'Q')
                    {
                        Console.WriteLine("\r   Quit requested. Exiting.   ");
                        return false;
                    }
                }

                await Task.Delay(50, CancellationToken.None);
            }
        }

        Console.WriteLine("\r   Restarting now...          ");
        return true;
    }

    /// <summary>
    /// Checks if a TCP port is available for binding.
    /// Public to allow GatewayCommand to use it.
    /// </summary>
    public static bool IsPortAvailable(int port)
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
