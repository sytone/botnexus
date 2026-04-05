using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Configuration;

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
/// <param name="AfterToolCall">Optional post-tool-call hook for result transformation.</param>
/// <param name="GenerationSettings">The generation settings for model calls (temperature, maxTokens, etc.).</param>
/// <param name="MaxRetryDelayMs">
/// Optional maximum delay in milliseconds for transient retry backoff.
/// Must be greater than zero when set; null means uncapped retry delay.
/// </param>
/// <param name="SkipInitialSteeringPoll">True to skip the first steering queue drain for this run.</param>
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
    int? MaxRetryDelayMs = null,
    bool SkipInitialSteeringPoll = false);
