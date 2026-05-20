using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Emits conversation lifecycle changes to all connected SignalR clients.
/// </summary>
public sealed class SignalRConversationChangeNotifier(IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IConversationChangeNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    /// <inheritdoc />
    public Task NotifyConversationChangedAsync(string changeType, string agentId, string conversationId, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.ConversationChanged(new ConversationChangedPayload(changeType, agentId, conversationId, DateTimeOffset.UtcNow));
}
