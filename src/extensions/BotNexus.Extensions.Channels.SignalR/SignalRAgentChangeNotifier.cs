using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Emits gateway agent catalog changes to all connected SignalR clients.
/// </summary>
public sealed class SignalRAgentChangeNotifier(IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IAgentChangeNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    /// <inheritdoc />
    public Task NotifyAgentsChangedAsync(string changeType, string? agentId, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.AgentsChanged(new AgentsChangedPayload(changeType, agentId));
}
