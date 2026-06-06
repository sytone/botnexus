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
    public IReadOnlyList<object?>? Entries { get; init; }
}
