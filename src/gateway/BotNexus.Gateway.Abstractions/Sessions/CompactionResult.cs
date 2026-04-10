namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Result of a compaction operation.
/// </summary>
public sealed record CompactionResult
{
    /// <summary>The generated summary text.</summary>
    public required string Summary { get; init; }

    /// <summary>Number of entries that were summarized (removed).</summary>
    public int EntriesSummarized { get; init; }

    /// <summary>Number of entries preserved verbatim.</summary>
    public int EntriesPreserved { get; init; }

    /// <summary>Approximate token count before compaction.</summary>
    public int TokensBefore { get; init; }

    /// <summary>Approximate token count after compaction.</summary>
    public int TokensAfter { get; init; }
}
