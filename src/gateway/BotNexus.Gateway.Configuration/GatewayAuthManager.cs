using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Abstractions;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Providers;
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
    private readonly IProviderHealthObserver _healthObserver;

    /// <summary>
    /// The credential-refresh call, indirected so that a test can drive the upstream-failure path.
    ///
    /// <para>
    /// The refresh goes through a static <c>CopilotOAuth</c> call, which cannot be made to return a
    /// 502/503 from a test. Without this seam the entire refresh-failure branch - the branch the
    /// whole of #3281 is about - would be unreachable by any test, and its coverage would be a claim
    /// rather than a demonstration. Production wiring is unchanged: the default is the real call.
    /// </para>
    /// </summary>
    private readonly Func<AuthEntry, CancellationToken, Task<AuthEntry>> _refreshEntry;
    private readonly object _sync = new();
    private Dictionary<string, AuthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    /// <summary>
    /// #3673: the observed on-disk state of the auth files at the moment the cache was populated.
    ///
    /// <para>
    /// The cache used to be a one-shot latch (<see cref="_loaded"/> set once, never reset), so a
    /// credential written <b>out of process</b> - which is exactly what <c>botnexus provider login</c>
    /// does - was invisible to a running gateway. It kept serving the revoked token and every call
    /// failed <c>HTTP 403</c> until someone restarted the daemon. Recording the files' last-write
    /// stamp and size lets the next resolution notice the rewrite and re-read.
    /// </para>
    /// <para>
    /// A stat is deliberately used rather than a read: the steady state must not perform disk I/O on
    /// every credential resolution, and an unchanged stamp is sufficient evidence that the cached
    /// entries still match the file.
    /// </para>
    /// </summary>
    private string? _loadedSignature;

    public GatewayAuthManager(IOptionsMonitor<PlatformConfig> platformConfig, ILogger<GatewayAuthManager> logger, IFileSystem fileSystem)
        : this(platformConfig, logger, fileSystem, NullProviderHealthObserver.Instance)
    {
    }

    /// <summary>
    /// Creates an auth manager that reports credential outcomes to a health observer (#3281).
    /// The observer receives every refresh attempt so that repeated upstream failures can become a
    /// <c>health.degraded</c> event instead of a log line nobody sees.
    /// </summary>
    public GatewayAuthManager(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<GatewayAuthManager> logger,
        IFileSystem fileSystem,
        IProviderHealthObserver healthObserver)
        : this(platformConfig, logger, fileSystem, healthObserver, RefreshEntryAsync)
    {
    }

    /// <summary>
    /// Test-facing constructor that also substitutes the credential-refresh call so the
    /// upstream-failure branch can be exercised deterministically (#3281).
    /// </summary>
    internal GatewayAuthManager(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<GatewayAuthManager> logger,
        IFileSystem fileSystem,
        IProviderHealthObserver healthObserver,
        Func<AuthEntry, CancellationToken, Task<AuthEntry>> refreshEntry)
    {
        _refreshEntry = refreshEntry ?? throw new ArgumentNullException(nameof(refreshEntry));
        _platformConfig = platformConfig;
        _logger = logger;
        _fileSystem = fileSystem;
        _healthObserver = healthObserver ?? NullProviderHealthObserver.Instance;
        _authFilePath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(_fileSystem), AuthFileName);
        _legacyAuthFilePath = Path.Combine(Environment.CurrentDirectory, ".botnexus-agent", AuthFileName);
    }

    /// <summary>
    /// Resolves a provider credential and reports why the attempt ended as it did (#3281).
    ///
    /// <para>
    /// Prefer this over <see cref="GetApiKeyAsync"/> when the caller needs to distinguish an upstream
    /// outage from a provider that was simply never configured. <see cref="GetApiKeyAsync"/> remains
    /// the convenience projection that drops the reason.
    /// </para>
    /// </summary>
    public async Task<ProviderCredentialOutcome> ResolveCredentialAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return ProviderCredentialOutcome.NotConfigured();
        }

        var resolved = await ResolveSelectedCredentialAsync(provider, cancellationToken, stopOnRefreshFailure: true).ConfigureAwait(false);
        return resolved.Outcome;
    }

    /// <summary>
    /// Returns the API endpoint override for a provider from auth.json or platform config.
    /// Used to override model BaseUrl (e.g., enterprise vs individual Copilot endpoints).
    /// </summary>
    public string? GetApiEndpoint(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        var selection = SelectCredential(provider);
        if (!string.IsNullOrWhiteSpace(selection.Entry?.Endpoint))
            return selection.Entry.Endpoint;

        var providerConfig = SelectProviderConfig(provider);
        return string.IsNullOrWhiteSpace(providerConfig?.BaseUrl) ? null : providerConfig.BaseUrl;
    }

    /// <summary>
    /// Returns the GitHub OAuth refresh credential for the Copilot account without refreshing it
    /// or exposing the short-lived Copilot session access token. This is for read-only account
    /// discovery endpoints that require the original OAuth credential.
    /// </summary>
    public Task<string?> GetCopilotOAuthTokenAsync(CancellationToken cancellationToken = default)
        => GetCopilotOAuthTokenAsync("github-copilot", cancellationToken);

    /// <summary>Resolves only the selected configured Copilot account; unknown instances never use the default.</summary>
    public Task<string?> GetCopilotOAuthTokenAsync(string instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResolveCopilotAccountCredential(instance)?.OAuthToken);
    }

    private readonly byte[] _quotaGenerationKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    /// <summary>Reads local auth state without token exchange. Unsupported authorities are unavailable.</summary>
    public CopilotAccountCredential? ResolveCopilotAccountCredential(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Length > 128) return null;
        lock (_sync)
        {
            return CreateCopilotAccountCredential(instance, SelectCredential(instance));
        }
    }

    private CopilotAccountCredential? CreateCopilotAccountCredential(string instance, CredentialSelection selection)
    {
        var selected = instance;
        var config = SelectProviderConfig(selected);
        var canonical = IsCopilotType(selected);
        if (config is null && !canonical) return null;
        if (config is not null && (!config.Enabled ||
            !IsCopilotType(config.Type ?? selected) || !IsPublicCopilotEndpoint(config.BaseUrl))) return null;
        var entry = selection.Entry;
        if (entry is null || selection.AuthKey is null || entry.AttributionUnavailable ||
            !string.Equals(entry.Type, "oauth", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.Refresh) || !IsPublicCopilotEndpoint(entry.Endpoint)) return null;
        if (canonical)
        {
            var alias = string.Equals(selected, "copilot", StringComparison.OrdinalIgnoreCase) ? "github-copilot" : "copilot";
            var other = SelectCredential(alias);
            // Both aliases share one quota/header scope. Distinct OAuth snapshots cannot prove
            // a single supplier, even with equal refresh text (their auth-key generations differ).
            // Keep inference exact-first; only account/header attribution fails closed.
            if (other.Entry is { } otherEntry && !ReferenceEquals(entry, otherEntry) &&
                string.Equals(otherEntry.Type, "oauth", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(otherEntry.Refresh)) return null;
        }
        var scope = canonical ? "github-copilot" : selected.ToLowerInvariant();
        var generation = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            _quotaGenerationKey, System.Text.Encoding.UTF8.GetBytes(selection.AuthKey.ToLowerInvariant() + "\n" + entry.Refresh)));
        return new CopilotAccountCredential(scope, generation, entry.Refresh);
    }

    private static bool IsCopilotType(string type) =>
        string.Equals(type, "copilot", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "github-copilot", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicCopilotEndpoint(string? endpoint) => string.IsNullOrWhiteSpace(endpoint) ||
        (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
         (uri.Host == "api.githubcopilot.com" || uri.Host == "api.individual.githubcopilot.com" ||
          uri.Host == "api.business.githubcopilot.com" || uri.Host == "api.enterprise.githubcopilot.com") &&
         uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo));

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
        var resolved = await ResolveSelectedCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        return resolved.ApiKey;
    }

    /// <summary>
    /// Creates the provider-owned execution policy used by foreground and background LLM calls.
    /// The resolved credential and configured stream-idle timeout are applied without overwriting
    /// explicit base options. A blank credential sentinel is preserved to suppress ambient fallback.
    /// </summary>
    /// <param name="provider">The model provider whose execution policy is required.</param>
    /// <param name="baseOptions">Optional provider policy to copy and enrich.</param>
    /// <param name="cancellationToken">Cancellation for credential resolution.</param>
    /// <returns>A fresh execution-policy value with gateway authentication and timeout policy applied.</returns>
    public async Task<ProviderExecutionOptions> CreateExecutionOptionsAsync(
        string provider,
        ProviderExecutionOptions? baseOptions = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveSelectedCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        var apiKey = resolved.ApiKey;
        var options = baseOptions ?? new ProviderExecutionOptions();
        if (options.StreamIdleTimeoutMs is null)
            options = options with { StreamIdleTimeoutMs = ResolveStreamIdleTimeoutMs(provider) };
        if (apiKey is not null)
            options = options with { ApiKey = apiKey };
        var metadata = options.Metadata is null ? new Dictionary<string, object>() : new Dictionary<string, object>(options.Metadata);
        // Never retain caller attribution after resolving a different credential. Unsupported or
        // ambiguous paths are explicitly unavailable, not legacy calls attributed to the default.
        metadata[BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderScope.MetadataKey] =
            (object?)ResolveHeaderScope(provider, resolved) ??
            BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderAttribution.Unavailable;
        return options with { Metadata = metadata };
    }
    private BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderScope? ResolveHeaderScope(string provider, CredentialResolution resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved.ApiKey) || resolved.Supplier?.Entry is null) return null;
        lock (_sync)
        {
            var current = SelectCredential(provider);
            // Identity, not equal token text, proves which auth snapshot supplied this call.
            // A refresh superseded by an external rotation has no attributable supplier.
            if (!ReferenceEquals(current.Entry, resolved.Supplier.Entry) ||
                !string.Equals(current.AuthKey, resolved.Supplier.AuthKey, StringComparison.OrdinalIgnoreCase)) return null;
            var account = CreateCopilotAccountCredential(provider, current);
            return account is null ? null : new(account.Instance, account.Generation);
        }
    }

    private int? ResolveStreamIdleTimeoutMs(string provider)
    {
        var providers = _platformConfig.CurrentValue.Providers;
        if (providers is null)
            return null;

        foreach (var (name, providerConfig) in providers)
        {
            if (string.Equals(name, provider, StringComparison.OrdinalIgnoreCase))
                return providerConfig.ResolveStreamIdleTimeoutMs();
        }

        return null;
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

    // The supplier decision is shared by inference, account discovery and header attribution.
    // An unusable exact entry may fall through, but a raw/blank declaration never becomes ambient.
    private CredentialSelection SelectCredential(string provider, bool skipExact = false)
    {
        LoadAuthEntries();
        lock (_sync)
        {
            if (!skipExact && TryGetAuthEntry(provider, out var exactKey, out var exact) && IsUsable(exact))
                return new(exactKey, exact, true, null);

            var config = SelectProviderConfig(provider);
            if (config?.ApiKey is not { } declaration) return new(null, null, false, null);
            if (!declaration.StartsWith("auth:", StringComparison.OrdinalIgnoreCase))
                return new(null, null, true, declaration);
            var reference = declaration[5..].Trim();
            if (reference.Length > 0 && TryGetAuthEntry(reference, out var authKey, out var entry) && IsUsable(entry))
                return new(authKey, entry, true, null);
            return new(null, null, true, string.Empty);
        }
    }

    // Exact config wins; only canonical Copilot names may fall back to each other's config.
    // Credentials, endpoints and attribution must all apply the same policy.
    private ProviderConfig? SelectProviderConfig(string provider)
    {
        var providers = _platformConfig.CurrentValue.Providers;
        if (providers is null) return null;
        TryGetProviderConfig(providers, provider, out var config);
        if (config is null && IsCopilotType(provider))
        {
            var alias = string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase) ? "github-copilot" : "copilot";
            TryGetProviderConfig(providers, alias, out config);
        }
        return config;
    }

    private static bool IsUsable(AuthEntry entry) => !string.IsNullOrWhiteSpace(entry.Access) || NeedsRefresh(entry);

    private sealed record CredentialSelection(string? AuthKey, AuthEntry? Entry, bool IsDeclared, string? ApiKey);
    private sealed record CredentialResolution(string? ApiKey, CredentialSelection? Supplier, ProviderCredentialOutcome Outcome);

    private async Task<CredentialResolution> ResolveSelectedCredentialAsync(
        string provider, CancellationToken cancellationToken, bool stopOnRefreshFailure = false)
    {
        if (string.IsNullOrWhiteSpace(provider)) return new(null, null, ProviderCredentialOutcome.NotConfigured());
        var selection = SelectCredential(provider);
        if (selection.Entry is not null && selection.AuthKey is not null)
        {
            var resolved = await ResolveAuthEntryCredentialAsync(selection, cancellationToken).ConfigureAwait(false);
            if (stopOnRefreshFailure || !string.IsNullOrWhiteSpace(resolved.ApiKey)) return resolved;
            // GetApiKey falls through on both refresh failure and successful-but-blank refresh.
            // ResolveCredential keeps the original refresh outcome, including successful blanks.
            var attemptedEntry = selection.Entry;
            selection = SelectCredential(provider, skipExact: true);
            if (selection.Entry is not null && selection.AuthKey is not null && !ReferenceEquals(selection.Entry, attemptedEntry))
            {
                resolved = await ResolveAuthEntryCredentialAsync(selection, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved.ApiKey)) return resolved;
            }
        }
        var credential = ProviderCredentialResolver.Resolve(provider,
            selection.IsDeclared ? selection.ApiKey ?? string.Empty : null, _logger);
        var apiKey = credential.Source == CredentialSource.Declared || credential.HasValue ? credential.Value : null;
        return new(apiKey, null, string.IsNullOrWhiteSpace(apiKey)
            ? ProviderCredentialOutcome.NotConfigured() : ProviderCredentialOutcome.Success(apiKey));
    }

    private async Task<CredentialResolution> ResolveAuthEntryCredentialAsync(CredentialSelection selection, CancellationToken cancellationToken)
    {
        var entry = selection.Entry ?? throw new InvalidOperationException("Missing credential supplier.");
        var provider = selection.AuthKey ?? throw new InvalidOperationException("Missing credential supplier key.");
        if (!NeedsRefresh(entry))
            return new(entry.Access, selection, ProviderCredentialOutcome.Success(entry.Access));
        try
        {
            var refreshed = await _refreshEntry(entry, cancellationToken).ConfigureAwait(false);
            var updated = UpdateEntry(provider, entry, refreshed);
            var success = ProviderCredentialOutcome.Success(refreshed.Access);
            await NotifyHealthObserverAsync(provider, success, cancellationToken).ConfigureAwait(false);
            return new(refreshed.Access, updated ? selection with { Entry = refreshed } : null, success);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep inference retry/fallback semantics, but never expose this failed snapshot as
            // the account behind a later call. State is bounded by the snapshot's own lifetime.
            lock (_sync) entry.AttributionUnavailable = true;
            _logger.LogWarning(ex, "Failed refreshing auth credentials for provider '{Provider}'.", provider);
            var failure = ProviderCredentialOutcome.Failed(ex.GetType().Name, ExtractStatusCode(ex), ex.Message);
            await NotifyHealthObserverAsync(provider, failure, cancellationToken).ConfigureAwait(false);
            return new(null, null, failure);
        }
    }

    /// <summary>
    /// Reports an outcome to the health observer without ever letting it break credential
    /// resolution. An observer fault must not escalate a recoverable provider outage into a
    /// failure of the code attempting to report that outage.
    /// </summary>
    private async Task NotifyHealthObserverAsync(string provider, ProviderCredentialOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await _healthObserver.RecordAsync(provider, outcome, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider health observer failed for provider '{Provider}'.", provider);
        }
    }

    /// <summary>
    /// Best-effort extraction of the upstream HTTP status code from a refresh failure.
    ///
    /// <para>
    /// <see cref="HttpRequestException.StatusCode"/> is populated when the failure came from
    /// <c>EnsureSuccessStatusCode</c>, which is the path the observed outage took. It is left null
    /// for transport-level faults (DNS, connection reset) where no status exists - null means "no
    /// status observed", never zero, so that a missing measurement is not presented as a real one.
    /// </para>
    /// </summary>
    private static int? ExtractStatusCode(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return (int)status;
            }
        }

        return null;
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

    private bool TryGetAuthEntry(string provider, out string authKey, out AuthEntry entry)
    {
        lock (_sync)
        {
            authKey = provider;
            if (_entries.TryGetValue(provider, out entry!)) return true;
            if (IsCopilotType(provider))
            {
                authKey = string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase) ? "github-copilot" : "copilot";
                if (_entries.TryGetValue(authKey, out entry!)) return true;
            }
            return false;
        }
    }

    private bool UpdateEntry(string provider, AuthEntry expected, AuthEntry entry)
    {
        lock (_sync)
        {
            LoadAuthEntries();
            // Never overwrite a login/rotation that completed while the old refresh was in flight.
            if (!_entries.TryGetValue(provider, out var current) || !ReferenceEquals(current, expected))
            {
                expected.AttributionUnavailable = true;
                return false;
            }
            expected.AttributionUnavailable = true;
            entry.AttributionUnavailable = false;
            _entries[provider] = entry;
            SaveAuthEntries();
            return true;
        }
    }

    /// <summary>
    /// Runs a provider call with a single, bounded invalidate-and-retry on an authentication
    /// failure (#3833).
    ///
    /// <para>
    /// #3673 shortened the stale-credential window from "until a gateway restart" to "until the next
    /// resolution", but it could not remove the call that <i>discovers</i> the staleness: a turn
    /// already in flight when the credential rotates still spends a real provider round trip and
    /// surfaces an opaque 403 that advises rotating a key which has already been rotated. This is
    /// the seam that closes that gap - it hands the resolved credential to the operation, and on a
    /// 401/403 drops the cache via <see cref="InvalidateCache"/>, re-resolves from disk and runs the
    /// operation exactly once more.
    /// </para>
    ///
    /// <para>
    /// The bound is <b>structural, not configured</b>. There is no loop and no retry count: the
    /// second attempt is a straight-line second call, so "at most one forced reload per failed call"
    /// is a property of the shape of this method rather than of a policy object someone could later
    /// widen. A second failure propagates unmodified, so the caller sees the provider's own message
    /// and not a wrapper.
    /// </para>
    ///
    /// <para>
    /// Only <see cref="ProviderAuthenticationException"/> triggers the retry. A 500 or a timeout is
    /// not evidence that the cached credential is wrong, and spending an invalidation on one would
    /// turn every upstream outage into a disk re-read storm across every in-flight turn.
    /// </para>
    /// </summary>
    /// <typeparam name="TResult">The provider call's result type.</typeparam>
    /// <param name="provider">The provider whose credential backs the call.</param>
    /// <param name="operation">
    /// The provider call, receiving the freshly resolved API key (null when none is configured, so
    /// the provider's own environment fallback still applies) and the cancellation token.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TResult> InvokeWithAuthRetryAsync<TResult>(
        string provider,
        Func<string?, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderAuthenticationException ex)
        {
            _logger.LogWarning(
                "Provider '{Provider}' rejected the cached credential (HTTP {StatusCode}); invalidating " +
                "the auth cache and retrying once (#3833).",
                provider,
                ex.StatusCode is { } status ? (int)status : 0);

            InvalidateCache();
        }

        // Outside the catch deliberately: the retry's own failure must reach the caller as itself,
        // not as an exception thrown while handling another one.
        var refreshedKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return await operation(refreshedKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the cached <c>auth.json</c> entries so the next resolution re-reads from disk (#3673).
    ///
    /// <para>
    /// The mtime check below already covers the ordinary rotation case. This is the explicit escape
    /// hatch for a caller that has independent evidence the cache is wrong - most usefully a provider
    /// that just answered 401/403 - and does not want to depend on filesystem timestamp granularity
    /// to find out.
    /// </para>
    /// </summary>
    public void InvalidateCache()
    {
        lock (_sync)
        {
            _loaded = false;
            _loadedSignature = null;
        }
    }

    private void LoadAuthEntries()
    {
        lock (_sync)
        {
            var signature = ComputeAuthFileSignature();
            if (_loaded && string.Equals(_loadedSignature, signature, StringComparison.Ordinal))
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
                catch (Exception)
                {
                    // Auth paths and parser diagnostics can contain private data; log neither.
                    _logger.LogWarning("Failed to parse auth file.");
                }
            }

            _loaded = true;
            _loadedSignature = signature;
        }
    }

    /// <summary>
    /// Stats the candidate auth files and renders their observable state as a comparison token (#3673).
    /// Last-write time alone is not enough on filesystems with coarse timestamp granularity, so the
    /// length participates too - two rewrites within the same tick that changed the token almost
    /// always differ in length, and the explicit <see cref="InvalidateCache"/> seam covers the rest.
    /// A stat failure renders as a stable marker rather than a changing one: a transient error must
    /// not be able to turn every subsequent resolution into a disk read.
    /// </summary>
    private string ComputeAuthFileSignature()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var candidatePath in new[] { _legacyAuthFilePath, _authFilePath })
        {
            builder.Append(candidatePath).Append('|');
            try
            {
                var info = _fileSystem.FileInfo.New(candidatePath);
                if (info.Exists)
                {
                    builder.Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                }
                else
                {
                    builder.Append("absent");
                }
            }
            catch (Exception)
            {
                // Auth paths and filesystem exceptions can contain private data; log neither.
                _logger.LogDebug("Failed to stat auth file for staleness detection.");
                builder.Append("unknown");
            }

            builder.Append(';');
        }

        return builder.ToString();
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

        // #3673: this process just rewrote the file, so the cache already matches what is on disk.
        // Re-baselining the signature keeps an in-process refresh from being mistaken for a foreign
        // rotation and forcing a pointless re-read of our own write.
        _loadedSignature = ComputeAuthFileSignature();
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

    /// <summary>
    /// A single credential record from <c>auth.json</c>. Internal rather than private so the
    /// refresh seam above can be substituted from tests (#3281); it is not part of the public API.
    /// </summary>
    internal sealed class AuthEntry
    {
        // Process-local provenance only: never persisted, never changes inference selection.
        // Replacement/reload naturally discards it; successful refresh restores attribution.
        [JsonIgnore]
        internal bool AttributionUnavailable { get; set; }

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
