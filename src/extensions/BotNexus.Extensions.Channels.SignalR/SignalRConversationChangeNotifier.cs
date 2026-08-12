using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Emits conversation lifecycle changes to the connections that observe the affected agent.
/// </summary>
/// <remarks>
/// Scope is per-agent, not global (#2541). See <see cref="NotifyConversationChangedAsync"/> for why
/// the previous <c>Clients.All</c> broadcast was a defect.
/// </remarks>
public sealed class SignalRConversationChangeNotifier(IHubContext<GatewayHub, IGatewayHubClient> hubContext) : IConversationChangeNotifier
{
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    /// <inheritdoc />
    public Task NotifyConversationChangedAsync(string changeType, string agentId, string conversationId, CancellationToken cancellationToken = default)
    {
        // #2541: this used to be Clients.All. Every connected portal therefore re-fetched its whole
        // conversation list whenever ANY client touched ANY conversation on ANY agent -- an O(clients)
        // REST storm triggered by unrelated activity. The event is now addressed to the group for the
        // agent the change actually belongs to, which connections join via the hub's SubscribeAgents
        // verb for exactly the agents they render.
        //
        // Do NOT restore Clients.All here: the fan-out cost is invisible on a single-client dev box
        // and only shows up as portal-wide load once several tabs/devices are connected.
        if (string.IsNullOrWhiteSpace(agentId))
        {
            // No agent means no group to address. Dropping is deliberate: sending to "agent:" would
            // be a silent no-op that merely LOOKS delivered, and broadcasting would reinstate the
            // defect above.
            return Task.CompletedTask;
        }

        return _hubContext.Clients
            .Group(SignalRChannelAdapter.GetAgentGroup(agentId))
            .ConversationChanged(new ConversationChangedPayload(changeType, agentId, conversationId, DateTimeOffset.UtcNow));
    }
}
