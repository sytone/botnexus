using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>Identity and owner projected for a session carrying an unresolved crash sentinel.</summary>
public sealed record UnresolvedCrashSentinelRow(
    SessionId SessionId,
    AgentId AgentId);

/// <summary>A bounded, transcript-free page of sessions carrying unresolved crash sentinels.</summary>
public sealed record UnresolvedCrashSentinelPage(
    IReadOnlyList<UnresolvedCrashSentinelRow> Rows,
    string? NextCursor);

/// <summary>Transcript-free row used to plan one cleanup iteration.</summary>
public sealed record SessionCleanupPlanRow(
    SessionId SessionId,
    AgentId AgentId,
    ConversationId ConversationId,
    SessionStatus Status,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    long Bytes);

/// <summary>A bounded page of transcript-free cleanup planning rows.</summary>
public sealed record SessionCleanupPlanPage(
    IReadOnlyList<SessionCleanupPlanRow> Rows,
    string? NextCursor);

/// <summary>Identity and version fence for cleanup mutations derived from a planning row.</summary>
public readonly record struct SessionCleanupFence(
    SessionId SessionId,
    ConversationId ConversationId,
    SessionStatus ExpectedStatus,
    DateTimeOffset ExpectedUpdatedAt)
{
    /// <summary>Captures the exact projected row cleanup is allowed to mutate.</summary>
    public static SessionCleanupFence Capture(SessionCleanupPlanRow row) =>
        new(row.SessionId, row.ConversationId, row.Status, row.UpdatedAt);
}
