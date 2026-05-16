using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

public sealed class SignalRCanvasNotifier(IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IAgentCanvasNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    public Task NotifyCanvasUpdatedAsync(string agentId, string html, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.CanvasUpdated(agentId, html);
}
