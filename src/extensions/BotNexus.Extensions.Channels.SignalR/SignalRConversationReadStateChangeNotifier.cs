using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>Routes durable read-state invalidations to the SignalR user owning the cursor.</summary>
public sealed class SignalRConversationReadStateChangeNotifier(
    IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IConversationReadStateChangeNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    /// <inheritdoc />
    public Task NotifyReadStateChangedAsync(
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        long version,
        CancellationToken cancellationToken = default)
        => _hubContext.Clients.User(readerId.Value).ConversationReadStateChanged(
            new ConversationReadStateChangedPayload(conversationId.Value, position.Value, version));
}
