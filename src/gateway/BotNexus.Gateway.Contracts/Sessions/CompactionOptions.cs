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
    /// Token threshold as a fraction of context window (0.0-1.0) at which auto-compaction triggers (default: 0.6).
    /// </summary>
    public double TokenThresholdRatio { get; init; } = 0.6;

    /// <summary>Approximate context window size in tokens for the model (default: 128000).</summary>
    public int ContextWindowTokens { get; init; } = 128_000;

    /// <summary>Model to use for summarization. If null, uses the session's model.</summary>
    public string? SummarizationModel { get; init; }

    /// <summary>Provider to use for summarization (e.g., "github-copilot"). If null, auto-detected from registered providers.</summary>
    public string? SummarizationProvider { get; init; }

    /// <summary>Pre-compaction memory flush configuration.</summary>
    public MemoryFlushOptions MemoryFlush { get; init; } = new();
}

/// <summary>
/// Configuration for the pre-compaction memory flush turn.
/// When enabled, the agent is given a brief turn to write important context to
/// memory files (e.g. <c>memory/YYYY-MM-DD.md</c>) before the session history
/// is summarised and truncated.
/// </summary>
public sealed record MemoryFlushOptions
{
    /// <summary>Whether pre-compaction memory flush is enabled (default: true).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The prompt sent to the agent during the flush turn.
    /// The agent should use this turn to write important context to memory files.
    /// </summary>
    public string PromptText { get; init; } =
        "Session compaction is about to run. " +
        "Write any important context, decisions, or open items from this conversation to your daily memory file " +
        "(memory/YYYY-MM-DD.md) now. Keep it brief and focused on what must survive compaction.";

    /// <summary>Maximum seconds to wait for the flush turn to complete (default: 60).</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Metadata key used to track the compaction-cycle count at last flush.</summary>
    public const string MetadataKey = "memoryFlushCompactionCount";
}
