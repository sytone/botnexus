using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Typed hub client interface defining the server→client event contract.
/// Every method maps to a client-side event handler registered by the WebUI.
/// </summary>
public interface IGatewayHubClient
{
    Task Connected(ConnectedPayload payload);
    Task SessionReset(SessionResetPayload payload);
    /// <summary>
    /// Server signals that the agent run loop has started. Brackets the entire loop with
    /// <see cref="RunEnded"/> so clients have an authoritative "agent busy" signal that stays
    /// asserted across the gaps between turns and tools (where per-step events leave a window).
    /// </summary>
    Task RunStarted(AgentStreamEvent evt);
    Task MessageStart(AgentStreamEvent evt);
    Task ContentDelta(object evt);
    Task ThinkingDelta(AgentStreamEvent evt);
    Task ToolStart(AgentStreamEvent evt);
    Task ToolEnd(AgentStreamEvent evt);
    Task MessageEnd(AgentStreamEvent evt);
    Task Error(AgentStreamEvent evt);
    Task UserInputRequired(AgentStreamEvent evt);
    Task SubAgentSpawned(SubAgentEventPayload payload);
    Task SubAgentCompleted(SubAgentEventPayload payload);
    Task SubAgentFailed(SubAgentEventPayload payload);
    Task SubAgentKilled(SubAgentEventPayload payload);
    Task AgentsChanged(AgentsChangedPayload payload);
    Task ConversationChanged(ConversationChangedPayload payload);

    Task SteeringFeedback(SteeringFeedbackPayload payload);
    Task CanvasUpdated(string agentId, string conversationId, string html);
    /// <summary>Server notifies the client that a canvas state key was changed or cleared.</summary>
    Task CanvasStateChanged(string conversationId, string key, object? value);

    /// <summary>
    /// Server notifies the client that a conversation's per-conversation todo state changed.
    /// Carries the raw <c>TodoJson</c> payload (or null/empty when cleared) so the portal Todo
    /// panel can refresh live without a manual reload (#1464 step 5).
    /// </summary>
    Task TodoUpdated(string agentId, string conversationId, string? todoJson);

    /// <summary>Server notifies the client that a gateway restart interrupted its active agent turn.</summary>
    Task TurnInterrupted(AgentStreamEvent evt);

    /// <summary>Server signals that the agent turn has fully completed (all tool calls done, no more events).</summary>
    Task TurnEnd(AgentStreamEvent evt);

    /// <summary>
    /// Server signals that the agent run loop has fully settled — the final turn, last tool result,
    /// and any follow-up continuations are all done. Clients should treat the agent as idle only
    /// after this fires. Brackets the run with <see cref="RunStarted"/>.
    /// </summary>
    Task RunEnded(AgentStreamEvent evt);
}

