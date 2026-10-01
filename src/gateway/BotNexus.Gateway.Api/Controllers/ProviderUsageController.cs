using BotNexus.Gateway.Providers;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Live provider rate-limit headroom and observed burn rate.</summary>
[ApiController]
[Route("api/providers")]
public sealed class ProviderUsageController(IProviderUsageStore store) : ControllerBase
{
    private const int DefaultWindowMinutes = 60;
    private readonly IProviderUsageStore _store = store;

    /// <summary>Returns headroom and retained burn for every observed provider.</summary>
    [HttpGet("usage")]
    public IActionResult GetUsage([FromQuery] int windowMinutes = DefaultWindowMinutes)
    {
        var window = Math.Clamp(windowMinutes, 1, 1440);
        var query = _store.QuerySince(DateTimeOffset.UtcNow.AddMinutes(-window));
        var providers = _store.Snapshots.Values
            .OrderBy(snapshot => snapshot.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(snapshot => new ProviderUsageDto
            {
                Provider = snapshot.Provider,
                ObservedAtUtc = snapshot.ObservedAtUtc,
                Limits = BuildLimits(snapshot),
                Burn = BuildBurn(query.Samples.Where(sample =>
                    string.Equals(sample.Provider, snapshot.Provider, StringComparison.OrdinalIgnoreCase)).ToList()),
            })
            .ToList();

        return Ok(new ProviderUsageResponseDto
        {
            WindowMinutes = window,
            IsTruncated = query.IsTruncated,
            Providers = providers,
        });
    }

    private static List<RateLimitDimensionDto> BuildLimits(ProviderRateLimitSnapshot snapshot)
    {
        var dimensions = new List<RateLimitDimensionDto>();
        Add("requests", "Requests", snapshot.RequestsLimit, snapshot.RequestsRemaining, snapshot.RequestsResetUtc);
        Add("inputTokens", "Input tokens", snapshot.InputTokensLimit, snapshot.InputTokensRemaining, snapshot.InputTokensResetUtc);
        Add("outputTokens", "Output tokens", snapshot.OutputTokensLimit, snapshot.OutputTokensRemaining, snapshot.OutputTokensResetUtc);
        Add("tokens", "Total tokens", snapshot.TokensLimit, snapshot.TokensRemaining, snapshot.TokensResetUtc);
        return dimensions;

        void Add(string id, string label, long? limit, long? remaining, DateTimeOffset? reset)
        {
            if (limit is not > 0 || remaining is null) return;
            var used = Math.Max(0, limit.Value - remaining.Value);
            dimensions.Add(new RateLimitDimensionDto
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
        Requests = samples.Sum(sample => sample.Requests),
        Failures = samples.Sum(sample => sample.Failures),
        InputTokens = SumAvailable(samples.Select(sample => sample.InputTokens)),
        OutputTokens = SumAvailable(samples.Select(sample => sample.OutputTokens)),
        TotalTokens = SumAvailable(samples.Select(sample => sample.TotalTokens)),
        Models = [.. samples
            .GroupBy(sample => sample.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelBurnDto
            {
                Model = group.Key,
                ModelKnown = group.Key is not null,
                ModelDisplayName = group.Key ?? "Unknown model",
                Requests = group.Sum(sample => sample.Requests),
                Failures = group.Sum(sample => sample.Failures),
                InputTokens = SumAvailable(group.Select(sample => sample.InputTokens)),
                OutputTokens = SumAvailable(group.Select(sample => sample.OutputTokens)),
                TotalTokens = SumAvailable(group.Select(sample => sample.TotalTokens)),
            })
            .OrderByDescending(model => model.TotalTokens ?? (model.InputTokens ?? 0) + (model.OutputTokens ?? 0))
            .ThenByDescending(model => model.Requests)],
    };

    private static long? SumAvailable(IEnumerable<long?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value.GetValueOrDefault()).ToList();
        return available.Count == 0 ? null : available.Sum();
    }
}

/// <summary>Top-level payload for <c>GET /api/providers/usage</c>.</summary>
public sealed class ProviderUsageResponseDto
{
    /// <summary>The burn window actually applied.</summary>
    public int WindowMinutes { get; init; }

    /// <summary>True when bounded retention removed any sample in the requested global window.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>One entry per provider observed since startup.</summary>
    public IReadOnlyList<ProviderUsageDto> Providers { get; init; } = [];
}

/// <summary>Headroom and burn for one provider.</summary>
public sealed class ProviderUsageDto
{
    /// <summary>Canonical provider id.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>When the latest non-stale response was observed.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Dimensions the provider has reported.</summary>
    public IReadOnlyList<RateLimitDimensionDto> Limits { get; init; } = [];

    /// <summary>Observed consumption over the retained part of the requested window.</summary>
    public BurnDto Burn { get; init; } = new();
}

/// <summary>One rate-limit dimension.</summary>
public sealed class RateLimitDimensionDto
{
    /// <summary>Stable dimension id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable dimension label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Provider-stated allowance.</summary>
    public long Limit { get; init; }

    /// <summary>Provider-stated remaining allowance.</summary>
    public long Remaining { get; init; }

    /// <summary>Calculated used allowance.</summary>
    public long Used { get; init; }

    /// <summary>Calculated percentage used.</summary>
    public double PercentUsed { get; init; }

    /// <summary>Provider-stated reset instant, when available.</summary>
    public DateTimeOffset? ResetUtc { get; init; }
}

/// <summary>Observed consumption for a provider over the retained part of the window.</summary>
public sealed class BurnDto
{
    /// <summary>Response-bearing wire attempts.</summary>
    public long Requests { get; init; }

    /// <summary>Wire attempts returning non-success responses.</summary>
    public long Failures { get; init; }

    /// <summary>Derived input-token consumption, or null when unavailable.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Derived output-token consumption, or null when unavailable.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Derived combined-token consumption, or null when unavailable.</summary>
    public long? TotalTokens { get; init; }

    /// <summary>Consumption grouped by known or unavailable model identity.</summary>
    public IReadOnlyList<ModelBurnDto> Models { get; init; } = [];
}

/// <summary>Observed consumption for one known model id or the explicit unavailable identity.</summary>
public sealed class ModelBurnDto
{
    /// <summary>Literal model id, or null when identity was unavailable.</summary>
    public string? Model { get; init; }

    /// <summary>Whether <see cref="Model"/> is a known literal id.</summary>
    public bool ModelKnown { get; init; }

    /// <summary>Display-safe label for known and unavailable identities.</summary>
    public string ModelDisplayName { get; init; } = string.Empty;

    /// <summary>Response-bearing wire attempts attributed to this identity.</summary>
    public long Requests { get; init; }

    /// <summary>Non-success responses attributed to this identity.</summary>
    public long Failures { get; init; }

    /// <summary>Derived input-token consumption, or null when unavailable.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Derived output-token consumption, or null when unavailable.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Derived combined-token consumption, or null when unavailable.</summary>
    public long? TotalTokens { get; init; }
}
