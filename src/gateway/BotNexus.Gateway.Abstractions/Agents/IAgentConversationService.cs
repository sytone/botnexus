using BotNexus.Domain.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Executes synchronous conversations between registered peer agents.
/// </summary>
public interface IAgentConversationService
{
    /// <summary>
    /// Starts a peer agent conversation and returns the completed transcript/result.
    /// </summary>
    Task<AgentConversationResult> ConverseAsync(ConversationRequest request, CancellationToken cancellationToken = default);
}
