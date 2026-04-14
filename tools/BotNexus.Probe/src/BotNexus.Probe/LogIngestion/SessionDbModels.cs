namespace BotNexus.Probe.LogIngestion;

public sealed record SessionSummary(
    string Id,
    string? AgentId,
    string? ChannelType,
    string? SessionType,
    string? Status,
    string? CallerId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    int MessageCount);

public sealed record SessionDetail(
    string Id,
    string? AgentId,
    string? ChannelType,
    string? SessionType,
    string? Status,
    string? CallerId,
    string? ParticipantsJson,
    string? Metadata,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record SessionHistoryEntry(
    long Id,
    string SessionId,
    string? Role,
    string? Content,
    DateTimeOffset? Timestamp,
    string? ToolName,
    string? ToolCallId,
    bool IsCompactionSummary);

public sealed record SessionCounts(
    int Total,
    int Active,
    int Sealed,
    int Expired,
    int Suspended);
