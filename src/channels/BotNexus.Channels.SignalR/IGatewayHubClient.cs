using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Channels.SignalR;

/// <summary>
/// Typed hub client interface defining the server→client event contract.
/// Every method maps to a client-side event handler registered by the WebUI.
/// </summary>
public interface IGatewayHubClient
{
    Task Connected(ConnectedPayload payload);
    Task SessionReset(SessionResetPayload payload);
    Task MessageStart(AgentStreamEvent evt);
    Task ContentDelta(object evt);
    Task ThinkingDelta(AgentStreamEvent evt);
    Task ToolStart(AgentStreamEvent evt);
    Task ToolEnd(AgentStreamEvent evt);
    Task MessageEnd(AgentStreamEvent evt);
    Task Error(AgentStreamEvent evt);
    Task SubAgentSpawned(SubAgentEventPayload payload);
    Task SubAgentCompleted(SubAgentEventPayload payload);
    Task SubAgentFailed(SubAgentEventPayload payload);
    Task SubAgentKilled(SubAgentEventPayload payload);
}
