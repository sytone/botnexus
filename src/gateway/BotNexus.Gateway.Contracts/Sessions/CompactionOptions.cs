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

    /// <summary>
    /// Per-entry size (in UTF-8 bytes) at or above which a single visible history entry makes the
    /// session eligible for compaction, independently of the token-count threshold (#1599 — bloat-aware
    /// trigger). A session can accumulate a small number of enormous low-value entries (e.g. a raw
    /// transcript dump or a directory listing) whose total still sits under <see cref="TokenThresholdRatio"/>
    /// while the visible tail is dominated by dead weight. This signal is <b>additive</b>: whichever of the
    /// token-count threshold or this per-entry byte threshold trips first triggers compaction. Only
    /// LLM-visible entries are considered (historical / already-summarised entries are excluded, exactly
    /// like the token trigger). Default: 65536 (64 KiB). Values &lt;= 0 disable the byte-based trigger,
    /// restoring the pre-#1599 token-count-only behaviour.
    /// </summary>
    public int LargestEntryBytesThreshold { get; init; } = 65_536;

    /// <summary>Model to use for summarization. If null, uses the session's model.</summary>
    public string? SummarizationModel { get; init; }

    /// <summary>Provider to use for summarization (e.g., "github-copilot"). If null, auto-detected from registered providers.</summary>
    public string? SummarizationProvider { get; init; }

    /// <summary>
    /// Maximum seconds to wait for the LLM summarization call to complete before
    /// aborting compaction (default: 90). Prevents hung provider calls from blocking
    /// the session indefinitely. The timeout is enforced via a linked CancellationToken
    /// that cancels the wait on the provider response.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// How long (seconds) the per-session compaction circuit breaker stays open after
    /// <c>MaxConsecutiveFailures</c> consecutive failures, before it auto-resets and compaction is
    /// attempted again (default: 600 = 10 minutes). The previous behaviour kept the breaker open
    /// until the gateway restarted, which let a transient provider outage permanently wedge a
    /// session (it could no longer shed context). A bounded cooldown lets the session recover on
    /// its own once the provider issue clears. Values &lt;= 0 fall back to the default.
    /// </summary>
    public int CircuitBreakerCooldownSeconds { get; init; } = 600;

    /// <summary>Pre-compaction memory flush configuration.</summary>
    public MemoryFlushOptions MemoryFlush { get; init; } = new();
}

/// <summary>
/// Configuration for memory flush turns.
/// Used for both pre-compaction flush (Phase 1) and session-end flush (Phase 2).
/// When enabled, the agent is given a brief turn to write important context to
/// memory files (e.g. <c>memory/YYYY-MM-DD.md</c>) before the session history
/// is summarised or discarded.
/// </summary>
public sealed record MemoryFlushOptions
{
    /// <summary>Whether memory flush is enabled (default: true).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The prompt sent to the agent during the pre-compaction flush turn.
    /// </summary>
    public string PromptText { get; init; } =
        "Session compaction is about to run. " +
        "Write any important context, decisions, or open items from this conversation to your daily memory file " +
        "(memory/YYYY-MM-DD.md) now. Keep it brief and focused on what must survive compaction.";

    /// <summary>
    /// The prompt sent to the agent during the session-end flush turn (on /reset or explicit session close).
    /// </summary>
    public string SessionEndPromptText { get; init; } =
        "This session is ending. " +
        "Write any important context, decisions, or open items from this conversation to your daily memory file " +
        "(memory/YYYY-MM-DD.md) now. Keep it brief and focused on what should persist.";

    /// <summary>Maximum seconds to wait for the flush turn to complete (default: 60).</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Metadata key used to track the compaction-cycle count at last flush.</summary>
    public const string MetadataKey = "memoryFlushCompactionCount";
}
