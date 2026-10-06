using BotNexus.Agent.Core.ExtensionPoints.Messages;
using BotNexus.Agent.Core.ExtensionPoints.ProviderExecution;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.ExtensionPoints.ToolExecution;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Configuration;

/// <summary>
/// Defines creation-time options for initializing an agent loop instance.
/// </summary>
/// <param name="InitialState">The optional initial mutable state seed (system prompt, model, tools, messages).</param>
/// <param name="Model">The model definition used for provider calls (can be overridden in InitialState).</param>
/// <param name="LlmClient">The provider client used to stream model responses during runs.</param>
/// <param name="ProviderMessageTransformer">Optional converter for agent messages to provider chat messages before each LLM call.</param>
/// <param name="AgentContextTransformer">Optional context transformer before provider invocation (defaults to identity passthrough).</param>
/// <param name="ProviderExecutionOptionsProvider">Resolves provider execution policy, including credentials, on demand.</param>
/// <param name="SteeringMessageProvider">Provides steering messages when configured (combined with Agent.Steer queues).</param>
/// <param name="FollowUpMessageProvider">Provides follow-up messages when configured (combined with Agent.FollowUp queues).</param>
/// <param name="ToolExecutionMode">Controls tool execution ordering (Sequential or Parallel).</param>
/// <param name="ToolAuditGate">Optional durable audit gate with blocking authority, invoked before tool-execution policy.</param>
/// <param name="ToolExecutionPolicy">Optional tool-execution policy for validation and blocking.</param>
/// <param name="ToolExecutionPolicyTimeout">
/// Cooperative tool-execution policy budget; null selects 15 seconds. Timeout fails closed,
/// subject to host-suspend adjustment, but cannot forcibly interrupt an uncooperative callback.
/// Set to <see cref="Timeout.InfiniteTimeSpan"/> or a non-positive value to disable.
/// </param>
/// <param name="ToolResultTransformer">Optional tool-result transformer applied after execution.</param>
/// <param name="GenerationSettings">The generation settings for model calls (temperature, maxTokens, sessionId, etc.).</param>
/// <param name="SteeringMode">Controls steering message queue consumption (All or OneAtATime).</param>
/// <param name="FollowUpMode">Controls follow-up message queue consumption (All or OneAtATime).</param>
/// <param name="SessionId">Optional caller-provided session identifier (overrides GenerationSettings.SessionId if set).</param>
/// <param name="DiagnosticObserver">Optional callback for non-fatal runtime diagnostics.</param>
/// <param name="MaxRetryDelayMs">
/// Maximum delay in milliseconds for transient retry backoff, and the ceiling applied to a
/// server-supplied <c>Retry-After</c>. Must be greater than zero when set.
/// Defaults to <see cref="AgentLoopConfig.DefaultMaxRetryDelayMs"/> rather than to "uncapped" (#3035);
/// a null value is normalised to that same ceiling, so the retry delay is bounded on every path.
/// </param>
/// <param name="ToolTimeout">
/// Loop-level tool-execution timeout; null selects 120 seconds when the runtime config is built.
/// Tool-declared defaults and supported caller-requested timeouts can extend the effective budget.
/// </param>
/// <param name="ClaimAudit">
/// Optional post-turn claim-auditor configuration (#1600). When null the auditor does not run.
/// </param>
/// <param name="ContextCompactionService">
/// Optional auto-compaction service awaited before each provider turn to re-check the threshold.
/// A returned context refreshes the agent state and loop snapshot; null retains the current context.
/// <see cref="Loop.ProactiveCompactionException"/> blocks provider invocation when required compaction fails;
/// cancellation propagates and other exceptions are best-effort. Null means no mid-loop re-check.
/// </param>
/// <param name="SuspensionRegistry">
/// Optional provider-exhaustion suspension registry (#3015). Flows to the loop config; when set, a
/// non-transient exhaustion failure records a time-bounded suspension scoped to provider +
/// <paramref name="AuthProfile"/> instead of re-burning the retry budget every turn.
/// </param>
/// <param name="AuthProfile">
/// Optional auth-profile identifier scoping suspensions (#3015). Null means the empty profile.
/// </param>
/// <param name="MaxToolOutputBytes">
/// Shared central UTF-8 byte budget applied to every tool result before it reaches the model
/// (#3162). Flows to the loop config. Null means the platform default; a non-positive value
/// disables the backstop.
/// </param>
/// <param name="ToolResultTextTransformer">
/// Optional host-owned sanitizer for generic tool text after result transformation and before budgeting and retention.
/// </param>
/// <param name="ToolExecutionDecisionObserver">
/// Optional observer reporting whether a validated, audited tool call will execute after policy evaluation.
/// Observer exceptions are not swallowed by the executor.
/// </param>
/// <param name="RunCompletionPolicy">Optional authoritative host completion evaluator.</param>
/// <param name="MaxCompletionContinuations">Bound on automatic completion-gate continuation turns.</param>
/// <param name="CredentialInvalidationService">
/// Optional host-owned credential invalidation invoked before one bounded authentication retry.
/// </param>
/// <param name="RecoveryCoordinator">
/// Optional shared provider-recovery admission coordinator scoped by provider and auth profile.
/// Null disables coordinated admission, not the loop's retry handling.
/// </param>
/// <param name="RecoveryAdmissionTimeout">
/// Maximum wait for coordinated provider admission; null uses the effective retry-delay ceiling.
/// Used only when RecoveryCoordinator is set. Admission failures and cancellation propagate to the run.
/// </param>
/// <remarks>
/// AgentOptions is passed to the Agent constructor and frozen for the lifetime of the agent.
/// InitialState is used to seed AgentState - changes to InitialState after construction have no effect.
/// </remarks>
public record AgentOptions(
    AgentInitialState? InitialState,
    LlmModel Model,
    LlmClient LlmClient,
    ProviderMessageTransformer? ProviderMessageTransformer,
    AgentContextTransformer? AgentContextTransformer,
    ProviderExecutionOptionsProvider ProviderExecutionOptionsProvider,
    AgentMessageProvider? SteeringMessageProvider,
    AgentMessageProvider? FollowUpMessageProvider,
    ToolExecutionMode ToolExecutionMode,
    ToolExecutionPolicy? ToolExecutionPolicy,
    ToolResultTransformer? ToolResultTransformer,
    GenerationOptions GenerationSettings,
    QueueMode SteeringMode,
    QueueMode FollowUpMode,
    string? SessionId = null,
    Action<string>? DiagnosticObserver = null,
    int? MaxRetryDelayMs = AgentLoopConfig.DefaultMaxRetryDelayMs,
    TimeSpan? ToolTimeout = null,
    Diagnostics.ClaimAuditOptions? ClaimAudit = null,
    Func<CancellationToken, Task<AgentContext?>>? ContextCompactionService = null,
    TimeSpan? ToolExecutionPolicyTimeout = null,
    Loop.IProviderSuspensionRegistry? SuspensionRegistry = null,
    string? AuthProfile = null,
    int? MaxToolOutputBytes = null,
    ToolAuditGate? ToolAuditGate = null,
    ToolExecutionDecisionObserver? ToolExecutionDecisionObserver = null,
    Func<string, string>? ToolResultTextTransformer = null,
    RunCompletionPolicy? RunCompletionPolicy = null,
    int MaxCompletionContinuations = 2,
    CredentialInvalidationService? CredentialInvalidationService = null,
    Loop.IProviderRecoveryCoordinator? RecoveryCoordinator = null,
    TimeSpan? RecoveryAdmissionTimeout = null);
