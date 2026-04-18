using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Abstractions;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Resolves provider API keys for Gateway-hosted agents.
/// </summary>
public sealed class GatewayAuthManager
{
    private const string AuthFileName = "auth.json";
    private readonly PlatformConfig _platformConfig;
    private readonly ILogger<GatewayAuthManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly string _authFilePath;
    private readonly string _legacyAuthFilePath;
    private readonly object _sync = new();
    private Dictionary<string, AuthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public GatewayAuthManager(PlatformConfig platformConfig, ILogger<GatewayAuthManager> logger, IFileSystem fileSystem)
    {
        _platformConfig = platformConfig;
        _logger = logger;
        _fileSystem = fileSystem;
        _authFilePath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(_fileSystem), AuthFileName);
        _legacyAuthFilePath = Path.Combine(Environment.CurrentDirectory, ".botnexus-agent", AuthFileName);
    }

    /// <summary>
    /// Returns the API endpoint override for a provider from auth.json or platform config.
    /// Used to override model BaseUrl (e.g., enterprise vs individual Copilot endpoints).
    /// </summary>
    public string? GetApiEndpoint(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        LoadAuthEntries();

        if (TryGetAuthEntry(provider, out var entry) && !string.IsNullOrWhiteSpace(entry.Endpoint))
            return entry.Endpoint;

        if (_platformConfig.Providers is not null &&
            TryGetProviderConfig(_platformConfig.Providers, provider, out var providerConfig) &&
            !string.IsNullOrWhiteSpace(providerConfig?.BaseUrl))
            return providerConfig.BaseUrl;

        return null;
    }

    /// <summary>
    /// Resolves an API key from <c>~/.botnexus/auth.json</c>, environment variables, or platform config.
    /// </summary>
    public async Task<string?> GetApiKeyAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        var authKey = await GetApiKeyFromAuthEntryAsync(provider, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(authKey))
        {
            return authKey;
        }

        var envKey = EnvironmentApiKeys.GetApiKey(provider);
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey;
        }

        return await ResolveProviderConfigApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveProviderConfigApiKeyAsync(string provider, CancellationToken cancellationToken)
    {
        if (_platformConfig.Providers is null)
        {
            return null;
        }

        if (!TryGetProviderConfig(_platformConfig.Providers, provider, out var providerConfig) ||
            string.IsNullOrWhiteSpace(providerConfig?.ApiKey))
        {
            return null;
        }

        const string AuthPrefix = "auth:";
        if (providerConfig.ApiKey.StartsWith(AuthPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var referenceProvider = providerConfig.ApiKey[AuthPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(referenceProvider))
            {
                return null;
            }

            return await GetApiKeyFromAuthEntryAsync(referenceProvider, cancellationToken).ConfigureAwait(false);
        }

        return providerConfig.ApiKey;
    }

    private async Task<string?> GetApiKeyFromAuthEntryAsync(string provider, CancellationToken cancellationToken)
    {
        LoadAuthEntries();

        if (!TryGetAuthEntry(provider, out var entry))
        {
            return null;
        }

        if (!NeedsRefresh(entry))
        {
            return entry.Access;
        }

        try
        {
            var refreshed = await RefreshEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            UpdateEntry(provider, refreshed);
            return refreshed.Access;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed refreshing auth credentials for provider '{Provider}'.", provider);
            return null;
        }
    }

    private static bool NeedsRefresh(AuthEntry entry)
    {
        if (!string.Equals(entry.Type, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs >= entry.Expires - 60_000 || string.IsNullOrWhiteSpace(entry.Endpoint);
    }

    private static async Task<AuthEntry> RefreshEntryAsync(AuthEntry entry, CancellationToken cancellationToken)
    {
        var credentials = new OAuthCredentials(
            AccessToken: entry.Access,
            RefreshToken: entry.Refresh,
            ExpiresAt: entry.Expires / 1000,
            ApiEndpoint: entry.Endpoint);

        var refreshed = await CopilotOAuth.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);

        return new AuthEntry
        {
            Type = entry.Type,
            Refresh = refreshed.RefreshToken,
            Access = refreshed.AccessToken,
            Expires = refreshed.ExpiresAt * 1000,
            Endpoint = refreshed.ApiEndpoint ?? entry.Endpoint
        };
    }

    private bool TryGetAuthEntry(string provider, out AuthEntry entry)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(provider, out entry!) ||
                   (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase) &&
                    _entries.TryGetValue("github-copilot", out entry!));
        }
    }

    private void UpdateEntry(string provider, AuthEntry entry)
    {
        lock (_sync)
        {
            _entries[provider] = entry;
            SaveAuthEntries();
        }
    }

    private void LoadAuthEntries()
    {
        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            _entries = new Dictionary<string, AuthEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidatePath in new[] { _legacyAuthFilePath, _authFilePath })
            {
                if (!_fileSystem.File.Exists(candidatePath))
                    continue;

                try
                {
                    var json = _fileSystem.File.ReadAllText(candidatePath);
                    var deserialized = JsonSerializer.Deserialize<Dictionary<string, AuthEntry>>(json, JsonOptions) ??
                        new Dictionary<string, AuthEntry>();

                    foreach (var (key, value) in deserialized)
                    {
                        _entries[key] = value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse auth file '{AuthPath}'.", candidatePath);
                }
            }

            _loaded = true;
        }
    }

    private void SaveAuthEntries()
    {
        var directory = Path.GetDirectoryName(_authFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        _fileSystem.File.WriteAllText(_authFilePath, json);
    }

    private static bool TryGetProviderConfig(
        IReadOnlyDictionary<string, ProviderConfig> providers,
        string provider,
        out ProviderConfig? providerConfig)
    {
        if (providers.TryGetValue(provider, out var exact))
        {
            providerConfig = exact;
            return true;
        }

        foreach (var (key, value) in providers)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase))
            {
                providerConfig = value;
                return true;
            }
        }

        providerConfig = null;
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class AuthEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "oauth";

        [JsonPropertyName("refresh")]
        public string Refresh { get; set; } = string.Empty;

        [JsonPropertyName("access")]
        public string Access { get; set; } = string.Empty;

        [JsonPropertyName("expires")]
        public long Expires { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }
    }
}
