using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>Bounded public projection shared by live tools, REST APIs, and persisted history.</summary>
public record SubAgentRunDetail
{
    public const int MaxShortTextLength = 256;
    public const int MaxLongTextLength = 2048;
    public const int MaxCollectionCount = 32;

    public required string SubAgentId { get; init; }
    public string? SpawningToolCallId { get; init; }
    public string? ParentSessionId { get; init; }
    public string? ChildSessionId { get; init; }
    public string? ParentConversationId { get; init; }
    public string? ChildConversationId { get; init; }
    public string? ParentAgentId { get; init; }
    public string? ChildAgentId { get; init; }
    public string? Name { get; init; }
    public string? Task { get; init; }
    public string? Archetype { get; init; }
    public string? Model { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<SubAgentStatus>))]
    public required SubAgentStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? ElapsedSeconds { get; init; }
    public int? EffectiveMaxTurns { get; init; }
    public int? EffectiveTimeoutSeconds { get; init; }
    public int? RemainingTurns { get; init; }
    public double? RemainingTimeSeconds { get; init; }
    public int? TurnsUsed { get; init; }
    public string? ResultSummary { get; init; }
    public SubAgentRunResult? Result { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<SubAgentCompletionDelivery>))]
    public SubAgentCompletionDelivery? CompletionDelivery { get; init; }
    public string? CompletionDeliveryError { get; init; }
    public string? DeliveryWarning { get; init; }
    public SubAgentWorktreeReference? WorktreeSnapshot { get; init; }

    /// <summary>Creates the safe external projection at a caller-supplied observation time.</summary>
    public static SubAgentRunDetail FromLive(SubAgentInfo info, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var terminal = SubAgentStatusPolicy.IsTerminal(info.Status);
        var elapsed = info.StartedAt == default ? (double?)null : Math.Max(0, ((info.CompletedAt ?? now) - info.StartedAt).TotalSeconds);
        int? remainingTurns = !terminal && info.EffectiveMaxTurns.HasValue
            ? Math.Max(0, info.EffectiveMaxTurns.Value - info.TurnsUsed) : null;
        double? remainingTime = !terminal && info.EffectiveTimeoutSeconds.HasValue && elapsed.HasValue
            ? Math.Max(0, info.EffectiveTimeoutSeconds.Value - elapsed.Value) : null;
        return new SubAgentRunDetail
        {
            SubAgentId = Bound(info.SubAgentId, MaxShortTextLength)!,
            SpawningToolCallId = info.SpawningToolCallId,
            ParentSessionId = info.ParentSessionId.Value, ChildSessionId = info.ChildSessionId.Value,
            ParentConversationId = info.ParentConversationId?.Value, ChildConversationId = info.ChildConversationId?.Value,
            ParentAgentId = Bound(info.ParentAgentId, MaxShortTextLength), ChildAgentId = Bound(info.ChildAgentId, MaxShortTextLength),
            Name = Bound(info.Name, MaxShortTextLength), Task = Bound(info.Task, MaxLongTextLength),
            Archetype = info.Archetype.ToString(), Model = Bound(info.Model, MaxShortTextLength), Status = info.Status,
            StartedAt = info.StartedAt == default ? null : info.StartedAt, CompletedAt = info.CompletedAt, ElapsedSeconds = elapsed,
            EffectiveMaxTurns = info.EffectiveMaxTurns, EffectiveTimeoutSeconds = info.EffectiveTimeoutSeconds,
            RemainingTurns = remainingTurns, RemainingTimeSeconds = remainingTime, TurnsUsed = info.TurnsUsed,
            ResultSummary = Bound(info.ResultSummary, MaxLongTextLength), Result = SubAgentRunResult.From(info.PartialResult),
            CompletionDelivery = info.CompletionDelivery, CompletionDeliveryError = Bound(info.CompletionDeliveryError, MaxLongTextLength),
            DeliveryWarning = info.CompletionDelivery == SubAgentCompletionDelivery.Failed
                ? "This sub-agent finished but its completion announcement never reached this session. Treat this record as the retained result; no wake-up is coming."
                : null,
            WorktreeSnapshot = SubAgentWorktreeReference.From(info.WorktreeSnapshot)
        };
    }

    internal static string? Bound(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}

/// <summary>Bounded structured terminal result without raw tool arguments, reasoning, or results.</summary>
public sealed record SubAgentRunResult
{
    [JsonConverter(typeof(JsonStringEnumConverter<SubAgentCompletion>))] public required SubAgentCompletion Completion { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<SubAgentStopReason>))] public required SubAgentStopReason StopReason { get; init; }
    public string? Summary { get; init; }
    public int? TurnsUsed { get; init; }
    public AgentResponseUsage? Usage { get; init; }
    public IReadOnlyList<string> VerifiedTools { get; init; } = [];
    public IReadOnlyList<string> UnresolvedWork { get; init; } = [];
    internal static SubAgentRunResult? From(SubAgentPartialResult? value) => value is null ? null : new()
    {
        Completion = value.Completion, StopReason = value.StopReason, Summary = SubAgentRunDetail.Bound(value.Summary, SubAgentRunDetail.MaxLongTextLength),
        TurnsUsed = value.TurnsUsed, Usage = value.Usage,
        VerifiedTools = BoundVerifiedTools(value.VerifiedEvidence.Select(x => x.ToolName).Concat(value.RetainedVerifiedTools)),
        UnresolvedWork = value.UnresolvedWork.Select(x => SubAgentRunDetail.Bound(x, SubAgentRunDetail.MaxShortTextLength)!).Where(x => x is not null && !Path.IsPathRooted(x)).Take(SubAgentRunDetail.MaxCollectionCount).ToArray()
    };

    /// <summary>Normalizes retained tool classifications using the same bounds as live evidence.</summary>
    public static IReadOnlyList<string> BoundVerifiedTools(IEnumerable<string> names)
        => names.Select(x => SubAgentRunDetail.Bound(x, SubAgentRunDetail.MaxShortTextLength))
            .OfType<string>().Distinct().Take(SubAgentRunDetail.MaxCollectionCount).ToArray();
}

/// <summary>Safe references to bounded worktree recovery artifacts; host-private roots are omitted.</summary>
public sealed record SubAgentWorktreeReference
{
    [JsonConverter(typeof(JsonStringEnumConverter<SubAgentWorktreeSnapshotOutcome>))] public required SubAgentWorktreeSnapshotOutcome Outcome { get; init; }
    public string? WorktreePath { get; init; }
    public string? ArtifactReference { get; init; }
    public int? PatchBytes { get; init; }
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    public bool PathsTruncated { get; init; }
    internal static SubAgentWorktreeReference? From(SubAgentWorktreeSnapshot? value) => value is null ? null : new()
    {
        Outcome = value.Outcome, WorktreePath = null,
        ArtifactReference = SafeRelative(value.ArtifactPath), PatchBytes = value.PatchBytes > 0 ? value.PatchBytes : null,
        ChangedFiles = value.UntrackedPaths.Select(SafeRelative).Where(x => x is not null).Take(SubAgentRunDetail.MaxCollectionCount).Cast<string>().ToArray(),
        PathsTruncated = value.UntrackedPathsTruncated || value.UntrackedPaths.Count > SubAgentRunDetail.MaxCollectionCount
    };
    private static string? SafeRelative(string? value) => string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal) ? null : SubAgentRunDetail.Bound(value, SubAgentRunDetail.MaxShortTextLength);
}
