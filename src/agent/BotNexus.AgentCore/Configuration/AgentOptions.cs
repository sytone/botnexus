using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Configuration;

/// <summary>
/// Defines creation-time options for initializing an agent loop instance.
/// </summary>
/// <param name="InitialState">The optional initial mutable state seed.</param>
/// <param name="Model">The model definition used for provider calls.</param>
/// <param name="ConvertToLlm">Converts agent messages to provider chat messages.</param>
/// <param name="TransformContext">Transforms the agent message context before calls.</param>
/// <param name="GetApiKey">Resolves provider API keys on demand.</param>
/// <param name="GetSteeringMessages">Provides steering messages when configured.</param>
/// <param name="GetFollowUpMessages">Provides follow-up messages when configured.</param>
/// <param name="ToolExecutionMode">Controls tool execution ordering.</param>
/// <param name="BeforeToolCall">Optional pre-tool-call hook.</param>
/// <param name="AfterToolCall">Optional post-tool-call hook.</param>
/// <param name="GenerationSettings">The generation settings for model calls.</param>
/// <param name="SteeringMode">Controls steering message queue consumption.</param>
/// <param name="FollowUpMode">Controls follow-up message queue consumption.</param>
/// <param name="SessionId">Optional caller-provided session identifier.</param>
public record AgentOptions(
    AgentInitialState? InitialState,
    LlmModel Model,
    ConvertToLlmDelegate ConvertToLlm,
    TransformContextDelegate TransformContext,
    GetApiKeyDelegate GetApiKey,
    GetMessagesDelegate? GetSteeringMessages,
    GetMessagesDelegate? GetFollowUpMessages,
    ToolExecutionMode ToolExecutionMode,
    BeforeToolCallDelegate? BeforeToolCall,
    AfterToolCallDelegate? AfterToolCall,
    SimpleStreamOptions GenerationSettings,
    QueueMode SteeringMode,
    QueueMode FollowUpMode,
    string? SessionId = null);
