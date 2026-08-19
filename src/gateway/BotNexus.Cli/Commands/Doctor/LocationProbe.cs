using BotNexus.Domain.Paths;
using BotNexus.Domain.World;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Health classification for a single registered <see cref="Location"/>.
/// </summary>
internal enum LocationHealthStatus
{
    Healthy,
    Warning,
    Error
}

/// <summary>
/// The probe result for one location: the concrete target that was checked, its status, and a short
/// human message. Shared by both <c>doctor locations</c> (rich table) and the aggregate
/// <see cref="LocationAccessibilityCheck"/> so the two never drift.
/// </summary>
internal sealed record LocationHealthResult(string Target, LocationHealthStatus Status, string Message);

/// <summary>
/// Pure location-accessibility probing extracted from the original <c>doctor locations</c> handler so
/// it can be reused verbatim by the aggregate doctor suite (issue #2041). Probes filesystem paths,
/// HTTP/MCP endpoints, and SQLite databases without any console rendering - callers decide how to
/// present the results.
/// </summary>
internal static class LocationProbe
{
    /// <summary>
    /// Probes a single location and classifies its accessibility. Never throws for an unreachable
    /// target - a failure is folded into a <see cref="LocationHealthStatus"/> so the aggregate runner
    /// can continue with the remaining checks.
    /// </summary>
    public static async Task<LocationHealthResult> CheckLocationAsync(Location location, HttpClient httpClient, CancellationToken cancellationToken)
        => await CheckLocationAsync(location, httpClient, worldId: null, cancellationToken);

    /// <summary>
    /// Probes a single location, additionally checking a filesystem location's world sentinel against
    /// <paramref name="worldId"/> when one is supplied (#2836).
    /// </summary>
    /// <param name="worldId">
    /// The world this process is running as, or <see langword="null"/> to skip the identity check.
    /// Must be the single already-resolved world ID - re-deriving it here would give the identity and
    /// the path a common failure mode, and the check would agree with itself while both were wrong.
    /// </param>
    public static async Task<LocationHealthResult> CheckLocationAsync(
        Location location,
        HttpClient httpClient,
        string? worldId,
        CancellationToken cancellationToken)
    {
        var target = location.Path ?? "(unset)";
        if (location.Type == LocationType.FileSystem)
        {
            if (string.IsNullOrWhiteSpace(location.Path))
                return new LocationHealthResult(target, LocationHealthStatus.Error, "not found (path missing)");

            if (!Directory.Exists(location.Path) && !File.Exists(location.Path))
                return new LocationHealthResult(target, LocationHealthStatus.Error, "not found");

            // #2836: an accessible directory that belongs to ANOTHER world is the failure this probe
            // most needs to surface. "Accessible" was previously the whole answer, which is exactly
            // how #2819 stayed invisible - the misdirected fallback path existed and was readable, so
            // every accessibility check passed while the data sat in the wrong home.
            var foreignWorld = DescribeForeignWorld(location.Path, worldId);
            if (foreignWorld is not null)
                return new LocationHealthResult(target, LocationHealthStatus.Error, foreignWorld);

            return new LocationHealthResult(target, LocationHealthStatus.Healthy, "accessible");
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
                return new LocationHealthResult(target, LocationHealthStatus.Warning, "unreachable (endpoint missing)");

            return IsCommandAvailable(command)
                ? new LocationHealthResult(target, LocationHealthStatus.Healthy, "reachable")
                : new LocationHealthResult(target, LocationHealthStatus.Warning, "unreachable (command not found)");
        }

        if (location.Type == LocationType.Database)
            return await ProbeDatabaseAsync(target, cancellationToken);

        return new LocationHealthResult(target, LocationHealthStatus.Warning, $"unreachable (unsupported type '{location.Type.Value}')");
    }

    private static async Task<LocationHealthResult> ProbeHttpEndpointAsync(string target, HttpClient httpClient, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            return new LocationHealthResult(target, LocationHealthStatus.Warning, "unreachable (invalid endpoint)");
        }

        try
        {
            using var response = await httpClient.GetAsync(endpointUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new LocationHealthResult(target, LocationHealthStatus.Healthy, $"accessible (HTTP {(int)response.StatusCode})");

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return new LocationHealthResult(target, LocationHealthStatus.Warning, $"unreachable (HTTP {(int)response.StatusCode})");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new LocationHealthResult(target, LocationHealthStatus.Error, "not found (HTTP 404)");

            return new LocationHealthResult(target, LocationHealthStatus.Warning, $"unreachable (HTTP {(int)response.StatusCode})");
        }
        catch (TaskCanceledException)
        {
            return new LocationHealthResult(target, LocationHealthStatus.Warning, "unreachable (timeout)");
        }
        catch (Exception ex)
        {
            return new LocationHealthResult(target, LocationHealthStatus.Warning, $"unreachable ({ex.GetType().Name})");
        }
    }

    private static async Task<LocationHealthResult> ProbeDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new LocationHealthResult("(unset)", LocationHealthStatus.Error, "not found (connection string missing)");

        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource?.Trim();
            if (!string.IsNullOrWhiteSpace(dataSource) && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                var expandedPath = HomePathExpander.Expand(dataSource);
                if (!Path.IsPathRooted(expandedPath))
                    expandedPath = Path.GetFullPath(expandedPath);
                if (!File.Exists(expandedPath))
                    return new LocationHealthResult(connectionString, LocationHealthStatus.Error, "not found");
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return new LocationHealthResult(connectionString, LocationHealthStatus.Healthy, "accessible");
        }
        catch (Exception ex)
        {
            return new LocationHealthResult(connectionString, LocationHealthStatus.Warning, $"unreachable ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Returns a message when <paramref name="path"/> carries a world sentinel naming a different
    /// world, otherwise <see langword="null"/>. Never throws: <c>doctor</c> reports, it does not fail
    /// (#2836).
    /// </summary>
    internal static string? DescribeForeignWorld(string path, string? worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId) || !Directory.Exists(path))
            return null;

        var sentinelPath = Path.Combine(path, WorldSentinel.FileName);
        if (!File.Exists(sentinelPath))
            return null;

        WorldSentinelDocument? sentinel;
        try
        {
            sentinel = WorldSentinel.Parse(File.ReadAllText(sentinelPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Same asymmetry as the resolver: no sentinel, or an unreadable one, presents no competing
        // identity and is not reported as a mismatch.
        var verdict = WorldSentinel.Classify(worldId!, sentinel, homeIsPopulated: true);
        return verdict == WorldSentinelVerdict.Mismatch
            ? $"wrong world (declares '{sentinel!.WorldId}', running as '{worldId}')"
            : null;
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

}
