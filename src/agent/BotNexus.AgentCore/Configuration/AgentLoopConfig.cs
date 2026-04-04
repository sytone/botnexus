using BotNexus.AgentCore.Types;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;

namespace BotNexus.AgentCore.Configuration;

/// <summary>
/// Defines the immutable runtime contract for a pi-mono compatible agent loop.
/// </summary>
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
public record AgentLoopConfig(
    ModelDefinition Model,
    ConvertToLlmDelegate ConvertToLlm,
    TransformContextDelegate TransformContext,
    GetApiKeyDelegate GetApiKey,
    GetMessagesDelegate? GetSteeringMessages,
    GetMessagesDelegate? GetFollowUpMessages,
    ToolExecutionMode ToolExecutionMode,
    BeforeToolCallDelegate? BeforeToolCall,
    AfterToolCallDelegate? AfterToolCall,
    GenerationSettings GenerationSettings);
