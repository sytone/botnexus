using System.CommandLine;
using System.Diagnostics;
using System.Net.Sockets;
using BotNexus.Gateway.Configuration;
using Spectre.Console;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Commands;

internal sealed class ServeCommand
{
    private readonly GatewayCommand _gatewayCommand;

    public ServeCommand(GatewayCommand gatewayCommand)
    {
        _gatewayCommand = gatewayCommand;
    }

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        // Gateway lifecycle management
        var gatewayCommand = _gatewayCommand.Build(verboseOption, targetOption);

        var probePortOption = new Option<int>("--port", () => 5050, "Port for the Probe web UI.");
        var probeSourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");
        var gatewayUrlOption = new Option<string>("--gateway-url", () => GatewayDefaults.LoopbackListenUrl, "URL of a running BotNexus Gateway.");

        var probeCommand = new Command("probe", "Start the BotNexus Probe diagnostic tool.")
        {
            probePortOption,
            probeSourceOption,
            gatewayUrlOption
        };
        probeCommand.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(probePortOption);
            var source = context.ParseResult.GetValueForOption(probeSourceOption);
            var gatewayUrl = context.ParseResult.GetValueForOption(gatewayUrlOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            context.ExitCode = await ServeProbeAsync(repoRoot, port, gatewayUrl, verbose, context.GetCancellationToken());
        });

        // serve (default = gateway)
        var servePortOption = new Option<int>("--port", () => GatewayDefaults.ListenPort, "Port to listen on.");
        var serveSourceOption = new Option<string?>("--source", () => null, "Path to the BotNexus repository root. Defaults to ~/botnexus.");

        var command = new Command("serve", "Start a BotNexus service in the foreground (development mode). Defaults to the gateway. Note: for production/background use, prefer 'gateway start' which runs the gateway as a detached process.")
        {
            servePortOption,
            serveSourceOption
        };

        command.SetHandler(async context =>
        {
            var port = context.ParseResult.GetValueForOption(servePortOption);
            var source = context.ParseResult.GetValueForOption(serveSourceOption);
            var target = context.ParseResult.GetValueForOption(targetOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var repoRoot = CliPaths.ResolveSource(source);
            var home = CliPaths.ResolveTarget(target);
            context.ExitCode = await ServeGatewayAsync(repoRoot, home, port, verbose, context.GetCancellationToken());
        });

        command.AddCommand(gatewayCommand);
        command.AddCommand(probeCommand);

        return command;
    }

    private static async Task<int> ServeGatewayAsync(string repoRoot, string home, int port, bool verbose, CancellationToken cancellationToken)
    {
        var buildResult = await BuildCommand.BuildSolutionAsync(repoRoot, verbose, cancellationToken);
        if (buildResult != 0)
            return buildResult;

        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Release build not found at: [dim]{CliText.SafeDisplay(gatewayDll)}[/]");
            return 1;
        }

        // Auto-initialize home with a default config if none exists
        var configPath = Path.Combine(home, "config.json");
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[blue][[serve]][/] No configuration found — creating default config...");
            var init = new InitCommand();
            var initResult = await init.ExecuteAsync(force: false, verbose, cancellationToken);
            if (initResult != 0)
                return initResult;
            AnsiConsole.MarkupLine("[dim]Configure your gateway via the WebUI at the root URL.[/]");
            AnsiConsole.WriteLine();
        }

        if (!IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Port [green]{port}[/] is already in use.");
            return 1;
        }

        DeployExtensions(repoRoot, home, verbose);

        // The gateway binds gateway.listenUrl when one is configured, overriding the --urls
        // argument below, so probe where it will actually listen rather than where we asked.
        var gatewayUrl = GatewayProbeUrlResolver.ResolveFromConfig(port);
        var lastExitCode = 0;

        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold blue]BotNexus Gateway[/]") { Justification = Justify.Left });
            AnsiConsole.MarkupLine($"  [dim]URL:[/]         [green]{CliText.SafeDisplay(gatewayUrl)}[/]");
            AnsiConsole.MarkupLine("  [dim]Environment:[/] Development");
            AnsiConsole.MarkupLine("  Press [yellow]Ctrl+C[/] to stop the gateway.");
            AnsiConsole.WriteLine();

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{gatewayDll}\"",
                UseShellExecute = false
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["ASPNETCORE_URLS"] = gatewayUrl;
            psi.Environment["BOTNEXUS_HOME"] = home;
            ApplyCrashDumpEnvironment(psi, home);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Gateway process.");

            await process.WaitForExitAsync(cancellationToken);
            lastExitCode = process.ExitCode;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Gateway exited (code [yellow]{lastExitCode}[/]).[/]");

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
            AnsiConsole.MarkupLine($"[red]Error:[/] Release build not found at: [dim]{CliText.SafeDisplay(probeDll)}[/]");
            return 1;
        }

        if (!IsPortAvailable(port))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Port [green]{port}[/] is already in use.");
            return 1;
        }

        // The gateway binds gateway.listenUrl when one is configured, overriding the --urls
        // argument below, so probe where it will actually listen rather than where we asked.
        var probeUrl = GatewayProbeUrlResolver.ResolveFromConfig(port);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold blue]BotNexus Probe[/]") { Justification = Justify.Left });
        AnsiConsole.MarkupLine($"  [dim]URL:[/]     [green]{CliText.SafeDisplay(probeUrl)}[/]");
        AnsiConsole.MarkupLine($"  [dim]Gateway:[/] [dim]{CliText.SafeDisplay(gatewayUrl)}[/]");
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
        AnsiConsole.MarkupLine($"[dim]Probe exited (code [yellow]{process.ExitCode}[/]).[/]");
        return process.ExitCode;
    }

    /// <summary>
    /// Applies the .NET minidump-on-crash environment variables to a child gateway process so a
    /// hard exit (including stack overflow / <see cref="System.Environment.FailFast(string)"/>)
    /// leaves a dump under <c>{home}/dumps</c>. The CLR only honours these variables when they are
    /// present at process startup, so they must be set here on the launcher's
    /// <see cref="ProcessStartInfo"/>. Best-effort: never blocks the gateway from starting.
    /// Public so <see cref="GatewayCommand"/> can share one aligned launcher contract.
    /// </summary>
    public static void ApplyCrashDumpEnvironment(ProcessStartInfo psi, string home)
    {
        try
        {
            var dumpsDir = Path.Combine(home, "dumps");
            Directory.CreateDirectory(dumpsDir);
            BotNexus.Gateway.Diagnostics.CrashDumpEnvironment.Apply(
                dumpsDir,
                (key, value) => psi.Environment[key] = value);
        }
        catch
        {
            // Diagnostics wiring must never break process launch.
        }
    }

    /// <summary>
    /// Deploys built in-tree and registered extensions silently and returns the count deployed.
    /// Registered outputs are read from each registration's managed staging directory.
    /// </summary>
    public static ExtensionDeploymentResult DeployExtensionsSilent(string repoRoot, string home, bool verbose)
        => ReconcileExtensions(repoRoot, home);

    /// <summary>Deploys built extensions and reports registered-source failures without failing startup.</summary>
    public static void DeployExtensions(string repoRoot, string home, bool verbose)
    {
        var result = ReconcileExtensions(repoRoot, home);
        foreach (var failure in result.Failures)
        {
            AnsiConsole.MarkupLine(
                $"[yellow][[deploy]] WARNING:[/] {CliText.SafeDisplay(failure.Source)}: {CliText.SafeDisplay(failure.Message)}");
        }

        AnsiConsole.MarkupLine(
            $"[green]✓[/] {result.DeployedCount} extension(s) deployed to [dim]{CliText.SafeDisplay(Path.Combine(home, "extensions"))}[/]");
    }

    private static ExtensionDeploymentResult ReconcileExtensions(string repoRoot, string home)
    {
        var sources = DiscoverInTreeDeploymentSources(repoRoot).ToList();
        sources.AddRange(DiscoverRegisteredDeploymentSources(home));
        return ExtensionDeploymentReconciler.Reconcile(Path.Combine(home, "extensions"), sources);
    }

    private static IEnumerable<ExtensionDeploymentSource> DiscoverInTreeDeploymentSources(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "src", "extensions");
        if (!Directory.Exists(root))
            yield break;

        foreach (var project in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var projectDirectory = Path.GetDirectoryName(project)!;
            var manifestPath = Path.Combine(projectDirectory, "botnexus-extension.json");
            if (!File.Exists(manifestPath))
                continue;

            var outputDirectory = ResolveExtensionOutputDirectory(projectDirectory);
            if (outputDirectory is null)
                continue;

            yield return new ExtensionDeploymentSource(
                $"in-tree:{Path.GetFileNameWithoutExtension(project)}",
                outputDirectory,
                Enabled: true,
                Registered: false,
                manifestPath);
        }
    }

    private static IEnumerable<ExtensionDeploymentSource> DiscoverRegisteredDeploymentSources(string home)
    {
        var registrations = new ExtensionRepositoryRegistryService(
                Path.Combine(home, "config.json"),
                new System.IO.Abstractions.FileSystem())
            .ListAsync()
            .GetAwaiter()
            .GetResult();

        foreach (var registration in registrations)
        {
            if (!registration.Enabled)
                continue;

            var sourceName = $"repository:{registration.Id}";
            var stagingRoot = Path.Combine(home, "extension-repositories", registration.Id, "staged");
            if (!Directory.Exists(stagingRoot))
            {
                yield return new ExtensionDeploymentSource(sourceName, stagingRoot, true, true);
                continue;
            }

            var roots = File.Exists(Path.Combine(stagingRoot, "botnexus-extension.json"))
                ? [stagingRoot]
                : Directory.GetDirectories(stagingRoot);
            if (roots.Length == 0)
            {
                yield return new ExtensionDeploymentSource(sourceName, stagingRoot, true, true);
                continue;
            }

            foreach (var output in roots)
            {
                var outputName = Path.GetFileName(output);
                yield return new ExtensionDeploymentSource($"{sourceName}:{outputName}", output, true, true);
            }
        }
    }

    private static string? ResolveExtensionOutputDirectory(string projectDir)
    {
        var binDir = Path.Combine(projectDir, "bin");
        if (!Directory.Exists(binDir))
            return null;

        return new[] { Path.Combine(binDir, "Release"), Path.Combine(binDir, "Debug") }
            .Where(Directory.Exists)
            .SelectMany(configurationDir => Directory.GetDirectories(configurationDir)
                .Where(tfmDir => Path.GetFileName(tfmDir).StartsWith("net", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Waits for a restart countdown or user quit input.
    /// Public to allow GatewayCommand to use it for attached mode.
    /// </summary>
    public static async Task<bool> WaitForRestartOrQuitAsync(int seconds, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Restarting[/] in [yellow]{seconds}s[/] — press [yellow]q[/] to quit.");

        for (var i = seconds; i > 0; i--)
        {
            Console.Write($"\r   Restarting in {i}... ");
            // #3738: the one-second tick is measured monotonically. A backwards host clock step would
            // otherwise stall this inner spin indefinitely, wedging the restart countdown.
            var tick = Stopwatch.StartNew();
            while (tick.Elapsed < TimeSpan.FromSeconds(1))
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
    /// Checks if a TCP port is available for binding on the interface the gateway
    /// will actually bind. The gateway binds a wildcard address by default
    /// (<c>http://0.0.0.0:5005</c>, see <see cref="InitCommand"/>), so the probe
    /// defaults to <see cref="System.Net.IPAddress.Any"/> rather than loopback.
    /// Probing the wildcard address detects an occupant on <em>any</em> interface
    /// (loopback included); a loopback-only probe missed wildcard/non-loopback
    /// occupants and could report a confusing late Kestrel <c>EADDRINUSE</c>
    /// (issue #1536). Public so <see cref="GatewayCommand"/> and
    /// <see cref="UpdateCommand"/> can share one aligned probe.
    /// </summary>
    /// <param name="port">The TCP port to probe.</param>
    /// <param name="bindAddress">
    /// The interface the caller intends to bind. Defaults to
    /// <see cref="System.Net.IPAddress.Any"/> (the gateway's wildcard bind) when
    /// not specified, keeping the probe interface aligned with the bind interface.
    /// </param>
    public static bool IsPortAvailable(int port, System.Net.IPAddress? bindAddress = null)
    {
        try
        {
            using var listener = new TcpListener(bindAddress ?? System.Net.IPAddress.Any, port);
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
