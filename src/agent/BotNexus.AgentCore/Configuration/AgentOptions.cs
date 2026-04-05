using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Configuration;

/// <summary>
/// Defines creation-time options for initializing an agent loop instance.
/// </summary>
/// <param name="InitialState">The optional initial mutable state seed (system prompt, model, tools, messages).</param>
/// <param name="Model">The model definition used for provider calls (can be overridden in InitialState).</param>
/// <param name="ConvertToLlm">Optional converter for agent messages to provider chat messages before each LLM call.</param>
/// <param name="TransformContext">Optional context transformer before provider invocation (defaults to identity passthrough).</param>
/// <param name="GetApiKey">Resolves provider API keys on demand.</param>
/// <param name="GetSteeringMessages">Provides steering messages when configured (combined with Agent.Steer queues).</param>
/// <param name="GetFollowUpMessages">Provides follow-up messages when configured (combined with Agent.FollowUp queues).</param>
/// <param name="ToolExecutionMode">Controls tool execution ordering (Sequential or Parallel).</param>
/// <param name="BeforeToolCall">Optional pre-tool-call hook for validation and blocking.</param>
/// <param name="AfterToolCall">Optional post-tool-call hook for result transformation.</param>
/// <param name="GenerationSettings">The generation settings for model calls (temperature, maxTokens, sessionId, etc.).</param>
/// <param name="SteeringMode">Controls steering message queue consumption (All or OneAtATime).</param>
/// <param name="FollowUpMode">Controls follow-up message queue consumption (All or OneAtATime).</param>
/// <param name="SessionId">Optional caller-provided session identifier (overrides GenerationSettings.SessionId if set).</param>
/// <param name="OnDiagnostic">Optional callback for non-fatal runtime diagnostics.</param>
/// <param name="MaxRetryDelayMs">
/// Optional maximum delay in milliseconds for transient retry backoff.
/// Must be greater than zero when set; null means uncapped retry delay.
/// </param>
/// <remarks>
/// AgentOptions is passed to the Agent constructor and frozen for the lifetime of the agent.
/// InitialState is used to seed AgentState — changes to InitialState after construction have no effect.
/// </remarks>
public record AgentOptions(
    AgentInitialState? InitialState,
    LlmModel Model,
    LlmClient LlmClient,
    ConvertToLlmDelegate? ConvertToLlm,
    TransformContextDelegate? TransformContext,
    GetApiKeyDelegate GetApiKey,
    GetMessagesDelegate? GetSteeringMessages,
    GetMessagesDelegate? GetFollowUpMessages,
    ToolExecutionMode ToolExecutionMode,
    BeforeToolCallDelegate? BeforeToolCall,
    AfterToolCallDelegate? AfterToolCall,
    SimpleStreamOptions GenerationSettings,
    QueueMode SteeringMode,
    QueueMode FollowUpMode,
    string? SessionId = null,
    Action<string>? OnDiagnostic = null,
    int? MaxRetryDelayMs = null);
