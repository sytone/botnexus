using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>Chooses the authority for compaction policy independently of the model's real capacity.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CompactionPolicySource>))]
public enum CompactionPolicySource
{
    /// <summary>Use the operator's configured policy; conversation overrides cannot replace it.</summary>
    Configured,
    /// <summary>Opt into conversation, agent and world inheritance, in that order.</summary>
    Conversation,
    /// <summary>Use the active model's ratio threshold without any configured token ceiling.</summary>
    Model
}

/// <summary>
/// Configuration for session compaction behavior.
/// </summary>
public sealed record CompactionOptions
{
    /// <summary>Policy authority. Explicit operator configuration wins unless inheritance is selected.</summary>
    [EnumDataType(typeof(CompactionPolicySource))]
    [Display(Name = "Compaction policy source",
        Description = "Configured (default): your gateway policy wins; agent/world ceilings supply defaults when no gateway ceiling is set. Conversation: explicitly inherit conversation, agent, then world settings. Model: use the active model's ratio threshold. Model capacity always remains a safety limit.",
        GroupName = "Compaction", Order = -1)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "compaction", Order = -1)]
    public CompactionPolicySource PolicySource { get; init; } = CompactionPolicySource.Configured;

    /// <summary>The configured policy window before runtime model capacity is resolved; never persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? ConfiguredWindowTokens { get; private init; }

    internal CompactionOptions WithResolvedWindow(int window) => this with
    {
        ContextWindowTokens = window,
        ConfiguredWindowTokens = ConfiguredWindowTokens ?? ContextWindowTokens
    };

    /// <summary>
    /// Runtime-only protection for the active request. Only an explicit manual compaction may
    /// summarize it; changing a retention preference never changes this operation boundary.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PreserveLatestRequest { get; private init; } = true;

    /// <summary>
    /// Creates the policy for an explicit compact-now operation after the current work has stopped.
    /// This operation state cannot be selected through persisted configuration.
    /// </summary>
    public CompactionOptions ForManualCompaction()
        => this with { PreservedTurns = 0, PreserveLatestRequest = false };

    /// <summary>Preferred recent user turns, subject to the token budget; zero retains only mandatory active context.</summary>
    [Range(0, int.MaxValue)]
    [Display(
        Name = "Preserved turns",
        Description = "Preferred recent user turns within the preserved-tail token budget (default: 3). Zero summarizes completed work but still protects the active request. Use the compact-now action for an explicit manual handoff.",
        GroupName = "Compaction",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 0)]
    public int PreservedTurns { get; init; } = 3;

    /// <summary>
    /// Estimated token budget for verbatim retained entries, excluding the new summary and system
    /// overhead (default: 12000). The latest request and unfinished tool groups are mandatory;
    /// compaction fails without modifying history when these alone exceed the positive budget.
    /// </summary>
    [Range(1, int.MaxValue)]
    [Display(
        Name = "Preserved tail tokens",
        Description = "Positive estimated token budget for recent verbatim context (default: 12000), excluding the summary and system overhead. Mandatory context exceeding it aborts compaction without changing history.",
        GroupName = "Compaction",
        Order = 12)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 12)]
    public int PreservedTailTokens { get; init; } = 12_000;

    /// <summary>Maximum characters for the compaction summary (default: 16000).</summary>
    [Range(1, int.MaxValue)]
    [Display(
        Name = "Max summary chars",
        Description = "Maximum characters for the compaction summary (default: 16000).",
        GroupName = "Compaction",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 1)]
    public int MaxSummaryChars { get; init; } = 16_000;

    /// <summary>
    /// Token threshold as a fraction of context window (0.0-1.0) at which auto-compaction triggers (default: 0.6).
    /// </summary>
    [Range(0.0, 1.0)]
    [Display(
        Name = "Token threshold ratio",
        Description = "Token threshold as a fraction of context window (0.0-1.0) at which auto-compaction triggers (default: 0.6).",
        GroupName = "Compaction",
        Order = 2)]
    // #3654: this is a numeric tuning knob, NOT a credential. It was previously annotated
    // Secret = true, which made the reflection-driven secret discovery in ConfigSecretMerge treat
    // it as a scalar redaction target: GET /api/config served the string "***" against a schema
    // that declares a number, and the SchemaForm password branch committed a JSON *string* back,
    // producing a config document that would not bind.
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 2)]
    public double TokenThresholdRatio { get; init; } = 0.6;

    /// <summary>Configured working-context policy window and unknown-capacity fallback, not declared model capacity.</summary>
    [Range(1, int.MaxValue)]
    [Display(
        Name = "Context window tokens",
        Description = "Configured working-context policy window (default: 128000), not a declaration of model capacity. Used with the ratio when Configured is selected and no explicit ceiling applies; also the fallback when model capacity is unknown.",
        GroupName = "Compaction",
        Order = 3)]
    // #3654: numeric, not a credential (see TokenThresholdRatio above). Redacting it was also
    // pointless -- the value is printed in cleartext by the /context slash command.
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 3)]
    public int ContextWindowTokens { get; init; } = 128_000;

    /// <summary>
    /// Positive configured trigger, not model capacity. Configured mode gives this value priority;
    /// Conversation opts into scoped inheritance and Model ignores ceilings.
    /// </summary>
    [Display(
        Name = "Compaction token ceiling",
        Description = "Explicit positive compaction trigger in tokens, not model capacity. With Configured (default), this value wins over conversation/agent/world ceilings; null uses agent then world defaults. Conversation opts into scoped inheritance; Model ignores ceilings. The actual model safety threshold still applies.",
        GroupName = "Compaction",
        Order = 11)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 11)]
    [Range(1, int.MaxValue)]
    public int? CompactionTokenCeiling { get; init; }

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
    [Display(
        Name = "Largest entry bytes threshold",
        Description = "Per-entry size (in UTF-8 bytes) at or above which a single visible history entry makes the session eligible for compaction, independently of the token-count threshold (#1599 — bloat-aware trigger). A session can accumulate a small number of enormous low-value entries (e.g. a raw transcript dump or a directory listing) whose total still sits under TokenThresholdRatio while the visible tail is dominated by dead weight. This signal is additive: whichever of the token-count threshold or this per-entry byte threshold trips first triggers compaction. Only LLM-visible entries are considered (historical / already-summarised entries are excluded, exactly like the token trigger). Default: 65536 (64 KiB). Values &lt;= 0 disable the byte-based trigger, restoring the pre-#1599 token-count-only behaviour.",
        GroupName = "Compaction",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 4)]
    public int LargestEntryBytesThreshold { get; init; } = 65_536;

    /// <summary>Model to use for summarization. If null, uses the session's model.</summary>
    [Display(
        Name = "Summarization model",
        Description = "Model to use for summarization. If null, uses the session's model.",
        GroupName = "Compaction",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "compaction", Order = 5)]
    public string? SummarizationModel { get; init; }

    /// <summary>Provider to use for summarization (e.g., "github-copilot"). If null, auto-detected from registered providers.</summary>
    [Display(
        Name = "Summarization provider",
        Description = "Provider to use for summarization (e.g., \"github-copilot\"). If null, auto-detected from registered providers.",
        GroupName = "Compaction",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "compaction", Order = 6)]
    public string? SummarizationProvider { get; init; }

    /// <summary>
    /// Maximum seconds to wait for the LLM summarization call to complete before
    /// aborting compaction (default: 90). Prevents hung provider calls from blocking
    /// the session indefinitely. The timeout is enforced via a linked CancellationToken
    /// that cancels the wait on the provider response.
    /// </summary>
    [Display(
        Name = "Timeout seconds",
        Description = "Maximum seconds to wait for the LLM summarization call to complete before aborting compaction (default: 90). Prevents hung provider calls from blocking the session indefinitely. The timeout is enforced via a linked CancellationToken that cancels the wait on the provider response.",
        GroupName = "Compaction",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 7)]
    public int TimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// How long (seconds) the per-session compaction circuit breaker stays open after
    /// <c>MaxConsecutiveFailures</c> consecutive failures, before it auto-resets and compaction is
    /// attempted again (default: 600 = 10 minutes). The previous behaviour kept the breaker open
    /// until the gateway restarted, which let a transient provider outage permanently wedge a
    /// session (it could no longer shed context). A bounded cooldown lets the session recover on
    /// its own once the provider issue clears. Values &lt;= 0 fall back to the default.
    /// </summary>
    [Display(
        Name = "Circuit breaker cooldown seconds",
        Description = "How long (seconds) the per-session compaction circuit breaker stays open after MaxConsecutiveFailures consecutive failures, before it auto-resets and compaction is attempted again (default: 600 = 10 minutes). The previous behaviour kept the breaker open until the gateway restarted, which let a transient provider outage permanently wedge a session (it could no longer shed context). A bounded cooldown lets the session recover on its own once the provider issue clears. Values &lt;= 0 fall back to the default.",
        GroupName = "Compaction",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 8)]
    public int CircuitBreakerCooldownSeconds { get; init; } = 600;

    /// <summary>
    /// Stream-setup idle cap (milliseconds) applied to the compaction summarization model call when
    /// the resolved candidate is a CLOUD provider (default: 60000 = 60s, mirroring OpenClaw's
    /// CRON_LLM_IDLE_TIMEOUT_MS). This wires the otherwise-inert
    /// <c>StreamOptions.StreamSetupTimeoutMs</c> first-token watchdog so a cloud model that stalls
    /// mid-stream (or never emits a first token) is aborted early. Compaction is a background
    /// (non-interactive) call that already has an outer per-attempt watchdog
    /// (<see cref="TimeoutSeconds"/>); the stream stall must fail well INSIDE that window so the
    /// model fallback chain still has time to try the next candidate rather than the whole attempt
    /// timing out. The cap is intentionally NOT applied to LOCAL/self-hosted providers (localhost /
    /// 127.0.0.1 - e.g. ollama, vllm, lmstudio, sglang) because those endpoints are legitimately slow
    /// to warm up; the cloud-vs-local decision is made via
    /// <c>ProviderEndpointClassifier.IsLocalProviderBaseUrl</c> from the resolved model BaseUrl.
    /// Values &lt;= 0 disable the cap entirely (restores the pre-#1652 behaviour where no setup-phase
    /// timeout is enforced).
    /// </summary>
    [Display(
        Name = "Cron llm idle timeout ms",
        Description = "Stream-setup idle cap (milliseconds) applied to the compaction summarization model call when the resolved candidate is a CLOUD provider (default: 60000 = 60s, mirroring OpenClaw's CRON_LLM_IDLE_TIMEOUT_MS). This wires the otherwise-inert StreamOptions.StreamSetupTimeoutMs first-token watchdog so a cloud model that stalls mid-stream (or never emits a first token) is aborted early. Compaction is a background (non-interactive) call that already has an outer per-attempt watchdog (TimeoutSeconds); the stream stall must fail well INSIDE that window so the model fallback chain still has time to try the next candidate rather than the whole attempt timing out. The cap is intentionally NOT applied to LOCAL/self-hosted providers (localhost / 127.0.0.1 - e.g. ollama, vllm, lmstudio, sglang) because those endpoints are legitimately slow to warm up; the cloud-vs-local decision is made via ProviderEndpointClassifier.IsLocalProviderBaseUrl from the resolved model BaseUrl. Values &lt;= 0 disable the cap entirely (restores the pre-#1652 behaviour where no setup-phase timeout is enforced).",
        GroupName = "Compaction",
        Order = 9)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "compaction", Order = 9)]
    public int CronLlmIdleTimeoutMs { get; init; } = 60_000;

    /// <summary>Pre-compaction memory flush configuration.</summary>
    [Display(
        Name = "Memory flush",
        Description = "Pre-compaction memory flush configuration.",
        GroupName = "Compaction",
        Order = 10)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "compaction", Order = 10)]
    public MemoryFlushOptions MemoryFlush { get; init; } = new();
}

/// <summary>Pure token trigger policy shared by estimates, provider usage and cut-plan fallback.</summary>
public static class CompactionThreshold
{
    /// <summary>Bounds the selected configured/inherited trigger by the resolved model or working-window safety threshold.</summary>
    public static int Resolve(CompactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CompactionTokenCeiling is <= 0)
            throw new InvalidOperationException("Invalid compaction token ceiling; expected a positive integer.");
        var ratioThreshold = (int)Math.Floor(options.ContextWindowTokens * options.TokenThresholdRatio);
        if (options.PolicySource == CompactionPolicySource.Model)
            return ratioThreshold;
        var policyThreshold = options.CompactionTokenCeiling ??
            (options.PolicySource == CompactionPolicySource.Configured && options.ConfiguredWindowTokens is int configured
                ? (int)Math.Floor(configured * options.TokenThresholdRatio)
                : ratioThreshold);
        return Math.Min(ratioThreshold, policyThreshold);
    }
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
    [Display(
        Name = "Enabled",
        Description = "Whether memory flush is enabled (default: true).",
        GroupName = "Memory flush",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "memory-flush", Order = 0)]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The prompt sent to the agent during the pre-compaction flush turn.
    /// </summary>
    [Display(
        Name = "Prompt text",
        Description = "The prompt sent to the agent during the pre-compaction flush turn.",
        GroupName = "Memory flush",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory-flush", Order = 1)]
    public string PromptText { get; init; } =
        "Session compaction is about to run. " +
        "Write any important context, decisions, or open items from this conversation to your daily memory file " +
        "(memory/YYYY-MM-DD.md) now. Keep it brief and focused on what must survive compaction.";

    /// <summary>
    /// The prompt sent to the agent during the session-end flush turn (on /reset or explicit session close).
    /// </summary>
    [Display(
        Name = "Session end prompt text",
        Description = "The prompt sent to the agent during the session-end flush turn (on /reset or explicit session close).",
        GroupName = "Memory flush",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory-flush", Order = 2)]
    public string SessionEndPromptText { get; init; } =
        "This session is ending. " +
        "Write any important context, decisions, or open items from this conversation to your daily memory file " +
        "(memory/YYYY-MM-DD.md) now. Keep it brief and focused on what should persist.";

    /// <summary>Maximum seconds to wait for the flush turn to complete (default: 60).</summary>
    [Display(
        Name = "Timeout seconds",
        Description = "Maximum seconds to wait for the flush turn to complete (default: 60).",
        GroupName = "Memory flush",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "memory-flush", Order = 3)]
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Metadata key used to track the compaction-cycle count at last flush.</summary>
    public const string MetadataKey = "memoryFlushCompactionCount";
}
