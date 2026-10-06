using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.ExtensionPoints.Messages;
using BotNexus.Agent.Core.ExtensionPoints.ProviderExecution;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.ExtensionPoints.ToolExecution;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Configuration;

/// <summary>
/// Defines the immutable runtime contract for a pi-mono compatible agent loop.
/// </summary>
/// <param name="Model">The model definition used for provider calls.</param>
/// <param name="LlmClient">The provider client used to stream model responses during runs.</param>
/// <param name="ProviderMessageTransformer">Converts agent messages to provider chat messages before each LLM call.</param>
/// <param name="AgentContextTransformer">Optional context transformer before provider invocation (defaults to identity passthrough).</param>
/// <param name="ProviderExecutionOptionsProvider">Resolves provider execution policy on demand (called before each LLM invocation).</param>
/// <param name="SteeringMessageProvider">Provides steering messages when configured (drained at turn boundaries).</param>
/// <param name="FollowUpMessageProvider">Provides follow-up messages when configured (drained after runs complete).</param>
/// <param name="ToolExecutionMode">Controls tool execution ordering (Sequential or Parallel).</param>
/// <param name="ToolAuditGate">Optional durable audit gate with blocking authority, invoked before tool-execution policy.</param>
/// <param name="ToolExecutionPolicy">Optional tool-execution policy for validation and blocking.</param>
/// <param name="ToolExecutionPolicyTimeout">
/// Cooperative tool-execution policy budget; null selects 15 seconds. Timeout fails closed,
/// subject to host-suspend adjustment, but cannot forcibly interrupt an uncooperative callback.
/// Set to <see cref="Timeout.InfiniteTimeSpan"/> or a non-positive value to disable.
/// </param>
/// <param name="ToolResultTransformer">Optional tool-result transformer applied after execution.</param>
/// <param name="GenerationSettings">The generation settings for model calls (temperature, maxTokens, etc.).</param>
/// <param name="MaxRetryDelayMs">
/// Maximum delay in milliseconds for transient retry backoff, and the ceiling applied to a
/// server-supplied <c>Retry-After</c>. Must be greater than zero when set.
/// Defaults to <see cref="AgentLoopConfig.DefaultMaxRetryDelayMs"/> rather than to "uncapped" (#3035):
/// an uncapped ceiling let a single malformed or hostile upstream <c>Retry-After</c> header park a turn
/// for as long as it asked, with no operator-visible bound. A null or non-positive value is treated as
/// "use the default ceiling", so the delay is bounded on every path.
/// </param>
/// <param name="RetryRandomnessProvider">
/// Injectable randomness source in <c>[0,1]</c> for the transient-retry backoff jitter (#3035).
/// Null uses <see cref="BotNexus.Agent.Providers.Core.Resilience.RetryJitter.DefaultRandomSource"/>.
/// The seam exists so the jitter is deterministically testable rather than being untestable
/// non-determinism: pinned to <c>0</c> the loop reproduces the historical 500/1000/2000ms sequence.
/// </param>
/// <param name="SkipInitialSteeringPoll">True to skip the first steering queue drain for this run.</param>
/// <param name="ToolTimeout">
/// Loop-level tool-execution timeout; null disables this budget, while tool-declared defaults still apply.
/// Supported caller-requested timeouts can extend a configured budget. AgentOptions supplies 120 seconds when unset.
/// </param>
/// <param name="ClaimAudit">
/// Optional post-turn claim-auditor configuration (#1600). When null the auditor does not run.
/// When provided and enabled, the agent's final message is audited for artifact-shaped claims that
/// lack a backing tool call, and a <see cref="ClaimAuditEvent"/> is emitted on detection.
/// </param>
/// <param name="ContextCompactionService">
/// Optional auto-compaction service. When set it is awaited before every provider
/// turn, after any preceding tool batch and its results have completed, so both inner tool chains and
/// outer follow-up iterations re-check the compaction threshold before growing further. A returned
/// context replaces the loop's live snapshot; a null result retains it. Hosts throw <see cref="ProactiveCompactionException"/>
/// when compaction was required but failed; that typed failure blocks provider invocation. Other
/// non-cancellation exceptions retain the optional best-effort behavior; cancellation propagates.
/// Null means no mid-loop re-check.
/// </param>
/// <param name="DiagnosticObserver">
/// Optional non-fatal diagnostic sink. Used to surface policy-budget breaches so a slow or
/// wedged policy provider is diagnosable rather than silently stalling the loop.
/// </param>
/// <param name="SuspendDetector">
/// Optional active-time clock used to distinguish policy-budget breaches from host suspension.
/// Null uses HostSuspendDetector.Instance.
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
/// <param name="ToolResultTextTransformer">
/// Optional host-owned sanitizer applied to finalized generic tool text after result transformation
/// and before central budgeting and continuation retention (#4096).
/// </param>
/// <param name="ToolExecutionDecisionObserver">
/// Optional observer reporting whether a validated, audited tool call will execute after policy evaluation.
/// Observer exceptions are not swallowed by the executor.
/// </param>
/// <param name="SatelliteToolExecution">
/// Optional satellite dispatch configuration; null executes tools locally. Classified remote-capable
/// tools use its executor, and tools classified as unsupported return an error result.
/// </param>
/// <param name="RunCompletionPolicy">
/// Optional authoritative host evaluation invoked before a normal run end. It may accept completion,
/// park the run with a structured stop disposition, or require another bounded continuation turn.
/// Null preserves ordinary simple-run behavior.
/// </param>
/// <param name="MaxCompletionContinuations">
/// Maximum automatic turns added when <paramref name="RunCompletionPolicy"/> reports actionable
/// work. Exhausting the bound records an incomplete outcome rather than successful completion.
/// </param>
/// <param name="CredentialInvalidationService">
/// Optional host-owned credential invalidation seam. When set, one authentication rejection
/// invalidates credentials, re-resolves provider execution options, and retries exactly once.
/// </param>
/// <param name="RecoveryCoordinator">
/// Optional shared provider-recovery admission coordinator scoped by provider and auth profile.
/// Null disables coordinated admission, not the loop's retry handling.
/// </param>
/// <param name="RecoveryAdmissionTimeout">
/// Maximum wait for coordinated provider admission; null uses the effective retry-delay ceiling.
/// Used only when RecoveryCoordinator is set. Admission failures and cancellation propagate to the run.
/// </param>
/// <param name="ToolProgressPolicy">
/// Optional policy invoked once per retained tool result. Null uses the core default policy.
/// The loop owns run-local aggregation and applies warning or stop decisions. Policy exceptions
/// and cancellation propagate; an unclassified result resets repeated non-progress tracking.
/// </param>
/// <remarks>
/// AgentLoopConfig is built from AgentOptions at the start of each run.
/// It is immutable and passed through the loop to ensure consistent configuration.
/// </remarks>
public record AgentLoopConfig(
    LlmModel Model,
    LlmClient LlmClient,
    ProviderMessageTransformer ProviderMessageTransformer,
    AgentContextTransformer? AgentContextTransformer,
    ProviderExecutionOptionsProvider ProviderExecutionOptionsProvider,
    AgentMessageProvider? SteeringMessageProvider,
    AgentMessageProvider? FollowUpMessageProvider,
    ToolExecutionMode ToolExecutionMode,
    ToolExecutionPolicy? ToolExecutionPolicy,
    ToolResultTransformer? ToolResultTransformer,
    GenerationOptions GenerationSettings,
    int? MaxRetryDelayMs = AgentLoopConfig.DefaultMaxRetryDelayMs,
    bool SkipInitialSteeringPoll = false,
    TimeSpan? ToolTimeout = null,
    ClaimAuditOptions? ClaimAudit = null,
    Func<CancellationToken, Task<AgentContext?>>? ContextCompactionService = null,
    TimeSpan? ToolExecutionPolicyTimeout = null,
    Action<string>? DiagnosticObserver = null,
    IProviderSuspensionRegistry? SuspensionRegistry = null,
    string? AuthProfile = null,
    Func<double>? RetryRandomnessProvider = null,
    int? MaxToolOutputBytes = null,
    IHostSuspendDetector? SuspendDetector = null,
    ToolAuditGate? ToolAuditGate = null,
    ToolExecutionDecisionObserver? ToolExecutionDecisionObserver = null,
    Func<string, string>? ToolResultTextTransformer = null,
    BotNexus.Agent.Core.Tools.SatelliteToolExecutionOptions? SatelliteToolExecution = null,
    RunCompletionPolicy? RunCompletionPolicy = null,
    int MaxCompletionContinuations = 2,
    CredentialInvalidationService? CredentialInvalidationService = null,
    IProviderRecoveryCoordinator? RecoveryCoordinator = null,
    TimeSpan? RecoveryAdmissionTimeout = null,
    ToolProgressPolicy? ToolProgressPolicy = null)
{
    /// <summary>
    /// Default cooperative cancellation budget for the tool-execution policy.
    /// </summary>
    public static readonly TimeSpan DefaultToolExecutionPolicyTimeout = TimeSpan.FromSeconds(15);

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

    /// <summary>The non-negative automatic continuation bound.</summary>
    public int EffectiveMaxCompletionContinuations => Math.Max(0, MaxCompletionContinuations);
}
