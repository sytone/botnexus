using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Admin-only local account state and explicitly requested refresh.</summary>
[ApiController]
[Route("api/copilot/quota")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CopilotQuotaController(CopilotQuotaService service) : ControllerBase
{
    private bool IsAdmin => HttpContext?.Items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var caller) == true &&
        caller is GatewayCallerIdentity { IsAdmin: true };

    /// <summary>Returns local cached state immediately; never fetches from GitHub.</summary>
    [HttpGet]
    public async Task<IActionResult> GetQuota(CancellationToken cancellationToken, [FromQuery] string instance = "github-copilot")
    {
        if (!IsAdmin) return StatusCode(StatusCodes.Status403Forbidden);
        return Ok(await service.ReadAsync(instance, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Requests a single bounded refresh; concurrent requests do not queue.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshQuota(CancellationToken cancellationToken, [FromQuery] string instance = "github-copilot", [FromQuery] bool force = false)
    {
        if (!IsAdmin) return StatusCode(StatusCodes.Status403Forbidden);
        return Ok(await service.RefreshAsync(instance, force, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Bounded process-local cache isolated by configured instance and non-secret credential generation.</summary>
public sealed class CopilotQuotaService(GatewayAuthManager authManager, CopilotDiscoveryClient discoveryClient,
    ILogger<CopilotQuotaService> logger, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private const int MaxAccounts = 64;
    private static readonly TimeSpan SuccessDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Local auth resolution also invalidates a rotated credential before exposing cached state.</summary>
    public Task<CopilotQuotaState> ReadAsync(string instance = "github-copilot", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var credential = authManager.ResolveCopilotAccountCredential(instance);
            var entry = ResolveEntry(instance, credential);
            return Task.FromResult(entry is null ? Unavailable(credential) : Project(entry));
        }
    }

    // Composite reads capture credential attribution outside this cache and verify it again after
    // header reads. Read only that exact generation; never resolve a newer credential here.
    internal CopilotQuotaState ReadForScope(BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return _entries.TryGetValue(scope.Instance, out var entry) && entry.Generation == scope.Generation
                ? Project(entry) : new CopilotQuotaState { Instance = scope.Instance, RefreshState = "not-requested", Reason = "not-yet-requested" };
        }
    }

    /// <summary>Compatibility local read; does not implicitly fetch. New callers use ReadAsync for full state.</summary>
    public async Task<CopilotQuotaDto?> GetQuotaAsync(CancellationToken cancellationToken = default)
        => (await ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Snapshots.FirstOrDefault(x => x.QuotaId == "premium_interactions");

    /// <summary>Starts at most one fetch per scope, rejects queues and honors success/failure cadence.</summary>
    public async Task<CopilotQuotaState> RefreshAsync(string instance = "github-copilot", bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CopilotAccountCredential? credential;
        Entry entry;
        lock (_sync)
        {
            credential = authManager.ResolveCopilotAccountCredential(instance);
            var resolved = ResolveEntry(instance, credential);
            if (resolved is null || credential is null) return Unavailable(credential);
            entry = resolved;
            var now = _clock.GetUtcNow();
            if (entry.Busy) return Project(entry);
            if (now < entry.NextAttempt)
            {
                entry.Throttled = true;
                return Project(entry);
            }
            if (!force && entry.LastSuccess is not null && now < entry.LastSuccess + SuccessDuration && entry.AttemptState == "success")
                return Project(entry);
            entry.Throttled = false;
            entry.Busy = true;
            entry.AttemptState = "loading";
            entry.LastAttempt = now;
            entry.NextAttempt = now + MinimumInterval;
        }
        using var deadline = new CancellationTokenSource(UpstreamTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        Task<IReadOnlyList<CopilotQuotaDto>>? upstream = null;
        try
        {
            upstream = discoveryClient.GetAccountQuotaAsync(credential.OAuthToken, _clock.GetUtcNow(), linked.Token);
            var snapshots = await upstream.WaitAsync(UpstreamTimeout, _clock, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                var current = authManager.ResolveCopilotAccountCredential(instance);
                ResolveEntry(instance, current);
                if (current?.Generation == credential.Generation && entry.Generation == credential.Generation)
                {
                    entry.Snapshots = snapshots;
                    entry.LastSuccess = _clock.GetUtcNow();
                    entry.AttemptState = "success";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync) { if (entry.Generation == credential.Generation) entry.AttemptState = "cancelled"; }
            throw;
        }
        catch (Exception)
        {
            // Never log exception messages, type names, bodies or credential/profile data.
            logger.LogWarning("Copilot account quota lookup unavailable.");
            lock (_sync)
            {
                ResolveEntry(instance, authManager.ResolveCopilotAccountCredential(instance));
                if (entry.Generation == credential.Generation) entry.AttemptState = "error";
            }
        }
        finally
        {
            linked.Cancel();
            lock (_sync)
            {
                if (entry.Generation == credential.Generation) entry.NextAttempt = _clock.GetUtcNow() + MinimumInterval;
                if (upstream is null || upstream.IsCompleted) entry.Busy = false;
                else _ = DrainAsync(upstream, entry);
            }
        }
        lock (_sync)
        {
            var currentCredential = authManager.ResolveCopilotAccountCredential(instance);
            var current = ResolveEntry(instance, currentCredential);
            return current is null ? Unavailable(currentCredential) : Project(current);
        }
    }

    private async Task DrainAsync(Task<IReadOnlyList<CopilotQuotaDto>> upstream, Entry entry)
    {
        try { await upstream.ConfigureAwait(false); }
        catch (Exception) { /* Observe detached transport failure without leaking details. */ }
        finally { lock (_sync) entry.Busy = false; }
    }

    private Entry? ResolveEntry(string requested, CopilotAccountCredential? credential)
    {
        var scope = credential?.Instance ?? (string.Equals(requested, "copilot", StringComparison.OrdinalIgnoreCase) ? "github-copilot" : requested.Trim().ToLowerInvariant());
        if (credential is null)
        {
            if (_entries.TryGetValue(scope, out var unavailable))
            {
                unavailable.Snapshots = [];
                unavailable.LastSuccess = null;
                unavailable.Generation = string.Empty;
                unavailable.LastAttempt = null;
                unavailable.AttemptState = "unavailable";
                unavailable.NextAttempt = default;
                unavailable.Throttled = false;
            }
            return null;
        }
        if (_entries.TryGetValue(scope, out var entry))
        {
            if (entry.Generation != credential.Generation)
            {
                // Preserve the old in-flight slot until it drains, but never its account data.
                entry.Generation = credential.Generation;
                entry.Snapshots = [];
                entry.LastSuccess = null;
                entry.LastAttempt = null;
                entry.AttemptState = "unavailable";
                entry.NextAttempt = default;
                entry.Throttled = false;
            }
            return entry;
        }
        // Admission fails closed instead of evicting a busy entry and permitting another flight.
        if (_entries.Count >= MaxAccounts) return null;
        entry = new Entry { Scope = scope, Generation = credential.Generation };
        _entries.Add(scope, entry);
        return entry;
    }

    private static CopilotQuotaState Unavailable(CopilotAccountCredential? credential) => new()
    {
        Reason = credential is null ? "identity-unavailable" : "capacity-unavailable"
    };

    private CopilotQuotaState Project(Entry entry)
    {
        var stale = entry.LastSuccess is not null && (entry.AttemptState != "success" || _clock.GetUtcNow() >= entry.LastSuccess + SuccessDuration);
        var throttled = entry.Throttled && _clock.GetUtcNow() < entry.NextAttempt;
        var refreshState = entry.AttemptState == "loading" ? "refreshing" : throttled ? "throttled" :
            entry.LastAttempt is null ? "not-requested" : entry.AttemptState;
        var reason = refreshState switch
        {
            "refreshing" => "refresh-in-progress",
            "throttled" => "refresh-throttled",
            "not-requested" => "not-yet-requested",
            "error" => "upstream-error",
            "cancelled" => "refresh-cancelled",
            "success" when stale => "success-stale",
            "success" when entry.Snapshots.Count == 0 => "success-empty",
            "success" when entry.Snapshots.Any(x => x.IsPartial) => "success-partial",
            "success" => "success",
            _ => "identity-unavailable"
        };
        return new()
        {
            Instance = entry.Scope,
            Snapshots = entry.Snapshots,
            LastSuccessAtUtc = entry.LastSuccess,
            LastAttemptAtUtc = entry.LastAttempt,
            AttemptState = entry.AttemptState,
            IsStale = stale,
            RefreshState = refreshState,
            Reason = reason,
            NextRefreshAtUtc = entry.NextAttempt == default ? null : entry.NextAttempt
        };
    }

    private sealed class Entry
    {
        public required string Scope { get; init; }
        public required string Generation { get; set; }
        /// <summary>Last successful allow-listed dimensions, empty when unknown.</summary>
        public IReadOnlyList<CopilotQuotaDto> Snapshots { get; set; } = [];
        public DateTimeOffset? LastSuccess { get; set; }
        public DateTimeOffset? LastAttempt { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        /// <summary>Local attempt status without upstream details.</summary>
        public string AttemptState { get; set; } = "unavailable";
        public bool Busy { get; set; }
        public bool Throttled { get; set; }
    }
}

/// <summary>Allow-listed local state; unknown accounts return unavailable without reflecting input.</summary>
public sealed class CopilotQuotaState
{
    /// <summary>The observation source, not local usage.</summary>
    public string Source { get; init; } = "account-api";
    /// <summary>Canonical configured instance, or null when unavailable.</summary>
    public string? Instance { get; init; }
    /// <summary>Last successful allow-listed dimensions, empty when unknown.</summary>
    public IReadOnlyList<CopilotQuotaDto> Snapshots { get; init; } = [];
    /// <summary>Last successful observation, null before success or after rotation.</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
    /// <summary>Last attempt time, null before a refresh.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    /// <summary>Earliest permitted attempt; normal successful refresh also honors five-minute caching.</summary>
    public DateTimeOffset? NextRefreshAtUtc { get; init; }
    /// <summary>Local attempt status without upstream details.</summary>
    public string AttemptState { get; init; } = "unavailable";
    /// <summary>Fixed local refresh disposition: unavailable, not-requested, refreshing, throttled, success, error or cancelled. Does not replace the last AttemptState.</summary>
    public string RefreshState { get; init; } = "unavailable";
    /// <summary>Fixed diagnostic only: identity-unavailable, capacity-unavailable, not-yet-requested, refresh-in-progress, refresh-throttled, upstream-error, refresh-cancelled, success, success-empty, success-partial or success-stale. Never reflects request or upstream data.</summary>
    public string Reason { get; init; } = "identity-unavailable";
    /// <summary>Whether the retained success is expired or the latest attempt did not succeed.</summary>
    public bool IsStale { get; init; }
}
