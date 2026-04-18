namespace BotNexus.Memory.Models;

public sealed record MemoryEntry
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public string? SessionId { get; init; }
    public int? TurnIndex { get; init; }
    public required string SourceType { get; init; } // conversation, manual, compaction, dreaming
    public required string Content { get; init; }
    public string? MetadataJson { get; init; }
    public byte[]? Embedding { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsArchived { get; init; }
}
