using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Abstractions;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core;

namespace BotNexus.CodingAgent.Auth;

/// <summary>
/// Manages authentication credentials for the coding agent.
///
/// Credentials are stored in .botnexus-agent/auth.json and persist across sessions.
/// The user authenticates via the /login command (Copilot OAuth device code flow),
/// and subsequent runs use the saved credentials with auto-refresh.
///
/// Resolution order for GetApiKeyAsync:
/// 1. Explicit config (apiKey in config.json)
/// 2. Environment variables (COPILOT_GITHUB_TOKEN, GH_TOKEN, GITHUB_TOKEN, etc.)
/// 3. Saved credentials in auth.json (auto-refreshed when expired)
/// 4. Returns null — caller should prompt user to /login
/// </summary>
public sealed class AuthManager
{
    private const string AuthFileName = "auth.json";
    private const string DefaultProvider = "github-copilot";

    private readonly IFileSystem _fileSystem;
    private readonly string _authFilePath;
    private Dictionary<string, AuthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public AuthManager(string configDirectory, IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
        _authFilePath = Path.Combine(configDirectory, AuthFileName);
        Load();
    }

    /// <summary>
    /// Run the Copilot OAuth device code flow. Exchanges the GitHub token
    /// for a Copilot session token and saves both to auth.json.
    /// Called by the /login interactive command.
    /// </summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("GitHub Copilot OAuth — Device Code Flow");
        Console.ResetColor();
        Console.WriteLine();

        var credentials = await CopilotOAuth.LoginAsync(
            onAuth: (url, code) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Open:  {url}");
                Console.WriteLine($"  Code:  {code}");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  Waiting for authorization...");
                return Task.CompletedTask;
            },
            onProgress: _ => { },
            ct: ct);

        // Exchange GitHub OAuth token for Copilot session token
        var refreshed = await CopilotOAuth.RefreshAsync(credentials, ct);

        _entries[DefaultProvider] = new AuthEntry
        {
            Type = "oauth",
            Refresh = refreshed.RefreshToken,
            Access = refreshed.AccessToken,
            Expires = refreshed.ExpiresAt * 1000,
            Endpoint = refreshed.ApiEndpoint
        };
        Save();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✅ Authenticated! Credentials saved to auth.json");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Logout — remove saved credentials for the given provider.
    /// </summary>
    public void Logout(string provider = DefaultProvider)
    {
        _entries.Remove(provider);
        Save();
    }

    /// <summary>
    /// Get a usable API key for the given provider. Auto-refreshes OAuth
    /// tokens when expired. Returns null if no credentials are available
    /// (caller should prompt user to /login).
    /// </summary>
    public async Task<string?> GetApiKeyAsync(
        CodingAgentConfig config,
        string provider,
        CancellationToken ct = default)
    {
        // 1. Explicit config
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            return config.ApiKey;

        // 2. Environment variables
        var envKey = EnvironmentApiKeys.GetApiKey(provider);
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        // 3. Saved auth.json
        if (_entries.TryGetValue(provider, out var entry))
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs >= entry.Expires - 60_000 || entry.Endpoint is null)
            {
                try
                {
                    entry = await RefreshEntryAsync(entry, ct);
                    _entries[provider] = entry;
                    Save();
                }
                catch
                {
                    // Refresh failed — credentials may be revoked
                    return null;
                }
            }

            return entry.Access;
        }

        // 4. Nothing found
        return null;
    }

    /// <summary>
    /// Whether any credentials are saved (regardless of expiry).
    /// </summary>
    public bool HasCredentials(string provider = DefaultProvider)
        => _entries.ContainsKey(provider);

    /// <summary>
    /// Get the API endpoint for the given provider, if stored from token exchange.
    /// Enterprise Copilot accounts use a different endpoint than individual accounts.
    /// </summary>
    public string? GetApiEndpoint(string provider = DefaultProvider)
    {
        return _entries.TryGetValue(provider, out var entry) ? entry.Endpoint : null;
    }

    private async Task<AuthEntry> RefreshEntryAsync(AuthEntry entry, CancellationToken ct)
    {
        var credentials = new OAuthCredentials(entry.Access, entry.Refresh, entry.Expires / 1000, entry.Endpoint);
        var refreshed = await CopilotOAuth.RefreshAsync(credentials, ct);

        return new AuthEntry
        {
            Type = entry.Type,
            Refresh = refreshed.RefreshToken,
            Access = refreshed.AccessToken,
            Expires = refreshed.ExpiresAt * 1000,
            Endpoint = refreshed.ApiEndpoint ?? entry.Endpoint
        };
    }

    private void Load()
    {
        if (!_fileSystem.File.Exists(_authFilePath))
        {
            _entries = new Dictionary<string, AuthEntry>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            var json = _fileSystem.File.ReadAllText(_authFilePath);
            _entries = JsonSerializer.Deserialize<Dictionary<string, AuthEntry>>(json, JsonOptions)
                       ?? new Dictionary<string, AuthEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _entries = new Dictionary<string, AuthEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_authFilePath);
        if (!string.IsNullOrEmpty(dir))
            _fileSystem.Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        _fileSystem.File.WriteAllText(_authFilePath, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Auth entry as persisted in auth.json:
    /// <code>
    /// { "github-copilot": { "type": "oauth", "refresh": "ghu_...", "access": "tid=...", "expires": 1775329821000, "endpoint": "https://proxy.enterprise.githubcopilot.com" } }
    /// </code>
    /// Expires is Unix milliseconds.
    /// </summary>
    private sealed class AuthEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "oauth";

        [JsonPropertyName("refresh")]
        public string Refresh { get; set; } = "";

        [JsonPropertyName("access")]
        public string Access { get; set; } = "";

        [JsonPropertyName("expires")]
        public long Expires { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }
    }
}
