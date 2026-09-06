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

        var authOutcome = await ResolveAuthEntryCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        if (authOutcome.Status != ProviderCredentialStatus.NotConfigured)
        {
            return authOutcome;
        }

        // No auth.json entry, but a key may still be declared in config or the environment. A
        // refresh failure above is returned as-is and never overwritten by a fallback lookup: the
        // fault is the more important fact about the provider's health.
        var fallbackKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(fallbackKey)
            ? ProviderCredentialOutcome.NotConfigured()
            : ProviderCredentialOutcome.Success(fallbackKey);
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
    /// <param name="sessionId">
    /// #3417: the identity of the session this background call is acting on. Threading it HERE - at
    /// the shared seam - rather than at each call site is deliberate: <c>SessionId</c> is what drives
    /// the Copilot Responses <c>prompt_cache_key</c>, and the two existing background callers
    /// (compaction, auto-title) had each independently omitted it, leaving that branch dead for the
    /// largest prompt the gateway ever sends. A third background caller now inherits the behaviour by
    /// construction instead of having to remember.
    /// <para>
    /// Typed as <see cref="SessionId"/> rather than a raw string (#3099 primitive-ID fence): the value
    /// object cannot hold a blank, so "no session identity" is representable only as <c>null</c> and
    /// an empty cache key cannot be constructed at this seam at all. A null value is inert - the
    /// caller's <paramref name="baseOptions"/> value, if any, survives untouched.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Options with the resolved key applied. A null/blank resolved key leaves <c>ApiKey</c> null so
    /// the provider falls back to environment keys - behaviour-preserving for callers that previously
    /// passed no options at all.
    /// </returns>
    public async Task<SimpleStreamOptions> CreateAuthenticatedOptionsAsync(
        string provider,
        SimpleStreamOptions? baseOptions = null,
        SessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        var options = baseOptions ?? new SimpleStreamOptions();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options = options with { ApiKey = apiKey };
        }

        // A null id must leave SessionId exactly as the caller left it. Writing null through would
        // erase a value the caller had already set, and an empty string would become an empty
        // prompt_cache_key on the wire - a different (and worse) failure than the absent key this
        // fix removes.
        if (sessionId is { } resolvedSessionId)
        {
            options = options with { SessionId = resolvedSessionId.Value };
        }

        return options;
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
        var outcome = await ResolveAuthEntryCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        return outcome.ApiKey;
    }

    /// <summary>
    /// Resolves a credential from <c>auth.json</c> and reports <em>why</em> the attempt ended as it
    /// did (#3281).
    ///
    /// <para>
    /// This method exists because the previous shape returned a bare <c>null</c> for three unrelated
    /// conditions - no auth entry, a failed refresh, and an absent credential - which made an upstream
    /// outage indistinguishable from a provider nobody had configured. The failure was logged and then
    /// discarded at this exact seam, so no caller could ever react to it. Returning the reason is what
    /// makes a provider-health signal possible at all.
    /// </para>
    ///
    /// <para>
    /// Only a <em>refresh failure</em> is reported to the health observer. A missing auth entry is a
    /// steady state, not an outage, and reporting it as one would fire a degraded-health event on every
    /// host that simply does not use a given provider.
    /// </para>
    /// </summary>
    private async Task<ProviderCredentialOutcome> ResolveAuthEntryCredentialAsync(string provider, CancellationToken cancellationToken)
    {
        LoadAuthEntries();

        if (!TryGetAuthEntry(provider, out var entry))
        {
            return ProviderCredentialOutcome.NotConfigured();
        }

        if (!NeedsRefresh(entry))
        {
            return string.IsNullOrWhiteSpace(entry.Access)
                ? ProviderCredentialOutcome.NotConfigured()
                : ProviderCredentialOutcome.Success(entry.Access);
        }

        try
        {
            var refreshed = await _refreshEntry(entry, cancellationToken).ConfigureAwait(false);
            UpdateEntry(provider, refreshed);
            var success = ProviderCredentialOutcome.Success(refreshed.Access);
            await NotifyHealthObserverAsync(provider, success, cancellationToken).ConfigureAwait(false);
            return success;
        }
        catch (OperationCanceledException)
        {
            // Caller-driven cancellation is not a provider fault and must not be reported as one.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed refreshing auth credentials for provider '{Provider}'.", provider);

            var failure = ProviderCredentialOutcome.Failed(
                ex.GetType().Name,
                ExtractStatusCode(ex),
                ex.Message);

            await NotifyHealthObserverAsync(provider, failure, cancellationToken).ConfigureAwait(false);
            return failure;
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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse auth file '{AuthPath}'.", candidatePath);
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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stat auth file '{AuthPath}' for staleness detection.", candidatePath);
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
