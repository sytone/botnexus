namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Debug snapshot returned by GET /api/sessions/{sessionId}/debug.
/// </summary>
public sealed class SessionDebugSnapshotDto
{
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? Status { get; init; }
    public string? SessionType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int MessageCount { get; init; }
    public string? ConversationId { get; init; }
    public string? ChannelType { get; init; }
    public string? SystemPrompt { get; init; }
    public DateTimeOffset? SystemPromptCapturedAt { get; init; }
    public SessionDebugHistoryDto? History { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Paginated history block within a <see cref="SessionDebugSnapshotDto"/>.
/// </summary>
public sealed class SessionDebugHistoryDto
{
    public int TotalCount { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<SessionDebugEntryDto>? Entries { get; init; }
}

/// <summary>
/// A single session history entry returned in the debug snapshot.
/// </summary>
public sealed class SessionDebugEntryDto
{
    /// <summary>Message role: user, assistant, system, tool, etc.</summary>
    public string? Role { get; init; }
    /// <summary>Text content of the entry.</summary>
    public string? Content { get; init; }
    /// <summary>Timestamp when this entry was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>Tool name for tool-call/result entries.</summary>
    public string? ToolName { get; init; }
    /// <summary>True if this entry is a compaction summary boundary.</summary>
    public bool IsCompactionSummary { get; init; }
    /// <summary>True if this entry is a crash sentinel.</summary>
    public bool IsCrashSentinel { get; init; }
    /// <summary>True if this entry is folded into a newer compaction summary.</summary>
    public bool IsHistory { get; init; }
}
