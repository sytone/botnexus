using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Actions;


/// <summary>
/// Configuration for the optional periodic skill-review loop, read from <c>CronJob.Metadata</c>.
/// The loop is <b>disabled by default</b> so the feature is non-breaking - a job must explicitly
/// opt in via <c>skillReview.enabled = true</c> (or metadata key <c>enabled</c>).
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="MemoryDreamingCronAction"/>, this cron carries <b>configuration</b> in its
/// metadata - never per-turn signals. At each tick the action reads a <see cref="LookbackHours"/>
/// window of live session history and derives the review signals from it (see
/// <see cref="SkillReviewSignals.FromSessions"/>). The "producer" is the normal turn-transcript
/// persistence that already accumulates in the session store, exactly as memory-dreaming's producer
/// is the daily-notes that accumulate on disk.
/// </para>
/// </remarks>
public sealed record SkillReviewConfig
{
    /// <summary>
    /// Whether the skill-review pass runs at all. Defaults to <c>false</c> (off) so existing jobs
    /// are unaffected until an operator explicitly enables the loop.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Minimum number of tool calls <b>aggregated across the lookback window</b> that qualifies the
    /// period for review. Defaults to 5.
    /// </summary>
    public int MinToolCalls { get; init; } = 5;

    /// <summary>
    /// How many hours of session history to read back at each tick. Defaults to 24. This is the
    /// analogue of memory-dreaming's <c>lookbackDays</c>: the window of live durable state the
    /// consumer aggregates signals from. Reviews are idempotent enough to tolerate overlapping
    /// windows, so no exactly-once watermark cursor is required.
    /// </summary>
    public int LookbackHours { get; init; } = 24;

    /// <summary>
    /// Upper bound on how many recent sessions (newest-first) to scan in a single pass, so a very
    /// busy agent cannot make one tick unbounded. Defaults to 50.
    /// </summary>
    public int MaxSessions { get; init; } = 50;

    /// <summary>
    /// Reads skill-review configuration from cron job metadata. Recognises the flat keys
    /// <c>skillReviewEnabled</c>/<c>enabled</c>, <c>skillReviewMinToolCalls</c>/<c>minToolCalls</c>,
    /// <c>skillReviewLookbackHours</c>/<c>lookbackHours</c>, and
    /// <c>skillReviewMaxSessions</c>/<c>maxSessions</c>. Missing keys fall back to the
    /// default-off configuration.
    /// </summary>
    public static SkillReviewConfig FromMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        var enabled = MetadataReader.GetBool(metadata, "skillReviewEnabled", "enabled", defaultValue: false);
        var minToolCalls = MetadataReader.GetInt(metadata, "skillReviewMinToolCalls", "minToolCalls", defaultValue: 5);
        var lookbackHours = MetadataReader.GetInt(metadata, "skillReviewLookbackHours", "lookbackHours", defaultValue: 24);
        var maxSessions = MetadataReader.GetInt(metadata, "skillReviewMaxSessions", "maxSessions", defaultValue: 50);

        return new SkillReviewConfig
        {
            Enabled = enabled,
            MinToolCalls = minToolCalls < 1 ? 1 : minToolCalls,
            LookbackHours = lookbackHours < 1 ? 1 : lookbackHours,
            MaxSessions = maxSessions < 1 ? 1 : maxSessions
        };
    }
}

/// <summary>
/// The aggregate signals - derived from a lookback window of live session history - that determine
/// whether a periodic skill review should run. Unlike the previous design these are <b>not</b> read
/// from cron metadata (nothing populated them there); they are computed from the session transcripts
/// the gateway already persists during normal operation. See <see cref="FromSessions"/>.
/// </summary>
public sealed record SkillReviewSignals
{
    /// <summary>Total tool calls made across the window.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Whether any skill was loaded during the window (a <c>skills</c>/<c>skill_view</c> call).</summary>
    public bool SkillWasLoaded { get; init; }

    /// <summary>Whether the user corrected the agent or expressed frustration. Not cheaply derivable
    /// from a transcript, so it defaults to <c>false</c> in the derived path and is retained for
    /// forward-compatibility and unit-level trigger tests.</summary>
    public bool UserCorrectedOrFrustrated { get; init; }

    /// <summary>Whether a reusable workflow or non-trivial procedure was discovered. Not cheaply
    /// derivable from a transcript; defaults to <c>false</c> in the derived path.</summary>
    public bool DiscoveredReusableWorkflow { get; init; }

    /// <summary>Whether a <c>skill_manage</c> operation failed during the window.</summary>
    public bool SkillManageFailed { get; init; }

    /// <summary>Whether a loaded skill was found to be stale or incorrect. Not cheaply derivable
    /// from a transcript; defaults to <c>false</c> in the derived path.</summary>
    public bool LoadedSkillFoundStale { get; init; }

    /// <summary>Number of distinct sessions that contributed to the window.</summary>
    public int SessionCount { get; init; }

    /// <summary>Tool names that were used across the window, for the reviewer summary.</summary>
    public IReadOnlyList<string> SkillManageFailures { get; init; } = [];

    /// <summary>The tools whose loads/inspection indicate skill activity worth reviewing.</summary>
    private static readonly HashSet<string> SkillLoadTools =
        new(StringComparer.OrdinalIgnoreCase) { "skills", "skills_list", "skill_view" };

    private const string SkillManageTool = "skill_manage";

    /// <summary>
    /// Derives the aggregate review signals from a lookback window of sessions. Only entries at or
    /// after <paramref name="cutoff"/> are counted. A tool call is any <see cref="MessageRole.Tool"/>
    /// entry that carries a <c>ToolName</c> (the ToolStart record); a failed <c>skill_manage</c> is a
    /// tool entry with <c>ToolIsError</c> set.
    /// </summary>
    public static SkillReviewSignals FromSessions(
        IEnumerable<GatewaySession> sessions, DateTimeOffset cutoff)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var toolCalls = 0;
        var skillLoaded = false;
        var skillManageFailed = false;
        var sessionCount = 0;
        var failures = new List<string>();

        foreach (var session in sessions)
        {
            var contributed = false;
            foreach (var entry in session.GetHistorySnapshot())
            {
                if (entry.Timestamp < cutoff)
                    continue;
                if (!Equals(entry.Role, MessageRole.Tool) || string.IsNullOrWhiteSpace(entry.ToolName))
                    continue;

                // Only count ToolStart records (which carry args) so a request/result pair
                // is not double-counted.
                if (entry.ToolArgs is null && entry.ToolCallId is null)
                    continue;

                contributed = true;
                toolCalls++;

                if (SkillLoadTools.Contains(entry.ToolName))
                    skillLoaded = true;

                if (string.Equals(entry.ToolName, SkillManageTool, StringComparison.OrdinalIgnoreCase)
                    && entry.ToolIsError)
                {
                    skillManageFailed = true;
                    failures.Add(session.SessionId.Value);
                }
            }

            if (contributed)
                sessionCount++;
        }

        return new SkillReviewSignals
        {
            ToolCallCount = toolCalls,
            SkillWasLoaded = skillLoaded,
            SkillManageFailed = skillManageFailed,
            SessionCount = sessionCount,
            SkillManageFailures = failures
        };
    }
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
