using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class SignalRChannelAdapterTests
{
    [Fact]
    public async Task SendStreamEventAsync_RoutesByConversationGroup_AndSurfacesSessionOnPayload()
    {
        // After #682, SignalRChannelAdapter routes to "conversation:{id}" groups so the
        // subscription survives session compaction. The session id still appears on the
        // payload for provenance.
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.ContentDelta(It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.Group("conversation:conv-1")).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        var adapter = new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);
        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "delta" };

        var target = new ChannelStreamTarget(
            ConversationId.From("conv-1"),
            SessionId.From("session-1"),
            ChannelAddress.From("addr-1"),
            null);
        await adapter.SendStreamEventAsync(target, streamEvent, CancellationToken.None);

        clients.Verify(value => value.Group("conversation:conv-1"), Times.Once);
        clientProxy.Verify(proxy => proxy.ContentDelta(
                It.Is<object>(arg =>
                    arg is AgentStreamEvent &&
                    ((AgentStreamEvent)arg).SessionId == SessionId.From("session-1") &&
                    ((AgentStreamEvent)arg).ConversationId == ConversationId.From("conv-1"))),
            Times.Once);
    }

    [Fact]
    public async Task SendStreamEventAsync_UserInputRequired_InvokesDedicatedClientEvent()
    {
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.UserInputRequired(It.IsAny<AgentStreamEvent>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.Group("conversation:conv-2")).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        var adapter = new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);
        var streamEvent = new AgentStreamEvent
        {
            Type = AgentStreamEventType.UserInputRequired,
            UserInputRequest = new AskUserRequest
            {
                RequestId = "request-1",
                AgentId = AgentId.From("agent-a"),
                SessionId = SessionId.From("session-2"),
                ConversationId = ConversationId.From("conv-2"),
                Prompt = "Pick one"
            }
        };

        var target = new ChannelStreamTarget(
            ConversationId.From("conv-2"),
            SessionId.From("session-2"),
            ChannelAddress.From("addr-2"),
            null);
        await adapter.SendStreamEventAsync(target, streamEvent, CancellationToken.None);

        clientProxy.Verify(proxy => proxy.UserInputRequired(
                It.Is<AgentStreamEvent>(evt => evt.Type == AgentStreamEventType.UserInputRequired)),
            Times.Once);
    }

    [Fact]
    public async Task SendStreamEventAsync_TurnEnd_RoutesToConversationGroupAndCallsTurnEnd()
    {
        // #668: TurnEnd must be forwarded to the SignalR group so the portal clears
        // IsStreaming on tool-only turns that produce no MessageEnd.
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.TurnEnd(It.IsAny<AgentStreamEvent>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.Group("conversation:conv-2")).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        var adapter = new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-2"),
            SessionId.From("sess-2"),
            ChannelAddress.From("addr-2"),
            null);

        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd };
        await adapter.SendStreamEventAsync(target, streamEvent);

        // TurnEnd must route to the conversation group and call TurnEnd on the client.
        clientProxy.Verify(proxy => proxy.TurnEnd(It.Is<AgentStreamEvent>(evt =>
            evt.Type == AgentStreamEventType.TurnEnd &&
            evt.ConversationId == ConversationId.From("conv-2") &&
            evt.SessionId == SessionId.From("sess-2"))), Times.Once);
    }

    [Fact]
    public async Task SendStreamEventAsync_RunStarted_RoutesToConversationGroupAndCallsRunStarted()
    {
        // RunStarted brackets the whole loop; it must reach the portal so steer/follow-up/stop
        // controls show for the entire run, not just while a single step is active.
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.RunStarted(It.IsAny<AgentStreamEvent>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.Group("conversation:conv-3")).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        var adapter = new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-3"),
            SessionId.From("sess-3"),
            ChannelAddress.From("addr-3"),
            null);

        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.RunStarted };
        await adapter.SendStreamEventAsync(target, streamEvent);

        clientProxy.Verify(proxy => proxy.RunStarted(It.Is<AgentStreamEvent>(evt =>
            evt.Type == AgentStreamEventType.RunStarted &&
            evt.ConversationId == ConversationId.From("conv-3") &&
            evt.SessionId == SessionId.From("sess-3"))), Times.Once);
    }

    [Fact]
    public async Task SendStreamEventAsync_RunEnded_RoutesToConversationGroupAndCallsRunEnded()
    {
        // RunEnded is the authoritative idle signal; it must reach the portal so the controls
        // collapse back to Send only once the entire loop has settled.
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.RunEnded(It.IsAny<AgentStreamEvent>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.Group("conversation:conv-4")).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        var adapter = new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv-4"),
            SessionId.From("sess-4"),
            ChannelAddress.From("addr-4"),
            null);

        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.RunEnded };
        await adapter.SendStreamEventAsync(target, streamEvent);

        clientProxy.Verify(proxy => proxy.RunEnded(It.Is<AgentStreamEvent>(evt =>
            evt.Type == AgentStreamEventType.RunEnded &&
            evt.ConversationId == ConversationId.From("conv-4") &&
            evt.SessionId == SessionId.From("sess-4"))), Times.Once);
    }

}
