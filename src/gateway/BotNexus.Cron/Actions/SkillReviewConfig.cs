using System.Text.Json;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591

/// <summary>
/// Configuration for the optional post-turn skill-review loop, read from <c>CronJob.Metadata</c>.
/// The loop is <b>disabled by default</b> so the feature is non-breaking - a job must explicitly
/// opt in via <c>skillReview.enabled = true</c> (or metadata key <c>enabled</c>).
/// </summary>
public sealed record SkillReviewConfig
{
    /// <summary>
    /// Whether the skill-review pass runs at all. Defaults to <c>false</c> (off) so existing jobs
    /// are unaffected until an operator explicitly enables the loop.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Minimum number of tool calls in a turn that qualifies it for review. Defaults to 5.
    /// </summary>
    public int MinToolCalls { get; init; } = 5;

    /// <summary>
    /// Optional human-readable summary of the turn under review, surfaced to the reviewer prompt.
    /// </summary>
    public string? SessionSummary { get; init; }

    /// <summary>
    /// Reads skill-review configuration from cron job metadata. Recognises the flat keys
    /// <c>skillReviewEnabled</c>/<c>enabled</c>, <c>skillReviewMinToolCalls</c>/<c>minToolCalls</c>,
    /// and <c>skillReviewSummary</c>/<c>sessionSummary</c>. Missing keys fall back to the
    /// default-off, threshold-5 configuration.
    /// </summary>
    public static SkillReviewConfig FromMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        var enabled = MetadataReader.GetBool(metadata, "skillReviewEnabled", "enabled", defaultValue: false);
        var minToolCalls = MetadataReader.GetInt(metadata, "skillReviewMinToolCalls", "minToolCalls", defaultValue: 5);
        var summary = MetadataReader.GetString(metadata, "skillReviewSummary", "sessionSummary");

        return new SkillReviewConfig
        {
            Enabled = enabled,
            MinToolCalls = minToolCalls < 1 ? 1 : minToolCalls,
            SessionSummary = summary
        };
    }
}

/// <summary>
/// The per-turn signals that determine whether a post-turn skill review should run. Populated from
/// cron job metadata (set by the turn that scheduled the review) - see <see cref="FromMetadata"/>.
/// </summary>
public sealed record SkillReviewSignals
{
    /// <summary>Number of tool calls made during the turn.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Whether a skill was loaded during the turn.</summary>
    public bool SkillWasLoaded { get; init; }

    /// <summary>Whether the user corrected the agent or expressed frustration.</summary>
    public bool UserCorrectedOrFrustrated { get; init; }

    /// <summary>Whether a reusable workflow or non-trivial procedure was discovered.</summary>
    public bool DiscoveredReusableWorkflow { get; init; }

    /// <summary>Whether a <c>skill_manage</c> operation failed during the turn.</summary>
    public bool SkillManageFailed { get; init; }

    /// <summary>Whether a loaded skill was found to be stale or incorrect.</summary>
    public bool LoadedSkillFoundStale { get; init; }

    /// <summary>
    /// Reads the per-turn review signals from cron job metadata. All flags default to <c>false</c>
    /// and the tool-call count to 0 when absent.
    /// </summary>
    public static SkillReviewSignals FromMetadata(IReadOnlyDictionary<string, object?>? metadata)
        => new()
        {
            ToolCallCount = MetadataReader.GetInt(metadata, "toolCallCount", "toolCalls", defaultValue: 0),
            SkillWasLoaded = MetadataReader.GetBool(metadata, "skillWasLoaded", "skillLoaded", defaultValue: false),
            UserCorrectedOrFrustrated = MetadataReader.GetBool(metadata, "userCorrectedOrFrustrated", "userFrustrated", defaultValue: false),
            DiscoveredReusableWorkflow = MetadataReader.GetBool(metadata, "discoveredReusableWorkflow", "reusableWorkflow", defaultValue: false),
            SkillManageFailed = MetadataReader.GetBool(metadata, "skillManageFailed", "skillFailed", defaultValue: false),
            LoadedSkillFoundStale = MetadataReader.GetBool(metadata, "loadedSkillFoundStale", "skillStale", defaultValue: false)
        };
}

/// <summary>
/// Small helper for reading typed values out of the loosely-typed <c>CronJob.Metadata</c> bag,
/// tolerant of <see cref="JsonElement"/> boxing that arises from JSON-sourced configuration.
/// </summary>
internal static class MetadataReader
{
    public static bool GetBool(IReadOnlyDictionary<string, object?>? metadata, string primaryKey, string? altKey, bool defaultValue)
    {
        if (!TryGet(metadata, primaryKey, altKey, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            int i => i != 0,
            long l => l != 0,
            JsonElement je when je.ValueKind is JsonValueKind.True => true,
            JsonElement je when je.ValueKind is JsonValueKind.False => false,
            JsonElement je when je.ValueKind is JsonValueKind.String && bool.TryParse(je.GetString(), out var parsed) => parsed,
            JsonElement je when je.ValueKind is JsonValueKind.Number => je.GetDouble() != 0,
            _ => defaultValue
        };
    }

    public static int GetInt(IReadOnlyDictionary<string, object?>? metadata, string primaryKey, string? altKey, int defaultValue)
    {
        if (!TryGet(metadata, primaryKey, altKey, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            JsonElement je when je.ValueKind is JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind is JsonValueKind.String && int.TryParse(je.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    public static string? GetString(IReadOnlyDictionary<string, object?>? metadata, string primaryKey, string? altKey)
    {
        if (!TryGet(metadata, primaryKey, altKey, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind is JsonValueKind.String => je.GetString(),
            _ => value.ToString()
        };
    }

    private static bool TryGet(IReadOnlyDictionary<string, object?>? metadata, string primaryKey, string? altKey, out object? value)
    {
        value = null;
        if (metadata is null)
            return false;
        if (metadata.TryGetValue(primaryKey, out value))
            return true;
        if (altKey is not null && metadata.TryGetValue(altKey, out value))
            return true;
        return false;
    }
}
