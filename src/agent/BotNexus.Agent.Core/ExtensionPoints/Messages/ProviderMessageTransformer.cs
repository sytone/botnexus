using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.ExtensionPoints.Messages;

/// <summary>
/// Converts agent messages to provider-level chat messages.
/// </summary>
/// <param name="messages">The agent messages to convert.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A provider-level chat message list.</returns>
/// <remarks>
/// <para>
/// Each AgentMessage must be converted to a UserMessage, AssistantMessage, or ToolResultMessage
/// that the LLM can understand. AgentMessages that cannot be converted (e.g., UI-only notifications,
/// status messages) should be filtered out.
/// </para>
/// <para>
/// Contract: must not throw or reject. Return a safe fallback value instead.
/// Throwing interrupts the low-level agent loop without producing a normal event sequence.
/// </para>
/// </remarks>
public delegate Task<IReadOnlyList<Message>> ProviderMessageTransformer(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);
