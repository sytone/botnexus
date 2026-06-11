namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Optional filter for narrowing memory search results by source type,
/// date range, session, or tags.
/// </summary>
public sealed record AgentMemorySearchFilter
{
    /// <summary>
    /// Filter to entries from a specific source type (e.g. "conversation", "dreaming").
    /// </summary>
    public string? SourceType { get; init; }

    /// <summary>
    /// Filter to entries from a specific session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Only include entries created after this date.
    /// </summary>
    public DateTimeOffset? AfterDate { get; init; }

    /// <summary>
    /// Only include entries created before this date.
    /// </summary>
    public DateTimeOffset? BeforeDate { get; init; }

    /// <summary>
    /// Only include entries that have at least one of these tags.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }
}
