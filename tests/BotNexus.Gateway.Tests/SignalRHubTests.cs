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
using Microsoft.Extensions.Options;
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
        GetPropertyValue<int>(result, "messageCount").Should().Be(0);
        GetPropertyValue<bool>(result, "isResumed").Should().BeFalse();
    }

    [Fact]
    public async Task JoinSession_NewSession_ReturnsIsResumedFalse()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync("conn-1", "session:new-session", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync("new-session", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = "new-session",
                AgentId = "agent-a",
                History = []
            });

        var hub = CreateHub(groups: groups.Object, sessions: sessions.Object, connectionId: "conn-1");

        var result = await hub.JoinSession("agent-a", "new-session");

        GetPropertyValue<bool>(result, "isResumed").Should().BeFalse();
        GetPropertyValue<int>(result, "messageCount").Should().Be(0);
    }

    [Fact]
    public async Task JoinSession_ExistingSessionWithHistory_ReturnsIsResumedTrue()
    {
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync("existing", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = "existing",
                AgentId = "agent-a",
                History =
                [
                    new SessionEntry { Role = "user", Content = "hello" },
                    new SessionEntry { Role = "assistant", Content = "hi" }
                ]
            });

        var hub = CreateHub(sessions: sessions.Object, connectionId: "conn-1");

        var result = await hub.JoinSession("agent-a", "existing");

        GetPropertyValue<bool>(result, "isResumed").Should().BeTrue();
        GetPropertyValue<int>(result, "messageCount").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task JoinSession_ExpiredSession_ReactivatesAndReturnsResumed()
    {
        var expiredSession = new GatewaySession
        {
            SessionId = "expired",
            AgentId = "agent-a",
            Status = SessionStatus.Expired,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            History =
            [
                new SessionEntry { Role = "user", Content = "persisted" }
            ]
        };

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync("expired", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expiredSession);
        sessions.Setup(value => value.SaveAsync(expiredSession, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(sessions: sessions.Object, connectionId: "conn-1");

        var result = await hub.JoinSession("agent-a", "expired");

        expiredSession.Status.Should().Be(SessionStatus.Active);
        expiredSession.ExpiresAt.Should().BeNull();
        sessions.Verify(value => value.SaveAsync(expiredSession, It.IsAny<CancellationToken>()), Times.Once);
        GetPropertyValue<bool>(result, "isResumed").Should().BeTrue();
        GetPropertyValue<string>(result, "status").Should().Be(SessionStatus.Active.ToString());
    }

    [Fact]
    public async Task JoinSession_ReturnsStatusAndTimestamps()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var updatedAt = DateTimeOffset.UtcNow;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync("s1", "agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = "s1",
                AgentId = "agent-a",
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            });

        var hub = CreateHub(sessions: sessions.Object, connectionId: "conn-1");

        var result = await hub.JoinSession("agent-a", "s1");

        GetPropertyValue<string>(result, "status").Should().Be(SessionStatus.Active.ToString());
        GetPropertyValue<DateTimeOffset>(result, "createdAt").Should().Be(createdAt);
        GetPropertyValue<DateTimeOffset>(result, "updatedAt").Should().Be(updatedAt);
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

    [Fact]
    public async Task ResetSession_ArchivesInsteadOfDeleting()
    {
        var caller = new Mock<ISingleClientProxy>();
        caller.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(value => value.Caller).Returns(caller.Object);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.ArchiveAsync("session-1", CancellationToken.None)).Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(value => value.StopAsync("agent-a", "session-1", CancellationToken.None)).Returns(Task.CompletedTask);

        var hub = CreateHub(clients: clients.Object, sessions: sessions.Object, supervisor: supervisor.Object);

        await hub.ResetSession("agent-a", "session-1");

        sessions.Verify(value => value.ArchiveAsync("session-1", CancellationToken.None), Times.Once);
        sessions.Verify(value => value.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompactSession_Hub_ReturnsCompactionStats()
    {
        var session = new GatewaySession { SessionId = "session-1", AgentId = "agent-a" };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync("session-1", CancellationToken.None)).ReturnsAsync(session);
        sessions.Setup(value => value.SaveAsync(session, CancellationToken.None)).Returns(Task.CompletedTask);

        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(value => value.CompactAsync(session, It.IsAny<CompactionOptions>(), CancellationToken.None))
            .ReturnsAsync(new CompactionResult
            {
                Summary = "summary",
                EntriesSummarized = 5,
                EntriesPreserved = 3,
                TokensBefore = 2000,
                TokensAfter = 800
            });

        var hub = CreateHub(
            sessions: sessions.Object,
            compactor: compactor.Object,
            compactionOptions: Options.Create(new CompactionOptions()));

        var result = await hub.CompactSession("agent-a", "session-1");

        GetPropertyValue<int>(result, "summarized").Should().Be(5);
        GetPropertyValue<int>(result, "preserved").Should().Be(3);
        GetPropertyValue<int>(result, "tokensBefore").Should().Be(2000);
        GetPropertyValue<int>(result, "tokensAfter").Should().Be(800);
    }

    [Fact]
    public async Task CompactSession_Hub_SessionNotFound_ThrowsHubException()
    {
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync("missing", CancellationToken.None)).ReturnsAsync((GatewaySession?)null);
        var hub = CreateHub(sessions: sessions.Object);

        Func<Task> act = () => hub.CompactSession("agent-a", "missing");

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("Session 'missing' not found.");
    }

    private static GatewayHub CreateHub(
        IHubCallerClients? clients = null,
        IGroupManager? groups = null,
        ISessionStore? sessions = null,
        IChannelDispatcher? dispatcher = null,
        IActivityBroadcaster? activity = null,
        IAgentRegistry? registry = null,
        IAgentSupervisor? supervisor = null,
        ISessionCompactor? compactor = null,
        IOptions<CompactionOptions>? compactionOptions = null,
        string connectionId = "conn-test")
    {
        var hub = new GatewayHub(
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            registry ?? Mock.Of<IAgentRegistry>(),
            sessions ?? Mock.Of<ISessionStore>(),
            dispatcher ?? Mock.Of<IChannelDispatcher>(),
            activity ?? Mock.Of<IActivityBroadcaster>(),
            compactor ?? Mock.Of<ISessionCompactor>(),
            compactionOptions ?? Options.Create(new CompactionOptions()),
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

    private static T GetPropertyValue<T>(object payload, string propertyName)
    {
        var property = payload.GetType().GetProperty(propertyName);
        property.Should().NotBeNull();

        var value = property!.GetValue(payload);
        value.Should().NotBeNull();
        return (T)value!;
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
