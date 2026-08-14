using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Configuration;

/// <summary>
/// Defines the immutable runtime contract for a pi-mono compatible agent loop.
/// </summary>
/// <param name="Model">The model definition used for provider calls.</param>
/// <param name="ConvertToLlm">Converts agent messages to provider chat messages before each LLM call.</param>
/// <param name="TransformContext">Optional context transformer before provider invocation (defaults to identity passthrough).</param>
/// <param name="GetApiKey">Resolves provider API keys on demand (called before each LLM invocation).</param>
/// <param name="GetSteeringMessages">Provides steering messages when configured (drained at turn boundaries).</param>
/// <param name="GetFollowUpMessages">Provides follow-up messages when configured (drained after runs complete).</param>
/// <param name="ToolExecutionMode">Controls tool execution ordering (Sequential or Parallel).</param>
/// <param name="BeforeToolCall">Optional pre-tool-call hook for validation and blocking.</param>
/// <param name="BeforeToolCallTimeout">
/// Wall-clock budget for the <paramref name="BeforeToolCall"/> hook (#2518). The hook is the
/// pre-execution policy gate, so a hook that never returns would otherwise stall the whole turn.
/// When the budget elapses the tool call is <em>blocked</em> (fail closed), never allowed through.
/// Null means the loop default of 15 seconds; set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
/// or a non-positive value to disable the budget (not recommended).
/// </param>
/// <param name="AfterToolCall">Optional post-tool-call hook for result transformation.</param>
/// <param name="GenerationSettings">The generation settings for model calls (temperature, maxTokens, etc.).</param>
/// <param name="MaxRetryDelayMs">
/// Maximum delay in milliseconds for transient retry backoff, and the ceiling applied to a
/// server-supplied <c>Retry-After</c>. Must be greater than zero when set.
/// Defaults to <see cref="AgentLoopConfig.DefaultMaxRetryDelayMs"/> rather than to "uncapped" (#3035):
/// an uncapped ceiling let a single malformed or hostile upstream <c>Retry-After</c> header park a turn
/// for as long as it asked, with no operator-visible bound. A null or non-positive value is treated as
/// "use the default ceiling", so the delay is bounded on every path.
/// </param>
/// <param name="RetryRandomSource">
/// Injectable randomness source in <c>[0,1]</c> for the transient-retry backoff jitter (#3035).
/// Null uses <see cref="BotNexus.Agent.Providers.Core.Resilience.RetryJitter.DefaultRandomSource"/>.
/// The seam exists so the jitter is deterministically testable rather than being untestable
/// non-determinism: pinned to <c>0</c> the loop reproduces the historical 500/1000/2000ms sequence.
/// </param>
/// <param name="SkipInitialSteeringPoll">True to skip the first steering queue drain for this run.</param>
/// <param name="ToolTimeout">Per-tool execution timeout. Null = no timeout (not recommended). Defaults to 120 seconds.</param>
/// <param name="ClaimAudit">
/// Optional post-turn claim-auditor configuration (#1600). When null the auditor does not run.
/// When provided and enabled, the agent's final message is audited for artifact-shaped claims that
/// lack a backing tool call, and a <see cref="BotNexus.Agent.Core.Types.ClaimAuditEvent"/> is emitted on detection.
/// </param>
/// <param name="MaybeCompactAsync">
/// Optional best-effort auto-compaction hook (#1710). When set it is awaited at the top of each
/// outer-loop iteration (after a turn settles, before the next steering drain) so a single long
/// dispatch -- cron or an autonomous follow-up loop -- re-checks the compaction threshold instead
/// of growing the transcript unbounded until provider overflow. Failures are swallowed and the
/// loop continues. Null means no mid-loop re-check (prior behaviour).
/// </param>
/// <param name="OnDiagnostic">
/// Optional non-fatal diagnostic sink. Used to surface hook-budget breaches (#2518) so a slow or
/// wedged policy provider is diagnosable rather than silently stalling the loop.
/// </param>
/// <param name="SuspensionRegistry">
/// Optional provider-exhaustion suspension registry (#3015). When set, a non-transient exhaustion
/// failure (quota exhausted, billing disabled, credential rejected) fails after exactly ONE attempt
/// and records a time-bounded suspension scoped to the model's provider plus
/// <paramref name="AuthProfile"/>. Null means no suspension is recorded; the one-attempt lane still
/// applies, because not spending three pointless round-trips is correct regardless of whether
/// anything is listening.
/// </param>
/// <param name="AuthProfile">
/// Optional auth-profile identifier used with <paramref name="SuspensionRegistry"/> to scope a
/// suspension. Two agents sharing a provider but using different credentials must not cool each
/// other, so this is part of the suspension key rather than an afterthought. Null is normalised to
/// the empty profile.
/// </param>
/// <param name="MaxToolOutputBytes">
/// Shared central UTF-8 byte budget applied to every tool result before it reaches the model
/// (#3162). Null means <see cref="ToolOutputBudget.DefaultMaxBytes"/>; a non-positive value
/// disables the backstop entirely, matching the convention already used by the write-time
/// tool-result cap. This is a backstop <em>beneath</em> the existing per-tool caps, not a
/// replacement for them.
/// </param>
/// <remarks>
/// AgentLoopConfig is built from AgentOptions at the start of each run.
/// It is immutable and passed through the loop to ensure consistent configuration.
/// </remarks>
public record AgentLoopConfig(
    LlmModel Model,
    LlmClient LlmClient,
    ConvertToLlmDelegate ConvertToLlm,
    TransformContextDelegate? TransformContext,
    GetApiKeyDelegate GetApiKey,
    GetMessagesDelegate? GetSteeringMessages,
    GetMessagesDelegate? GetFollowUpMessages,
    ToolExecutionMode ToolExecutionMode,
    BeforeToolCallDelegate? BeforeToolCall,
    AfterToolCallDelegate? AfterToolCall,
    SimpleStreamOptions GenerationSettings,
    int? MaxRetryDelayMs = AgentLoopConfig.DefaultMaxRetryDelayMs,
    bool SkipInitialSteeringPoll = false,
    TimeSpan? ToolTimeout = null,
    ClaimAuditOptions? ClaimAudit = null,
    Func<CancellationToken, Task>? MaybeCompactAsync = null,
    TimeSpan? BeforeToolCallTimeout = null,
    Action<string>? OnDiagnostic = null,
    BotNexus.Agent.Core.Loop.IProviderSuspensionRegistry? SuspensionRegistry = null,
    string? AuthProfile = null,
    Func<double>? RetryRandomSource = null,
    int? MaxToolOutputBytes = null)
{
    /// <summary>
    /// Default wall-clock budget for the <see cref="BeforeToolCall"/> policy hook (#2518).
    /// </summary>
    public static readonly TimeSpan DefaultBeforeToolCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Default ceiling for transient retry backoff and for a server-supplied <c>Retry-After</c> (#3035).
    /// <para>
    /// Sixty seconds is comfortably above the loop's own worst-case backoff (500+1000+2000ms) so it never
    /// truncates the normal schedule, while still bounding the pathological case the previous <c>null</c>
    /// default allowed: a <c>Retry-After</c> of hours honoured verbatim, holding a turn open indefinitely.
    /// </para>
    /// </summary>
    public const int DefaultMaxRetryDelayMs = 60_000;

    /// <summary>
    /// The effective retry-delay ceiling in milliseconds. Null or non-positive is normalised to
    /// <see cref="DefaultMaxRetryDelayMs"/> so callers that explicitly opted into the old "uncapped"
    /// behaviour by passing <c>null</c> are still bounded.
    /// </summary>
    public int EffectiveMaxRetryDelayMs => MaxRetryDelayMs is > 0 ? MaxRetryDelayMs.Value : DefaultMaxRetryDelayMs;

    /// <summary>
    /// The effective central tool-output byte budget (#3162). Null means "use the platform default";
    /// an explicitly configured non-positive value is preserved verbatim, because zero-or-less is the
    /// documented way to DISABLE the backstop and silently re-enabling it would defeat the operator's
    /// choice. Contrast <see cref="EffectiveMaxRetryDelayMs"/>, where non-positive is normalised to
    /// the default because "no retry ceiling" is never a safe outcome.
    /// </summary>
    public int EffectiveMaxToolOutputBytes => MaxToolOutputBytes ?? ToolOutputBudget.DefaultMaxBytes;
}
