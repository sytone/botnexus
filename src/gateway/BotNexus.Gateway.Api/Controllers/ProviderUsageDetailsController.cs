using System.Collections.Immutable;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Cron;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Admin-only companion projection; the existing observed-request usage route is unchanged.</summary>
[ApiController]
[Route("api/providers/usage/details")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProviderUsageDetailsController(ProviderUsageDetailsService service) : ControllerBase
{
    /// <summary>Reads local quota observations and bounded gateway-wide scheduled activity; never refreshes upstream.</summary>
    [HttpGet]
    public async Task<IActionResult> GetDetails(CancellationToken cancellationToken,
        [FromQuery] string instance = "github-copilot", [FromQuery] DateTimeOffset? startUtc = null, [FromQuery] DateTimeOffset? endUtc = null)
    {
        if (HttpContext?.Items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var caller) != true ||
            caller is not GatewayCallerIdentity { IsAdmin: true }) return StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            return Ok(await service.ReadAsync(instance, startUtc, endUtc, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProviderUsageDetailsError("Invalid activity interval."));
        }
    }
}

/// <summary>Sanitized validation response.</summary>
/// <param name="Error">Fixed public validation text.</param>
public sealed record ProviderUsageDetailsError(string Error);

/// <summary>Local observations with separate SQL activity, never an estimated provider headroom calculation.</summary>
public sealed class ProviderUsageDetailsService(GatewayAuthManager authManager, CopilotQuotaService accountService,
    CopilotHeaderQuotaStore headerStore, ICronStore cronStore, IOptionsMonitor<PlatformConfig> config, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly (CopilotQuotaDimension Dimension, string Id)[] Dimensions =
    [ (CopilotQuotaDimension.Chat, "chat"), (CopilotQuotaDimension.Completions, "completions"), (CopilotQuotaDimension.PremiumInteractions, "premium_interactions") ];

    /// <summary>Returns a bounded allowlisted projection; one retry prevents old-account facts crossing a credential rotation.</summary>
    public async Task<ProviderUsageDetails> ReadAsync(string instance = "github-copilot", DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow().ToUniversalTime();
        var end = (endUtc ?? now).ToUniversalTime();
        var start = startUtc?.ToUniversalTime() ?? (end.Ticks < TimeSpan.TicksPerDay ? DateTimeOffset.MinValue : end.AddDays(-1));
        if (start > end || (start == end && (startUtc.HasValue || endUtc.HasValue)))
            throw new ArgumentException("Invalid activity interval.");
        ScheduledUsageDetails scheduled;
        try
        {
            var activity = await cronStore.GetRunActivityAsync(new() { StartInclusive = start, EndExclusive = end, TopJobLimit = 10 }, cancellationToken).ConfigureAwait(false);
            scheduled = new()
            {
                State = "available", ObservedAtUtc = now,
                IsPartial = activity.WindowTruncatedByNow || activity.WindowTruncatedByRetention ||
                    activity.Totals.MeasuredRunCount < activity.Totals.RunCount || activity.Totals.RunningRunCount > 0 || activity.Totals.UnfinalizedRunCount > 0 ||
                    activity.Totals.PromptTokenRunCount < activity.Totals.RunCount || activity.Totals.CompletionTokenRunCount < activity.Totals.RunCount ||
                    activity.Totals.TurnRunCount < activity.Totals.RunCount || activity.Totals.ToolCallRunCount < activity.Totals.RunCount ||
                    activity.Totals.DurationRunCount < activity.Totals.RunCount,
                Data = new(activity.RequestedStartInclusiveUtc, activity.RequestedEndExclusiveUtc, activity.EffectiveStartInclusiveUtc,
                    activity.EffectiveEndExclusiveUtc, activity.WindowTruncatedByRetention, activity.WindowTruncatedByNow,
                    activity.Totals, activity.TopJobs.Take(10).Select(x => new ScheduledUsageJob(x.JobId.Value, x.Totals)).ToImmutableArray())
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Store capabilities, overflow, SQLite diagnostics and paths never escape this boundary.
            scheduled = new() { ObservedAtUtc = now };
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Resolve account/header attribution after the only await, including sanitized store failures.
        // Sample local freshness now rather than before a potentially slow SQL read.
        return ReadLocal(instance, _clock.GetUtcNow().ToUniversalTime(), cancellationToken) with { Scheduled = scheduled };
    }

    private ProviderUsageDetails ReadLocal(string instance, DateTimeOffset now, CancellationToken ct)
    {
        var instances = AvailableInstances();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var before = authManager.ResolveCopilotAccountCredential(instance);
            if (before is null) break;
            var scope = new CopilotHeaderScope(before.Instance, before.Generation);
            var account = accountService.ReadForScope(scope, ct);
            var headers = Dimensions.Select(d => (d.Id, Observation: headerStore.GetLatest(scope, d.Dimension)))
                .Where(x => x.Observation is not null)
                .Select(x => ProjectHeader(x.Id, x.Observation ?? throw new InvalidOperationException(), now)).ToImmutableArray();
            var after = authManager.ResolveCopilotAccountCredential(instance);
            if (after?.Instance == before.Instance && after.Generation == before.Generation)
                return new()
                {
                    Instance = before.Instance, AvailableInstances = instances,
                    Account = new()
                    {
                        State = account.LastSuccessAtUtc is null ? "unavailable" : account.IsStale ? "stale" : "fresh",
                        Snapshots = account.Snapshots.Take(3).ToImmutableArray(), LastSuccessAtUtc = account.LastSuccessAtUtc,
                        LastAttemptAtUtc = account.LastAttemptAtUtc, NextRefreshAtUtc = account.NextRefreshAtUtc, AttemptState = account.AttemptState
                    }, Headers = headers, LegacyUnattributedHeaderResponses = headerStore.LegacyUnattributedResponses
                };
        }
        return new() { AvailableInstances = instances, LegacyUnattributedHeaderResponses = headerStore.LegacyUnattributedResponses };
    }

    private ImmutableArray<ProviderUsageInstance> AvailableInstances()
    {
        var providers = config.CurrentValue.Providers;
        static bool Copilot(string type) => type.Equals("github-copilot", StringComparison.OrdinalIgnoreCase) || type.Equals("copilot", StringComparison.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var canonicalConfigured = false;
        if (providers is not null)
            foreach (var pair in providers)
            {
                var canonical = Copilot(pair.Key);
                canonicalConfigured |= canonical;
                if (!pair.Value.Enabled || !Copilot(pair.Value.Type ?? pair.Key) || pair.Key.Length > 128 || pair.Key.Any(char.IsControl)) continue;
                names.Add(canonical ? "github-copilot" : pair.Key.Trim().ToLowerInvariant());
            }
        if (!canonicalConfigured) names.Add("github-copilot");
        return names.Order(StringComparer.Ordinal).Take(64).Select(x => new ProviderUsageInstance(x, "github-copilot")).ToImmutableArray();
    }

    private static HeaderUsageDetails ProjectHeader(string id, CopilotHeaderObservation observation, DateTimeOffset now) => new()
    {
        QuotaId = id, State = now - observation.ObservedAt >= TimeSpan.FromMinutes(5) ? "stale" : "fresh",
        ObservedAtUtc = observation.ObservedAt.ToUniversalTime(), Entitlement = observation.Quota.Entitlement,
        RemainingPercent = observation.Quota.RemainingPercent, TotalRemainingCount = observation.Quota.TotalRemainingCount,
        OverageCount = observation.Quota.OverageCount, OveragePermitted = observation.Quota.OveragePermitted,
        ResetAtUtc = observation.Quota.ResetAt?.ToUniversalTime(), IsUnlimited = observation.Quota.IsUnlimited
    };
}

/// <summary>Configured instance name and type only, without auth references or endpoints.</summary>
/// <param name="Instance">Normalized configured instance or canonical default alias.</param>
/// <param name="Type">Built-in provider type.</param>
public sealed record ProviderUsageInstance(string Instance, string Type);

/// <summary>Immutable, bounded companion response; quota and activity are deliberately separate sources.</summary>
public sealed record ProviderUsageDetails
{
    /// <summary>Verified account instance; null for unknown, unsupported or unstable credentials.</summary>
    public string? Instance { get; init; }
    /// <summary>At most 64 enabled Copilot instance names and types, independent of credential availability.</summary>
    public ImmutableArray<ProviderUsageInstance> AvailableInstances { get; init; } = [];
    /// <summary>Local account API cache state, never an implicit upstream request.</summary>
    public AccountUsageDetails Account { get; init; } = new();
    /// <summary>At most three current-generation response-header observations.</summary>
    public ImmutableArray<HeaderUsageDetails> Headers { get; init; } = [];
    /// <summary>Gateway-wide legacy observations without verified account attribution; not quota units.</summary>
    public long LegacyUnattributedHeaderResponses { get; init; }
    /// <summary>Separate gateway-wide scheduled-run SQL measurements.</summary>
    public ScheduledUsageDetails Scheduled { get; init; } = new();
    /// <summary>No supported mapping combines account counts, header percentages, tokens or billing credits.</summary>
    public string QuotaMapping => "unknown; sources and units are not interchangeable";
}

/// <summary>Allowlisted local account state and observation/refresh timestamps.</summary>
public sealed record AccountUsageDetails
{
    /// <summary>Origin of the cached quota facts.</summary>
    public string Source => "account-api";
    /// <summary>Provider quota count units, not local token or request burn.</summary>
    public string Unit => "provider quota units";
    /// <summary>Fresh, stale or unavailable local success state.</summary>
    public string State { get; init; } = "unavailable";
    /// <summary>At most three allowlisted quota dimensions, preserving provider fractions.</summary>
    public ImmutableArray<CopilotQuotaDto> Snapshots { get; init; } = [];
    /// <summary>True when absent or any source fields are unknown.</summary>
    public bool IsPartial => Snapshots.IsEmpty || Snapshots.Any(x => x.IsPartial);
    /// <summary>Last successful account observation.</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
    /// <summary>Last explicit refresh attempt.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    /// <summary>Earliest locally permitted refresh attempt.</summary>
    public DateTimeOffset? NextRefreshAtUtc { get; init; }
    /// <summary>Sanitized local refresh status.</summary>
    public string AttemptState { get; init; } = "unavailable";
}

/// <summary>Header-only facts; rem percentage is never treated as a count.</summary>
public sealed record HeaderUsageDetails
{
    /// <summary>Recognized source header dimension.</summary>
    public required string QuotaId { get; init; }
    /// <summary>Response-header source, not the account API.</summary>
    public string Source => "response-headers";
    /// <summary>Count field units; no billing conversion is implied.</summary>
    public string CountUnit => "provider quota units";
    /// <summary>Unit of the rem field only.</summary>
    public string RemainingPercentUnit => "percent";
    /// <summary>Fresh or stale observation state.</summary>
    public required string State { get; init; }
    /// <summary>UTC response observation timestamp.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }
    /// <summary>Finite entitlement count, or unknown.</summary>
    public decimal? Entitlement { get; init; }
    /// <summary>Provider rem percentage, independent of remaining count.</summary>
    public decimal? RemainingPercent { get; init; }
    /// <summary>Provider totRem count, independent of rem percentage.</summary>
    public decimal? TotalRemainingCount { get; init; }
    /// <summary>Finite overage count, or unknown.</summary>
    public decimal? OverageCount { get; init; }
    /// <summary>Explicit provider overage permission, or unknown.</summary>
    public bool? OveragePermitted { get; init; }
    /// <summary>Validated explicit reset instant, or unknown.</summary>
    public DateTimeOffset? ResetAtUtc { get; init; }
    /// <summary>Established unlimited sentinel pair state, or unknown.</summary>
    public bool? IsUnlimited { get; init; }
    /// <summary>Whether any projected field remains unknown.</summary>
    public bool IsPartial => Entitlement is null || RemainingPercent is null || TotalRemainingCount is null || OverageCount is null ||
        OveragePermitted is null || ResetAtUtc is null || IsUnlimited is null;
}

/// <summary>Scheduled activity availability and honest coverage labels, including when data is empty.</summary>
public sealed record ScheduledUsageDetails
{
    /// <summary>SQL scheduled-run source, never quota or transcript estimates.</summary>
    public string Source => "scheduled-run-sql";
    /// <summary>Activity units, independent of provider quota units.</summary>
    public string Unit => "tokens, runs, turns, tool calls and milliseconds";
    /// <summary>Explicit lack of provider/account attribution.</summary>
    public string Scope => "gateway-wide, provider-unattributed";
    /// <summary>Fixed coverage disclaimer; delegated runs may overlap, and live counts are not finalized.</summary>
    public string Measurement => "Scheduled-run measurements only; delegated overlap is possible; running and unfinalized runs are incomplete.";
    /// <summary>No cache-token field is measured by this source.</summary>
    public string CacheMeasurement => "unsupported";
    /// <summary>No all-conversation burn is measured by this source.</summary>
    public string AllConversationMeasurement => "unsupported";
    /// <summary>Available (including empty) or unavailable without private diagnostic details.</summary>
    public string State { get; init; } = "unavailable";
    /// <summary>UTC time of this local activity read.</summary>
    public DateTimeOffset? ObservedAtUtc { get; init; }
    /// <summary>True for unavailable, truncated or incompletely measured intervals.</summary>
    public bool IsPartial { get; init; } = true;
    /// <summary>UTC window and SQL measurements, absent when unavailable.</summary>
    public ScheduledUsageData? Data { get; init; }
}

/// <summary>Activity window metadata survives empty results; totals always cover all matching jobs.</summary>
/// <param name="RequestedStartInclusiveUtc">Requested inclusive UTC start.</param>
/// <param name="RequestedEndExclusiveUtc">Requested exclusive UTC end.</param>
/// <param name="EffectiveStartInclusiveUtc">Retention/now-clamped UTC start.</param>
/// <param name="EffectiveEndExclusiveUtc">Now-clamped UTC end.</param>
/// <param name="WindowTruncatedByRetention">Conservative retention clamp indicator.</param>
/// <param name="WindowTruncatedByNow">Future endpoint clamp indicator.</param>
/// <param name="Totals">All matching jobs, not the top-ten subset.</param>
/// <param name="TopJobs">At most ten SQL-ranked jobs.</param>
public sealed record ScheduledUsageData(DateTimeOffset RequestedStartInclusiveUtc, DateTimeOffset RequestedEndExclusiveUtc,
    DateTimeOffset EffectiveStartInclusiveUtc, DateTimeOffset EffectiveEndExclusiveUtc, bool WindowTruncatedByRetention,
    bool WindowTruncatedByNow, CronRunActivityTotals Totals, ImmutableArray<ScheduledUsageJob> TopJobs);

/// <summary>A ranked persisted job identity and measurements, without current provider inference.</summary>
/// <param name="JobId">Persisted job identity.</param>
/// <param name="Totals">Measured activity for this job.</param>
public sealed record ScheduledUsageJob(string JobId, CronRunActivityTotals Totals);
