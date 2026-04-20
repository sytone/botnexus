using System.CommandLine;
using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

internal sealed class DoctorCommand
{
    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("doctor", "Run CLI diagnostics.");
        var locationsCommand = new Command("locations", "Check location accessibility.");
        locationsCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteLocationsAsync(verbose, CancellationToken.None);
        });

        command.AddCommand(locationsCommand);
        return command;
    }

    public async Task<int> ExecuteLocationsAsync(bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        var locations = WorldDescriptorBuilder.Build(config, null, null)
            .Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (locations.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No locations registered.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Checking [green]{locations.Length}[/] locations...\n");

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var healthyCount = 0;
        var warningCount = 0;
        var errorCount = 0;

        var table = new Table()
            .AddColumn("Status")
            .AddColumn("Location")
            .AddColumn("Target")
            .AddColumn("Message");

        foreach (var location in locations)
        {
            var result = await CheckLocationAsync(location, httpClient, cancellationToken);
            var icon = result.Status switch
            {
                HealthStatus.Healthy => "[green]✓[/]",
                HealthStatus.Warning => "[yellow]⚠[/]",
                _ => "[red]✗[/]"
            };

            healthyCount += result.Status == HealthStatus.Healthy ? 1 : 0;
            warningCount += result.Status == HealthStatus.Warning ? 1 : 0;
            errorCount += result.Status == HealthStatus.Error ? 1 : 0;
            table.AddRow(icon, Markup.Escape(location.Name), Markup.Escape(result.Target), Markup.Escape(result.Message));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Results: [green]{healthyCount} healthy[/], [yellow]{warningCount} warning[/], [red]{errorCount} error[/]");
        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Loaded from: {Markup.Escape(PlatformConfigLoader.DefaultConfigPath)}[/]");

        return errorCount == 0 ? 0 : 1;
    }

    private static async Task<LocationHealthResult> CheckLocationAsync(Location location, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var target = location.Path ?? "(unset)";
        if (location.Type == LocationType.FileSystem)
        {
            if (string.IsNullOrWhiteSpace(location.Path))
                return new LocationHealthResult(target, HealthStatus.Error, "not found (path missing)");

            return Directory.Exists(location.Path) || File.Exists(location.Path)
                ? new LocationHealthResult(target, HealthStatus.Healthy, "accessible")
                : new LocationHealthResult(target, HealthStatus.Error, "not found");
        }

        if (location.Type == LocationType.Api || location.Type == LocationType.RemoteNode)
            return await ProbeHttpEndpointAsync(target, httpClient, cancellationToken);

        if (location.Type == LocationType.McpServer)
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var endpointUri)
                && (endpointUri.Scheme == Uri.UriSchemeHttp || endpointUri.Scheme == Uri.UriSchemeHttps))
            {
                return await ProbeHttpEndpointAsync(target, httpClient, cancellationToken);
            }

            var command = ParseCommand(target);
            if (string.IsNullOrWhiteSpace(command))
                return new LocationHealthResult(target, HealthStatus.Warning, "unreachable (endpoint missing)");

            return IsCommandAvailable(command)
                ? new LocationHealthResult(target, HealthStatus.Healthy, "reachable")
                : new LocationHealthResult(target, HealthStatus.Warning, "unreachable (command not found)");
        }

        if (location.Type == LocationType.Database)
            return await ProbeDatabaseAsync(target, cancellationToken);

        return new LocationHealthResult(target, HealthStatus.Warning, $"unreachable (unsupported type '{location.Type.Value}')");
    }

    private static async Task<LocationHealthResult> ProbeHttpEndpointAsync(string target, HttpClient httpClient, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            return new LocationHealthResult(target, HealthStatus.Warning, "unreachable (invalid endpoint)");
        }

        try
        {
            using var response = await httpClient.GetAsync(endpointUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new LocationHealthResult(target, HealthStatus.Healthy, $"accessible (HTTP {(int)response.StatusCode})");

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return new LocationHealthResult(target, HealthStatus.Warning, $"unreachable (HTTP {(int)response.StatusCode})");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new LocationHealthResult(target, HealthStatus.Error, "not found (HTTP 404)");

            return new LocationHealthResult(target, HealthStatus.Warning, $"unreachable (HTTP {(int)response.StatusCode})");
        }
        catch (TaskCanceledException)
        {
            return new LocationHealthResult(target, HealthStatus.Warning, "unreachable (timeout)");
        }
        catch (Exception ex)
        {
            return new LocationHealthResult(target, HealthStatus.Warning, $"unreachable ({ex.GetType().Name})");
        }
    }

    private static async Task<LocationHealthResult> ProbeDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new LocationHealthResult("(unset)", HealthStatus.Error, "not found (connection string missing)");

        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource?.Trim();
            if (!string.IsNullOrWhiteSpace(dataSource) && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                var expandedPath = ExpandUserHome(dataSource);
                if (!Path.IsPathRooted(expandedPath))
                    expandedPath = Path.GetFullPath(expandedPath);
                if (!File.Exists(expandedPath))
                    return new LocationHealthResult(connectionString, HealthStatus.Error, "not found");
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return new LocationHealthResult(connectionString, HealthStatus.Healthy, "accessible");
        }
        catch (Exception ex)
        {
            return new LocationHealthResult(connectionString, HealthStatus.Warning, $"unreachable ({ex.GetType().Name})");
        }
    }

    private static string ParseCommand(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : string.Empty;
        }

        var separatorIndex = trimmed.IndexOf(' ');
        return separatorIndex > 0 ? trimmed[..separatorIndex] : trimmed;
    }

    private static bool IsCommandAvailable(string command)
    {
        if (Path.IsPathRooted(command))
            return File.Exists(command);

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return false;

        var commandName = command.Trim();
        var isWindows = OperatingSystem.IsWindows();
        var extensions = isWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               ?? [".EXE", ".CMD", ".BAT"])
            : [string.Empty];

        foreach (var path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidateBasePath = Path.Combine(path, commandName);
            if (File.Exists(candidateBasePath))
                return true;

            if (isWindows)
            {
                foreach (var extension in extensions)
                {
                    var candidatePath = candidateBasePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                        ? candidateBasePath
                        : candidateBasePath + extension;
                    if (File.Exists(candidatePath))
                        return true;
                }
            }
        }

        return false;
    }

    private static string ExpandUserHome(string path)
    {
        if (!path.StartsWith('~'))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1)
            return home;

        var first = path[1];
        if (first == Path.DirectorySeparatorChar || first == Path.AltDirectorySeparatorChar)
            return Path.Combine(home, path[2..]);

        return path;
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Config file not found at [dim]{Markup.Escape(configPath)}[/]. Run [green]botnexus init[/] first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unable to load config: {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static string PadRight(string value, int width)
        => value.Length >= width ? value : value.PadRight(width);

    private enum HealthStatus
    {
        Healthy,
        Warning,
        Error
    }

    private sealed record LocationHealthResult(string Target, HealthStatus Status, string Message);
}
