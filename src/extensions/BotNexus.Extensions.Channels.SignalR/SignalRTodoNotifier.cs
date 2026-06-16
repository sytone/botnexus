using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Broadcasts per-conversation todo updates to all connected SignalR clients so the portal Todo
/// panel refreshes live (#1464 step 5). Mirrors <see cref="SignalRCanvasNotifier"/>; the todo state
/// is persisted by the <c>todo</c> tool itself, so this only fans the change out.
/// </summary>
public sealed class SignalRTodoNotifier(IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IAgentTodoNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    public Task NotifyTodoUpdatedAsync(string agentId, string conversationId, string? todoJson, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.TodoUpdated(agentId, conversationId, todoJson);
}
