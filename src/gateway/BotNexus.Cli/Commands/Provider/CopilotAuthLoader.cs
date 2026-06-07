using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Commands.Provider;

/// <summary>
/// Loads the <c>github-copilot</c> entry from <c>~/.botnexus/auth.json</c> and
/// turns it into a usable pair of credentials: the long-lived GitHub OAuth
/// token (required to call <c>/copilot_internal/user</c>) and a short-lived
/// Copilot session token (required to call the model endpoints). Auto-refreshes
/// the session token via <see cref="CopilotOAuth.RefreshAsync"/> when it is
/// missing or near expiry. Reuses <see cref="ProviderCommand.AuthFileEntry"/>
/// as the on-disk shape so reads and writes match the format the gateway
/// itself produces.
/// </summary>
internal static class CopilotAuthLoader
{
    private const string AuthFileName = "auth.json";
    private const string ProviderKey = "github-copilot";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Resolves Copilot credentials from the BotNexus auth file under
    /// <paramref name="home"/>. Returns <see langword="null"/> if no entry
    /// exists for <c>github-copilot</c>. The returned record contains the
    /// GitHub OAuth token, a fresh Copilot session token, and the resolved API
    /// endpoint base URL. The auth file is rewritten with refreshed credentials
    /// when a token exchange occurs so the next CLI invocation reuses them.
    /// </summary>
    public static async Task<CopilotResolvedAuth?> LoadAsync(string home, CancellationToken cancellationToken = default)
    {
        var authPath = Path.Combine(home, AuthFileName);
        if (!File.Exists(authPath))
        {
            return null;
        }

        Dictionary<string, ProviderCommand.AuthFileEntry> entries;
        try
        {
            var json = await File.ReadAllTextAsync(authPath, cancellationToken).ConfigureAwait(false);
            entries = JsonSerializer.Deserialize<Dictionary<string, ProviderCommand.AuthFileEntry>>(json, JsonOptions)
                ?? new Dictionary<string, ProviderCommand.AuthFileEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }

        if (!entries.TryGetValue(ProviderKey, out var entry))
        {
            return null;
        }

        // The GitHub OAuth token lives in Refresh; if that's empty (older auth.json
        // shape) Access doubles as the GitHub token until the first refresh.
        var githubToken = !string.IsNullOrWhiteSpace(entry.Refresh) ? entry.Refresh : entry.Access;
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            return null;
        }

        // Refresh the Copilot session token when it's missing, near expiry, or
        // the endpoint hasn't been resolved yet. Mirrors GatewayAuthManager.
        var needsRefresh = string.IsNullOrWhiteSpace(entry.Access)
            || string.IsNullOrWhiteSpace(entry.Endpoint)
            || entry.Expires <= 0
            || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= entry.Expires - 60_000;

        if (needsRefresh)
        {
            var credentials = new OAuthCredentials(
                AccessToken: entry.Access,
                RefreshToken: githubToken,
                ExpiresAt: entry.Expires / 1000,
                ApiEndpoint: entry.Endpoint);

            var refreshed = await CopilotOAuth.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);

            entry = new ProviderCommand.AuthFileEntry
            {
                Type = entry.Type,
                Refresh = refreshed.RefreshToken,
                Access = refreshed.AccessToken,
                Expires = refreshed.ExpiresAt * 1000,
                Endpoint = refreshed.ApiEndpoint ?? entry.Endpoint
            };

            entries[ProviderKey] = entry;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
                await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(entries, JsonOptions), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal: even if we cannot persist, the in-memory tokens
                // remain usable for the current invocation.
            }

            githubToken = entry.Refresh;
        }

        return new CopilotResolvedAuth(
            GitHubToken: githubToken,
            CopilotSessionToken: entry.Access,
            ApiEndpoint: entry.Endpoint,
            ExpiresAtUnixMs: entry.Expires);
    }
}

/// <summary>
/// Resolved Copilot credentials ready for use by the CLI diagnostic commands.
/// </summary>
internal sealed record CopilotResolvedAuth(
    string GitHubToken,
    string CopilotSessionToken,
    string? ApiEndpoint,
    long ExpiresAtUnixMs);
