using BotNexus.Gateway.Providers;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Live provider rate-limit headroom and observed burn rate, for the portal's usage panel.
/// </summary>
/// <remarks>
/// Read-only and cheap: everything served here is already in memory, so the portal can poll it on a
/// light interval. Authenticated by <c>GatewayAuthMiddleware</c> like every other <c>/api/*</c>
/// route, so no auth attributes are declared here.
/// </remarks>
[ApiController]
[Route("api/providers")]
public sealed class ProviderUsageController(IProviderUsageStore store) : ControllerBase
{
    private readonly IProviderUsageStore _store = store;

    /// <summary>Default burn window when the caller does not choose one.</summary>
    private const int DefaultWindowMinutes = 60;

    /// <summary>
    /// Returns headroom and burn for every provider observed since the gateway started.
    /// </summary>
    /// <param name="windowMinutes">
    /// Burn window in minutes. Clamped to 1..1440; the store retains 24 hours, so a longer request
    /// would silently report a partial window rather than the one asked for.
    /// </param>
    /// <returns>200 with a payload that is always shaped the same, even when nothing is observed yet.</returns>
    [HttpGet("usage")]
    public IActionResult GetUsage([FromQuery] int windowMinutes = DefaultWindowMinutes)
    {
        var window = Math.Clamp(windowMinutes, 1, 1440);
        var since = DateTimeOffset.UtcNow.AddMinutes(-window);
        var samples = _store.SamplesSince(since);

        var providers = _store.Snapshots.Values
            .OrderBy(s => s.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ProviderUsageDto
            {
                Provider = s.Provider,
                ObservedAtUtc = s.ObservedAtUtc,
                Limits = BuildLimits(s),
                Burn = BuildBurn(samples.Where(x =>
                    string.Equals(x.Provider, s.Provider, StringComparison.OrdinalIgnoreCase)).ToList()),
            })
            .ToList();

        return Ok(new ProviderUsageResponseDto { WindowMinutes = window, Providers = providers });
    }

    private static List<RateLimitDimensionDto> BuildLimits(ProviderRateLimitSnapshot s)
    {
        var dims = new List<RateLimitDimensionDto>();
        Add("requests", "Requests", s.RequestsLimit, s.RequestsRemaining, s.RequestsResetUtc);
        Add("inputTokens", "Input tokens", s.InputTokensLimit, s.InputTokensRemaining, s.InputTokensResetUtc);
        Add("outputTokens", "Output tokens", s.OutputTokensLimit, s.OutputTokensRemaining, s.OutputTokensResetUtc);
        Add("tokens", "Total tokens", s.TokensLimit, s.TokensRemaining, s.TokensResetUtc);
        return dims;

        void Add(string id, string label, long? limit, long? remaining, DateTimeOffset? reset)
        {
            // A dimension the provider did not report is omitted, not rendered as zero. Zero
            // remaining and "not reported" mean opposite things to someone reading a burn gauge.
            if (limit is not > 0 || remaining is null) return;
            var used = Math.Max(0, limit.Value - remaining.Value);
            dims.Add(new RateLimitDimensionDto
            {
                Id = id,
                Label = label,
                Limit = limit.Value,
                Remaining = remaining.Value,
                Used = used,
                PercentUsed = Math.Round(used * 100.0 / limit.Value, 1),
                ResetUtc = reset,
            });
        }
    }

    private static BurnDto BuildBurn(IReadOnlyList<ProviderUsageSample> samples) => new()
    {
        Requests = samples.Sum(s => s.Requests),
        Failures = samples.Sum(s => s.Failures),
        InputTokens = samples.Sum(s => s.InputTokens),
        OutputTokens = samples.Sum(s => s.OutputTokens),
        Models = [.. samples
            .GroupBy(s => s.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelBurnDto
            {
                Model = g.Key,
                Requests = g.Sum(x => x.Requests),
                Failures = g.Sum(x => x.Failures),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
            })
            .OrderByDescending(m => m.InputTokens + m.OutputTokens)
            .ThenByDescending(m => m.Requests)],
    };
}

/// <summary>Top-level payload for <c>GET /api/providers/usage</c>.</summary>
public sealed class ProviderUsageResponseDto
{
    /// <summary>The burn window actually applied, after clamping.</summary>
    public int WindowMinutes { get; init; }

    /// <summary>One entry per provider observed since startup.</summary>
    public IReadOnlyList<ProviderUsageDto> Providers { get; init; } = [];
}

/// <summary>Headroom and burn for one provider.</summary>
public sealed class ProviderUsageDto
{
    /// <summary>Canonical provider id.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>When the underlying headers were last seen.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Only the dimensions this provider actually reports.</summary>
    public IReadOnlyList<RateLimitDimensionDto> Limits { get; init; } = [];

    /// <summary>Observed consumption over the requested window.</summary>
    public BurnDto Burn { get; init; } = new();
}

/// <summary>One rate-limit dimension.</summary>
public sealed class RateLimitDimensionDto
{
    /// <summary>Stable id, for keying in the UI.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Allowance for the window.</summary>
    public long Limit { get; init; }

    /// <summary>Allowance still available.</summary>
    public long Remaining { get; init; }

    /// <summary>Allowance consumed.</summary>
    public long Used { get; init; }

    /// <summary>Consumed as a percentage, one decimal.</summary>
    public double PercentUsed { get; init; }

    /// <summary>When the allowance refills, when the provider states it.</summary>
    public DateTimeOffset? ResetUtc { get; init; }
}

/// <summary>Observed consumption for a provider over the window.</summary>
public sealed class BurnDto
{
    /// <summary>Calls observed. Exact.</summary>
    public long Requests { get; init; }

    /// <summary>Calls that returned a non-success status. Exact.</summary>
    public long Failures { get; init; }

    /// <summary>Input tokens observed. Derived from allowance deltas.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens observed. Derived from allowance deltas.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Per-model split, heaviest first.</summary>
    public IReadOnlyList<ModelBurnDto> Models { get; init; } = [];
}

/// <summary>Observed consumption for one model.</summary>
public sealed class ModelBurnDto
{
    /// <summary>Model id as named in the request.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Calls observed for this model.</summary>
    public long Requests { get; init; }

    /// <summary>Calls for this model that returned a non-success status.</summary>
    public long Failures { get; init; }

    /// <summary>Input tokens attributed to this model.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens attributed to this model.</summary>
    public long OutputTokens { get; init; }
}
