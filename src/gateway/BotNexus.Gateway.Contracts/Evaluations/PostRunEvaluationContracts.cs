using System.Collections.Immutable;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Evaluations;

/// <summary>Semantic contract version implemented by an evaluator.</summary>
public readonly record struct PostRunEvaluatorVersion
{
    public PostRunEvaluatorVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }
    public int Minor { get; }

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>Stable evaluator identity and contract version.</summary>
public sealed record PostRunEvaluatorDescriptor(
    PostRunEvaluatorId Id,
    PostRunEvaluatorVersion Version);

/// <summary>Terminal status reported by a post-run evaluator.</summary>
public enum PostRunEvaluationStatus
{
    Passed,
    Failed,
    NotApplicable,
    Error,
    TimedOut,
    Cancelled
}

/// <summary>A bounded evaluator result. Coordinator policy bounds detail before retaining it.</summary>
public sealed record PostRunEvaluationResult(PostRunEvaluationStatus Status, string? Detail = null)
{
    public static PostRunEvaluationResult Passed(string? detail = null) =>
        new(PostRunEvaluationStatus.Passed, detail);

    public static PostRunEvaluationResult Failed(string? detail = null) =>
        new(PostRunEvaluationStatus.Failed, detail);

    public static PostRunEvaluationResult NotApplicable(string? detail = null) =>
        new(PostRunEvaluationStatus.NotApplicable, detail);
}

/// <summary>Backend-neutral evaluator extension point. Evaluators receive evidence only, never tools.</summary>
public interface IPostRunEvaluator
{
    PostRunEvaluatorDescriptor Descriptor { get; }

    ValueTask<PostRunEvaluationResult> EvaluateAsync(
        RunOutcomeSnapshot snapshot,
        CancellationToken cancellationToken);
}

/// <summary>Immediate admission decision from the best-effort in-memory coordinator.</summary>
public enum PostRunEvaluationAdmission
{
    Accepted,
    Duplicate,
    Saturated,
    Disabled
}

/// <summary>Non-blocking admission seam for settled run snapshots.</summary>
public interface IPostRunEvaluationCoordinator
{
    PostRunEvaluationAdmission Enqueue(RunOutcomeSnapshot snapshot);
}

/// <summary>Aggregate run usage exposed to post-run evaluators.</summary>
public sealed record RunOutcomeUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    int? TurnCount);

/// <summary>Immutable, bounded projection of one tool invocation.</summary>
public sealed record RunOutcomeTool(
    string ToolCallId,
    string ToolName,
    string? Arguments,
    string? ResultContent,
    bool IsError,
    bool IsIncomplete);

/// <summary>Immutable projection of the authoritative run completion signal.</summary>
public sealed record RunOutcomeCompletion(
    string Status,
    ImmutableArray<string> OpenItemIds,
    string? StopReason,
    string? Detail,
    string? Evidence,
    string? ContinuationOwner,
    string? WakeCondition,
    int ContinuationAttempts);

/// <summary>Byte and collection bounds applied while constructing a run snapshot.</summary>
public sealed record RunOutcomeSnapshotLimits(
    int MaxAssistantContentBytes = 32 * 1024,
    int MaxCompletionDetailBytes = 4 * 1024,
    int MaxToolCount = 128,
    int MaxToolFieldBytes = 16 * 1024)
{
    public static RunOutcomeSnapshotLimits Default { get; } = new();
}

/// <summary>
/// Versioned, immutable and bounded evidence captured after an ordinary run's authoritative transcript
/// finalizer persists. This is deliberately an in-memory foundation; snapshots admitted to the coordinator
/// are lost on process restart.
/// </summary>
public sealed record RunOutcomeSnapshot
{
    public const int CurrentSchemaVersion = 1;

    private RunOutcomeSnapshot()
    {
    }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required RunId RunId { get; init; }
    public required SessionId SessionId { get; init; }
    public required ConversationId ConversationId { get; init; }
    public required AgentId AgentId { get; init; }
    public DateTimeOffset SettledAt { get; init; }
    public string AssistantContent { get; private init; } = string.Empty;
    public RunOutcomeCompletion? Completion { get; private init; }
    public RunOutcomeUsage? Usage { get; private init; }
    public ImmutableArray<RunOutcomeTool> Tools { get; private init; } = ImmutableArray<RunOutcomeTool>.Empty;

    public static RunOutcomeSnapshot Create(
        RunId runId,
        SessionId sessionId,
        ConversationId conversationId,
        AgentId agentId,
        DateTimeOffset settledAt,
        string assistantContent,
        RunCompletionSignal? completion,
        RunOutcomeUsage? usage,
        IEnumerable<RunOutcomeTool> tools,
        RunOutcomeSnapshotLimits? limits = null)
    {
        limits ??= RunOutcomeSnapshotLimits.Default;
        ValidateLimits(limits);

        return new RunOutcomeSnapshot
        {
            RunId = runId,
            SessionId = sessionId,
            ConversationId = conversationId,
            AgentId = agentId,
            SettledAt = settledAt,
            AssistantContent = Bound(assistantContent, limits.MaxAssistantContentBytes) ?? string.Empty,
            Completion = completion is null ? null : new RunOutcomeCompletion(
                Bound(completion.Status, limits.MaxCompletionDetailBytes) ?? string.Empty,
                completion.OpenItemIds
                    .Take(128)
                    .Select(value => Bound(value, limits.MaxCompletionDetailBytes) ?? string.Empty)
                    .ToImmutableArray(),
                Bound(completion.StopReason, limits.MaxCompletionDetailBytes),
                Bound(completion.Detail, limits.MaxCompletionDetailBytes),
                Bound(completion.Evidence, limits.MaxCompletionDetailBytes),
                Bound(completion.ContinuationOwner, limits.MaxCompletionDetailBytes),
                Bound(completion.WakeCondition, limits.MaxCompletionDetailBytes),
                completion.ContinuationAttempts),
            Usage = usage,
            Tools = tools.Take(limits.MaxToolCount).Select(tool => new RunOutcomeTool(
                Bound(tool.ToolCallId, limits.MaxToolFieldBytes) ?? string.Empty,
                Bound(tool.ToolName, limits.MaxToolFieldBytes) ?? string.Empty,
                Bound(tool.Arguments, limits.MaxToolFieldBytes),
                Bound(tool.ResultContent, limits.MaxToolFieldBytes),
                tool.IsError,
                tool.IsIncomplete)).ToImmutableArray()
        };
    }

    public static RunOutcomeSnapshot FromStreamingResult(
        RunId runId,
        SessionId sessionId,
        ConversationId conversationId,
        AgentId agentId,
        DateTimeOffset settledAt,
        string assistantContent,
        RunCompletionSignal? completion,
        AgentResponseUsage? usage,
        int turnCount,
        IEnumerable<RunOutcomeTool> tools,
        RunOutcomeSnapshotLimits? limits = null)
    {
        return Create(
            runId,
            sessionId,
            conversationId,
            agentId,
            settledAt,
            assistantContent,
            completion,
            usage is null && turnCount == 0
                ? null
                : new RunOutcomeUsage(
                    usage?.InputTokens,
                    usage?.OutputTokens,
                    usage?.CacheRead,
                    usage?.CacheWrite,
                    turnCount),
            tools,
            limits);
    }

    public static RunOutcomeSnapshot FromAgentResponse(
        RunId runId,
        SessionId sessionId,
        ConversationId conversationId,
        AgentId agentId,
        DateTimeOffset settledAt,
        AgentResponse response,
        RunOutcomeSnapshotLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Create(
            runId,
            sessionId,
            conversationId,
            agentId,
            settledAt,
            response.Content,
            response.Completion,
            response.RunUsage is { } usage
                ? new RunOutcomeUsage(
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheRead,
                    usage.CacheWrite,
                    response.TurnCount)
                : response.TurnCount is { } turns
                    ? new RunOutcomeUsage(null, null, null, null, turns)
                    : null,
            response.ToolCalls.Select(tool => new RunOutcomeTool(
                tool.ToolCallId,
                tool.ToolName,
                tool.Arguments,
                tool.ResultContent,
                tool.IsError,
                tool.IsIncomplete)),
            limits);
    }

    private static void ValidateLimits(RunOutcomeSnapshotLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxAssistantContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxCompletionDetailBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxToolCount);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxToolFieldBytes);
    }

    private static string? Bound(string? value, int maxBytes)
    {
        if (value is null || maxBytes <= 0)
            return value is null ? null : string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
                break;
            builder.Append(rune);
            used += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}
