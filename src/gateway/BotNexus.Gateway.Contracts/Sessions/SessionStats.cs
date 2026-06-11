namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Aggregate session statistics for monitoring and diagnostics.
/// </summary>
public sealed record SessionStats
{
    /// <summary>Total number of sessions in the store.</summary>
    public required int TotalSessions { get; init; }

    /// <summary>Session counts grouped by status.</summary>
    public required IReadOnlyDictionary<string, int> ByStatus { get; init; }

    /// <summary>Session counts grouped by agent ID.</summary>
    public required IReadOnlyList<AgentSessionCount> ByAgent { get; init; }

    /// <summary>Compaction statistics.</summary>
    public required CompactionStats Compaction { get; init; }

    /// <summary>When the stats were generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Session count for a single agent.
/// </summary>
/// <param name="AgentId">Agent identifier.</param>
/// <param name="Count">Number of sessions for this agent.</param>
public sealed record AgentSessionCount(string AgentId, int Count);

/// <summary>
/// Compaction statistics across all sessions.
/// </summary>
/// <param name="CompactedSessions">Sessions that have been compacted at least once.</param>
/// <param name="UncompactedSessions">Sessions that have never been compacted.</param>
public sealed record CompactionStats(int CompactedSessions, int UncompactedSessions);
