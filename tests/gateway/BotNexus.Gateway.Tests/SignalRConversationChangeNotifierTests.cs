using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the <see cref="SignalRConversationChangeNotifier"/> fan-out scope (#2541 AC2/AC3).
/// </summary>
/// <remarks>
/// Before #2541 the notifier published every conversation lifecycle change to
/// <c>Clients.All</c>, so every connected portal re-fetched its conversation list on every other
/// client's activity on an unrelated agent. The event is now addressed to the per-agent group
/// (<c>agent:{agentId}</c>) that <c>GatewayHub.SubscribeAll</c>/<c>SubscribeAgents</c> joins, so a
/// connection only hears about agents it actually observes.
/// <para>
/// These are deliberately written against the <see cref="IHubClients{T}"/> seam rather than a live
/// hub: they assert WHICH addressing method the notifier chooses, which an end-to-end test cannot
/// distinguish when the only connected client happens to observe every agent. The observable
/// "agent A client hears nothing about agent B" assertion lives in the integration suite.
/// </para>
/// </remarks>
public sealed class SignalRConversationChangeNotifierTests
{
    private static (Mock<IGatewayHubClient> GroupProxy, Mock<IGatewayHubClient> AllProxy, Mock<IHubClients<IGatewayHubClient>> Clients, SignalRConversationChangeNotifier Notifier, List<string> RequestedGroups) CreateNotifier()
    {
        var groupProxy = new Mock<IGatewayHubClient>();
        groupProxy.Setup(proxy => proxy.ConversationChanged(It.IsAny<ConversationChangedPayload>()))
            .Returns(Task.CompletedTask);

        var allProxy = new Mock<IGatewayHubClient>();
        allProxy.Setup(proxy => proxy.ConversationChanged(It.IsAny<ConversationChangedPayload>()))
            .Returns(Task.CompletedTask);

        var requestedGroups = new List<string>();
        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.All).Returns(allProxy.Object);
        clients.Setup(value => value.Group(It.IsAny<string>()))
            .Returns((string group) =>
            {
                requestedGroups.Add(group);
                return groupProxy.Object;
            });

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        return (groupProxy, allProxy, clients, new SignalRConversationChangeNotifier(hubContext.Object), requestedGroups);
    }

    /// <summary>
    /// Happy path: the change is delivered to the affected agent's group with the full payload.
    /// </summary>
    [Fact]
    public async Task NotifyConversationChangedAsync_SendsToTheAffectedAgentGroup()
    {
        var (groupProxy, _, _, notifier, requestedGroups) = CreateNotifier();

        await notifier.NotifyConversationChangedAsync("created", "agent-a", "conv-1");

        requestedGroups.ShouldBe(["agent:agent-a"]);
        groupProxy.Verify(
            proxy => proxy.ConversationChanged(It.Is<ConversationChangedPayload>(payload =>
                payload.ChangeType == "created"
                && payload.AgentId == "agent-a"
                && payload.ConversationId == "conv-1")),
            Times.Once);
    }

    /// <summary>
    /// Sad path — the actual #2541 defect: the notifier must never address <c>Clients.All</c>.
    /// A single unscoped send is enough to make every connected client re-fetch on every other
    /// client's activity, which is the fan-out defect this issue exists to remove.
    /// </summary>
    [Fact]
    public async Task NotifyConversationChangedAsync_NeverBroadcastsToAllClients()
    {
        var (_, allProxy, clients, notifier, _) = CreateNotifier();

        await notifier.NotifyConversationChangedAsync("archived", "agent-a", "conv-1");

        clients.VerifyGet(value => value.All, Times.Never);
        allProxy.Verify(
            proxy => proxy.ConversationChanged(It.IsAny<ConversationChangedPayload>()),
            Times.Never);
    }

    /// <summary>
    /// Sad path: activity on one agent must not be addressed to another agent's group. Pins the
    /// group key to the agent carried on the change itself, so a future refactor cannot quietly
    /// widen the scope back out by deriving the group from something coarser.
    /// </summary>
    [Fact]
    public async Task NotifyConversationChangedAsync_DoesNotAddressAnotherAgentsGroup()
    {
        var (_, _, _, notifier, requestedGroups) = CreateNotifier();

        await notifier.NotifyConversationChangedAsync("updated", "agent-b", "conv-9");

        requestedGroups.ShouldBe(["agent:agent-b"]);
        requestedGroups.ShouldNotContain("agent:agent-a");
    }

    /// <summary>
    /// Sad path: a blank agent id has no meaningful group to address. Sending it to a group named
    /// <c>agent:</c> would be a silent no-op that looks delivered; broadcasting it would reinstate
    /// the defect. The notifier drops it instead, and touches neither addressing method.
    /// </summary>
    [Fact]
    public async Task NotifyConversationChangedAsync_BlankAgentId_IsDroppedRatherThanBroadcast()
    {
        var (groupProxy, allProxy, clients, notifier, requestedGroups) = CreateNotifier();

        await notifier.NotifyConversationChangedAsync("updated", "   ", "conv-1");

        requestedGroups.ShouldBeEmpty();
        clients.VerifyGet(value => value.All, Times.Never);
        groupProxy.Verify(proxy => proxy.ConversationChanged(It.IsAny<ConversationChangedPayload>()), Times.Never);
        allProxy.Verify(proxy => proxy.ConversationChanged(It.IsAny<ConversationChangedPayload>()), Times.Never);
    }
}
