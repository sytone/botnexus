using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Abstractions;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Resolves provider API keys for Gateway-hosted agents.
/// </summary>
public sealed class GatewayAuthManager
{
    private const string AuthFileName = "auth.json";
    private readonly IOptionsMonitor<PlatformConfig> _platformConfig;
    private readonly ILogger<GatewayAuthManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly string _authFilePath;
    private readonly string _legacyAuthFilePath;
    private readonly object _sync = new();
    private Dictionary<string, AuthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public GatewayAuthManager(IOptionsMonitor<PlatformConfig> platformConfig, ILogger<GatewayAuthManager> logger, IFileSystem fileSystem)
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

        if (_platformConfig.CurrentValue.Providers is not null &&
            TryGetProviderConfig(_platformConfig.CurrentValue.Providers, provider, out var providerConfig) &&
            !string.IsNullOrWhiteSpace(providerConfig?.BaseUrl))
            return providerConfig.BaseUrl;

        return null;
    }

    // #1797: the individual/fallback GitHub Copilot MCP host. Distinct from the chat BaseUrl host
    // (api.individual.githubcopilot.com) - the MCP surface lives on api.githubcopilot.com.
    private const string CopilotMcpFallbackEndpoint = "https://api.githubcopilot.com/mcp";

    /// <summary>
    /// Resolves the ready-to-use GitHub Copilot MCP endpoint for a provider (#1797).
    /// Derives the MCP host from the provider's configured endpoint override (enterprise host)
    /// and falls back to the individual host (<c>https://api.githubcopilot.com/mcp</c>) when no
    /// override is declared. This is the single seam for extension-facing Copilot MCP endpoint
    /// resolution - contributors consume the resolved value rather than re-deriving it from a raw
    /// endpoint override.
    /// </summary>
    public string GetCopilotMcpEndpoint(string provider)
        => DeriveCopilotMcpEndpoint(GetApiEndpoint(provider));

    // Turns a raw provider endpoint override (the chat host) into the MCP endpoint by appending the
    // /mcp path, or returns the individual fallback when no override is present. Pure and null-safe.
    private static string DeriveCopilotMcpEndpoint(string? baseEndpoint)
    {
        if (string.IsNullOrWhiteSpace(baseEndpoint))
            return CopilotMcpFallbackEndpoint;

        if (Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var absoluteUri))
        {
            var path = absoluteUri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
                return absoluteUri.ToString().TrimEnd('/');

            var builder = new UriBuilder(absoluteUri)
            {
                Path = string.IsNullOrEmpty(path) || path == "/" ? "/mcp" : $"{path}/mcp"
            };

            return builder.Uri.ToString().TrimEnd('/');
        }

        var trimmed = baseEndpoint.TrimEnd('/');
        return trimmed.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/mcp";
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

        // #2807: declared provider configuration must be consulted BEFORE the process environment.
        // The previous order let an ambient variable win over an explicitly declared credential, and
        // let a declared-but-blank credential fall through to whatever the environment happened to
        // hold. Ambient admission is now gated on nothing having been declared at all.
        var declaredKey = await ResolveProviderConfigApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        if (declaredKey is not null)
        {
            return declaredKey;
        }

        var credential = ProviderCredentialResolver.Resolve(provider, declaredApiKey: null, _logger);
        return credential.HasValue ? credential.Value : null;
    }

    /// <summary>
    /// #2025: the single credential-threading seam for background (non-agent-loop) LLM callers.
    /// Resolves the provider API key via <see cref="GetApiKeyAsync"/> and returns a
    /// <see cref="SimpleStreamOptions"/> carrying it, mirroring what the foreground agent loop
    /// (<c>AgentLoopRunner.BuildStreamOptionsAsync</c>) does for interactive turns. Background
    /// callers (auto-title, compaction) route through this instead of rolling their own
    /// key-resolution + options-building, so every LLM call authenticates the same way.
    /// </summary>
    /// <param name="provider">The model's provider (e.g. <c>github-copilot</c>).</param>
    /// <param name="baseOptions">Optional caller-supplied options to preserve (timeouts, cancellation,
    /// stream-setup watchdog). A copy is returned with <see cref="SimpleStreamOptions.ApiKey"/> set;
    /// the caller's instance is not mutated. When null a fresh options instance is created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Options with the resolved key applied. A null/blank resolved key leaves <c>ApiKey</c> null so
    /// the provider falls back to environment keys - behaviour-preserving for callers that previously
    /// passed no options at all.
    /// </returns>
    public async Task<SimpleStreamOptions> CreateAuthenticatedOptionsAsync(
        string provider,
        SimpleStreamOptions? baseOptions = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        var options = baseOptions ?? new SimpleStreamOptions();
        return string.IsNullOrWhiteSpace(apiKey) ? options : options with { ApiKey = apiKey };
    }

    /// <summary>
    /// Resolves a stable, non-secret identifier for the credential currently backing a provider (#3015).
    /// </summary>
    /// <remarks>
    /// This is the <b>auth profile</b> half of a suspension's scope. A quota/billing/credential
    /// exhaustion is a property of one credential, not of the provider and not of the instance, so a
    /// suspension keyed on the provider alone would black-hole every agent the moment any one of
    /// them ran out of credit.
    /// <para>
    /// The returned value is deliberately <b>derived, never the secret itself</b>: it is a truncated
    /// SHA-256 digest of the resolved key. It is used as a dictionary key and may be
    /// logged, so returning the credential would turn a diagnostics aid into a secret leak. A digest
    /// is sufficient because the only question ever asked of it is "is this the same credential as
    /// last time".
    /// </para>
    /// <para>
    /// Returns <c>"default"</c> when no key is resolvable, so a provider using ambient/implicit
    /// credentials still gets a single consistent scope rather than a null key.
    /// </para>
    /// </remarks>
    /// <param name="provider">The provider whose credential is being identified.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> GetAuthProfileIdAsync(string provider, CancellationToken cancellationToken = default)
    {
        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return DeriveAuthProfileId(apiKey);
    }

    /// <summary>
    /// Derives the stable, non-secret auth-profile identifier from a resolved credential (#3015).
    /// Pure and separately testable so the "never emit the secret" property can be pinned directly.
    /// </summary>
    /// <param name="apiKey">The resolved credential, or null/blank when none is configured.</param>
    public static string DeriveAuthProfileId(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "default";
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private async Task<string?> ResolveProviderConfigApiKeyAsync(string provider, CancellationToken cancellationToken)
    {
        if (_platformConfig.CurrentValue.Providers is null)
        {
            return null;
        }

        if (!TryGetProviderConfig(_platformConfig.CurrentValue.Providers, provider, out var providerConfig) ||
            providerConfig?.ApiKey is null)
        {
            return null;
        }

        // #2807: a declared-but-blank apiKey is still a declaration. Returning null here would let the
        // caller widen into the ambient environment, which is exactly the substitution being prevented,
        // so the blank value is returned as-is and fails the caller's own emptiness guard instead.
        if (string.IsNullOrWhiteSpace(providerConfig.ApiKey))
        {
            return providerConfig.ApiKey;
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
        // #2392: auth.json holds OAuth refresh/access tokens. Narrow it to the owner on every
        // save, not just first create - a token refresh rewrites this file routinely.
        SecureFilePermissions.RestrictToOwner(_fileSystem, _authFilePath);
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
