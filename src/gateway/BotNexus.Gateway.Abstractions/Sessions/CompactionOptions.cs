namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Configuration for session compaction behavior.
/// </summary>
public sealed record CompactionOptions
{
    /// <summary>Number of most recent user turns to preserve verbatim (default: 3).</summary>
    public int PreservedTurns { get; init; } = 3;

    /// <summary>Maximum characters for the compaction summary (default: 16000).</summary>
    public int MaxSummaryChars { get; init; } = 16_000;

    /// <summary>
    /// Token threshold as a fraction of context window (0.0–1.0) at which auto-compaction triggers (default: 0.6).
    /// </summary>
    public double TokenThresholdRatio { get; init; } = 0.6;

    /// <summary>Approximate context window size in tokens for the model (default: 128000).</summary>
    public int ContextWindowTokens { get; init; } = 128_000;

    /// <summary>Model to use for summarization. If null, uses the session's model.</summary>
    public string? SummarizationModel { get; init; }
}
