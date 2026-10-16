using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Describes one completed tool result for progress evaluation.</summary>
/// <param name="ToolCall">The provider tool call that produced the result.</param>
/// <param name="ToolResult">The completed result retained in the run transcript.</param>
/// <remarks>
/// The context is shallowly immutable. A policy must inspect it without mutating the call,
/// result, arguments, transcript, or agent state.
/// </remarks>
public sealed record ToolProgressContext(
    ToolCallContent ToolCall,
    ToolResultAgentMessage ToolResult);
