using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.AspNetCore.SignalR;
namespace BotNexus.Extensions.Channels.SignalR;
public sealed class SignalRCanvasNotifier(IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IAgentCanvasNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;
    public Task NotifyCanvasUpdatedAsync(string agentId, string conversationId, string html, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.CanvasUpdated(agentId, conversationId, html);

    public Task NotifyCanvasStateChangedAsync(string conversationId, string key, object? value, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.CanvasStateChanged(conversationId, key, value);
}
