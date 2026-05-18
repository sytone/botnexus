namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Maps SignalR hub payloads into <see cref="IClientStateStore"/> mutations.
/// Subscriptions to <see cref="GatewayHubConnection"/> events are wired inside
/// the implementation constructor (or <c>AttachAsync</c>).
/// </summary>
public interface IGatewayEventHandler
{
    void HandleConnected(ConnectedPayload payload);
    void HandleMessageStart(AgentStreamEvent evt);
    void HandleContentDelta(AgentStreamEvent evt);
    void HandleThinkingDelta(AgentStreamEvent evt);
    void HandleToolStart(AgentStreamEvent evt);
    void HandleToolEnd(AgentStreamEvent evt);
    void HandleMessageEnd(AgentStreamEvent evt);
    void HandleError(AgentStreamEvent evt);
    void HandleSessionReset(SessionResetPayload payload);
    void HandleSubAgentSpawned(SubAgentEventPayload payload);
    void HandleSubAgentCompleted(SubAgentEventPayload payload);
    void HandleSubAgentFailed(SubAgentEventPayload payload);
    void HandleSubAgentKilled(SubAgentEventPayload payload);
    void HandleReconnecting();
    Task HandleReconnectedAsync(CancellationToken cancellationToken = default);
    void HandleDisconnected();
}
