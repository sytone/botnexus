using System.Globalization;
using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot.Discovery;

/// <summary>Allow-listed quota projection. Missing or invalid fields are unknown, never zero.</summary>
public sealed class CopilotQuotaDto
{
    /// <summary>Account API observation source.</summary>
    public string Source { get; init; } = "account-api";
    /// <summary>Recognized quota dimension.</summary>
    public required string QuotaId { get; init; }
    /// <summary>Provider quota units; no billing-credit conversion is asserted.</summary>
    public string Unit { get; init; } = "provider quota units";
    /// <summary>Finite nonnegative allowance, or unknown.</summary>
    public decimal? Entitlement { get; init; }
    /// <summary>Finite nonnegative remaining allowance; negative unlimited sentinels become unknown.</summary>
    public decimal? Remaining { get; init; }
    /// <summary>Provider-reported percentage in [0,100], or unknown.</summary>
    public decimal? PercentRemaining { get; init; }
    /// <summary>Finite nonnegative overage amount, or unknown.</summary>
    public decimal? OverageCount { get; init; }
    /// <summary>Provider permission, or unknown when omitted.</summary>
    public bool? OveragePermitted { get; init; }
    /// <summary>Explicit provider unlimited state, or unknown when omitted.</summary>
    public bool? IsUnlimited { get; init; }
    /// <summary>Validated ISO date, or unknown.</summary>
    public string? ResetDate { get; init; }
    /// <summary>True when any projected numeric, permission, unlimited or reset field is unknown.</summary>
    public bool IsPartial => Entitlement is null || Remaining is null || PercentRemaining is null ||
        OverageCount is null || OveragePermitted is null || IsUnlimited is null || ResetDate is null;
    /// <summary>UTC account response observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>Bounded allow-listed parser independent of the legacy CLI discovery model.</summary>
public static class CopilotAccountQuotaParser
{
    /// <summary>Projects only known dimensions and finite valid fields; ignores raw private profile fields.</summary>
    public static IReadOnlyList<CopilotQuotaDto> Parse(this JsonElement root, DateTimeOffset observedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object)
            return [];
        string? reset = null;
        if (root.TryGetProperty("quota_reset_date", out var date) && date.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            reset = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var result = new List<CopilotQuotaDto>(3);
        foreach (var dimension in new[] { "chat", "completions", "premium_interactions" })
        {
            if (!snapshots.TryGetProperty(dimension, out var item) || item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("quota_id", out var id) && (id.ValueKind != JsonValueKind.String || id.GetString() != dimension)) continue;
            result.Add(new CopilotQuotaDto
            {
                QuotaId = dimension,
                Entitlement = Number(item, "entitlement"), Remaining = Number(item, "quota_remaining"),
                PercentRemaining = Number(item, "percent_remaining", 100), OverageCount = Number(item, "overage_count"),
                OveragePermitted = Boolean(item, "overage_permitted"), IsUnlimited = Boolean(item, "unlimited"),
                ResetDate = reset, ObservedAtUtc = observedAtUtc
            });
        }
        return result.AsReadOnly();
    }

    private static decimal? Number(JsonElement item, string name, decimal maximum = decimal.MaxValue) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
        number >= 0 && number <= maximum ? number : null;
    private static bool? Boolean(JsonElement item, string name) => item.TryGetProperty(name, out var value) ?
        value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : null : null;
}
