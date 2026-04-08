using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;

namespace BotNexus.Gateway.Tests;

public sealed class SignalRHubTests
{
    [Fact]
    public async Task GatewayHub_OnConnected_SendsConnectionInfo()
    {
        var caller = new Mock<ISingleClientProxy>();
        caller.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll()).Returns([
            new AgentDescriptor
            {
                AgentId = "assistant",
                DisplayName = "Assistant",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            }
        ]);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(value => value.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var hub = CreateHub(
            clients: clients.Object,
            activity: activity.Object,
            registry: registry.Object,
            connectionId: "conn-1");

        await hub.OnConnectedAsync();

        caller.Verify(proxy => proxy.SendCoreAsync(
                "Connected",
                It.Is<object?[]>(args => HasPropertyValue(args, "connectionId", "conn-1")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        activity.Verify(value => value.PublishAsync(
                It.Is<GatewayActivity>(a =>
                    a.ChannelType == "signalr" &&
                    a.Message == "Web Chat client connected."),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_JoinSession_CreatesSessionAndAddsToGroup()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync("conn-1", "session:s1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync("s1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = "s1", AgentId = "agent-a" });

        var hub = CreateHub(
            groups: groups.Object,
            sessions: sessions.Object,
            connectionId: "conn-1");

        var result = await hub.JoinSession("agent-a", "s1");

        groups.Verify(value => value.AddToGroupAsync("conn-1", "session:s1", It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(value => value.GetOrCreateAsync("s1", "agent-a", It.IsAny<CancellationToken>()), Times.Once);
        HasPropertyValue([result], "sessionId", "s1").Should().BeTrue();
        HasPropertyValue([result], "agentId", "agent-a").Should().BeTrue();
        HasPropertyValue([result], "connectionId", "conn-1").Should().BeTrue();
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DispatchesThroughGateway()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, connectionId: "conn-1");

        await hub.SendMessage("agent-a", "session-1", "hello");

        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.ChannelType == "signalr" &&
                    m.SenderId == "conn-1" &&
                    m.ConversationId == "session-1" &&
                    m.SessionId == "session-1" &&
                    m.TargetAgentId == "agent-a" &&
                    m.Content == "hello"),
                CancellationToken.None),
            Times.Once);
    }

    private static GatewayHub CreateHub(
        IHubCallerClients? clients = null,
        IGroupManager? groups = null,
        ISessionStore? sessions = null,
        IChannelDispatcher? dispatcher = null,
        IActivityBroadcaster? activity = null,
        IAgentRegistry? registry = null,
        IAgentSupervisor? supervisor = null,
        string connectionId = "conn-test")
    {
        var hub = new GatewayHub(
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            registry ?? Mock.Of<IAgentRegistry>(),
            sessions ?? Mock.Of<ISessionStore>(),
            dispatcher ?? Mock.Of<IChannelDispatcher>(),
            activity ?? Mock.Of<IActivityBroadcaster>(),
            NullLogger<GatewayHub>.Instance)
        {
            Clients = clients ?? Mock.Of<IHubCallerClients>(),
            Groups = groups ?? Mock.Of<IGroupManager>(),
            Context = new TestHubCallerContext(connectionId)
        };

        return hub;
    }

    private static bool HasPropertyValue(object?[] args, string propertyName, string expectedValue)
    {
        args.Should().NotBeEmpty();
        var payload = args[0];
        payload.Should().NotBeNull();

        var property = payload!.GetType().GetProperty(propertyName);
        property.Should().NotBeNull();

        return string.Equals(property!.GetValue(payload)?.ToString(), expectedValue, StringComparison.Ordinal);
    }

    private sealed class TestHubCallerContext(string connectionId) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => "user";
        public override ClaimsPrincipal? User { get; } = new();
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }
}
