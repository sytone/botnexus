namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Request to persist a memory entry. The provider determines storage format and indexing.
/// </summary>
/// <param name="AgentId">The agent this memory belongs to.</param>
/// <param name="Content">The content to persist.</param>
/// <param name="SourceType">
/// Origin of the memory entry: "conversation", "manual", "compaction", "dreaming", "tool".
/// </param>
/// <param name="SessionId">Optional session that produced this memory.</param>
/// <param name="TurnIndex">Optional turn index within the session.</param>
/// <param name="Tags">Optional tags for categorisation and filtering.</param>
/// <param name="ExpiresAt">Optional expiry time after which the entry may be purged.</param>
public sealed record AgentMemorySaveRequest(
    string AgentId,
    string Content,
    string SourceType,
    string? SessionId = null,
    int? TurnIndex = null,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? ExpiresAt = null);
