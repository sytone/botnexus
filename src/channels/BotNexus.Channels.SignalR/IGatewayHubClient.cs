using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Channels.SignalR;

/// <summary>
/// Typed hub client interface defining the server→client event contract.
/// </summary>
public interface IGatewayHubClient
{
    Task Connected(object payload);
    Task SessionReset(object payload);
    Task MessageStart(AgentStreamEvent evt);
    Task ContentDelta(object evt);
    Task ThinkingDelta(AgentStreamEvent evt);
    Task ToolStart(AgentStreamEvent evt);
    Task ToolEnd(AgentStreamEvent evt);
    Task MessageEnd(AgentStreamEvent evt);
    Task Error(AgentStreamEvent evt);
    Task SubAgentSpawned(object payload);
    Task SubAgentCompleted(object payload);
    Task SubAgentFailed(object payload);
    Task SubAgentKilled(object payload);
}
