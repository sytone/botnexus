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
using SessionType = BotNexus.Domain.Primitives.SessionType;

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
                AgentId = BotNexus.Domain.Primitives.AgentId.From("assistant"),
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
                    a.ChannelType == ChannelKey.From("signalr") &&
                    a.Message == "Web Chat client connected."),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_UsesVisibleSessionForAgentChannel()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync("conn-1", "session:s1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var warmup = new Mock<ISessionWarmupService>();
        warmup.Setup(value => value.GetAvailableSessionsAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SessionSummary("s1", "agent-a", ChannelKey.From("signalr"), SessionStatus.Active, SessionType.UserAgent, true, 3, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow)
            ]);

        var existing = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("signalr"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent
        };

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("s1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(groups: groups.Object, sessions: sessions.Object, warmup: warmup.Object, dispatcher: dispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "web chat", "hello");

        groups.Verify(value => value.AddToGroupAsync("conn-1", "session:s1", It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(value => value.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("s1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()), Times.Once);
        dispatcher.Verify(value => value.DispatchAsync(
            It.Is<InboundMessage>(m => m.SessionId == "s1" && m.TargetAgentId == "agent-a" && m.Content == "hello"),
            CancellationToken.None), Times.Once);
        HasPropertyValue([result], "sessionId", "s1").Should().BeTrue();
    }

    [Fact]
    public async Task GatewayHub_SendMessage_NoVisibleSession_CreatesAndPersistsSession()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync("conn-1", It.Is<string>(g => g.StartsWith("session:")), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var warmup = new Mock<ISessionWarmupService>();
        warmup.Setup(value => value.GetAvailableSessionsAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        GatewaySession? capturedSession = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotNexus.Domain.Primitives.SessionId sid, BotNexus.Domain.Primitives.AgentId aid, CancellationToken _) => new GatewaySession
            {
                SessionId = sid,
                AgentId = aid
            });
        sessions.Setup(value => value.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) => capturedSession = s)
            .Returns(Task.CompletedTask);

        InboundMessage? dispatched = null;
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched = m)
            .Returns(Task.CompletedTask);

        var hub = CreateHub(groups: groups.Object, sessions: sessions.Object, warmup: warmup.Object, dispatcher: dispatcher.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "signalr", "hello");

        capturedSession.Should().NotBeNull();
        capturedSession!.ChannelType.Should().Be(ChannelKey.From("signalr"));
        groups.Verify(value => value.AddToGroupAsync("conn-1", It.Is<string>(g => g.StartsWith("session:")), It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(value => value.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Once);
        dispatched.Should().NotBeNull();
        dispatched!.TargetAgentId.Should().Be("agent-a");
        dispatched.Content.Should().Be("hello");
        GetPropertyValue<string>(result, "sessionId").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DispatchesThroughGateway()
    {
        var warmup = new Mock<ISessionWarmupService>();
        warmup.Setup(value => value.GetAvailableSessionsAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SessionSummary("session-1", "agent-a", ChannelKey.From("signalr"), SessionStatus.Active, SessionType.UserAgent, true, 0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)
            ]);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("signalr"),
                SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent
            });

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, sessions: sessions.Object, warmup: warmup.Object, connectionId: "conn-1");

        await hub.SendMessage("agent-a", "signalr", "hello");

        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.ChannelType == ChannelKey.From("signalr") &&
                    m.SenderId == "conn-1" &&
                    m.ConversationId == "session-1" &&
                    m.SessionId == "session-1" &&
                    m.TargetAgentId == "agent-a" &&
                    m.Content == "hello"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithAgentAndChannelType_RoutesToExistingSession()
    {
        var warmup = new Mock<ISessionWarmupService>();
        warmup.Setup(value => value.GetAvailableSessionsAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SessionSummary("signalr-session", "agent-a", ChannelKey.From("signalr"), SessionStatus.Active, SessionType.UserAgent, true, 1, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4)),
                new SessionSummary("telegram-session", "agent-a", ChannelKey.From("telegram"), SessionStatus.Active, SessionType.UserAgent, true, 2, DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-2))
            ]);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("telegram-session"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = BotNexus.Domain.Primitives.SessionId.From("telegram-session"),
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("telegram"),
                SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent
            });

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, sessions: sessions.Object, warmup: warmup.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "telegram", "hello-telegram");

        GetPropertyValue<string>(result, "sessionId").Should().Be("telegram-session");
        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.SessionId == "telegram-session" &&
                    m.TargetAgentId == "agent-a" &&
                    m.Content == "hello-telegram"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WithNoSessionForChannel_AutoCreatesSession()
    {
        var warmup = new Mock<ISessionWarmupService>();
        warmup.Setup(value => value.GetAvailableSessionsAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SessionSummary("signalr-session", "agent-a", ChannelKey.From("signalr"), SessionStatus.Active, SessionType.UserAgent, true, 1, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4))
            ]);

        GatewaySession? capturedSession = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotNexus.Domain.Primitives.SessionId sid, BotNexus.Domain.Primitives.AgentId aid, CancellationToken _) => new GatewaySession
            {
                SessionId = sid,
                AgentId = aid
            });
        sessions.Setup(value => value.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => capturedSession = session)
            .Returns(Task.CompletedTask);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, sessions: sessions.Object, warmup: warmup.Object, connectionId: "conn-1");

        var result = await hub.SendMessage("agent-a", "telegram", "needs-new-session");

        var createdSessionId = GetPropertyValue<string>(result, "sessionId");
        createdSessionId.Should().NotBeNullOrWhiteSpace();
        GetPropertyValue<string>(result, "channelType").Should().Be("telegram");
        capturedSession.Should().NotBeNull();
        capturedSession!.ChannelType.Should().Be(ChannelKey.From("telegram"));
        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.SessionId == createdSessionId &&
                    m.TargetAgentId == "agent-a" &&
                    m.Content == "needs-new-session"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_WhitespaceIds_DispatchesNormalizedIds()
    {
        var warmup = new Mock<ISessionWarmupService>();
        warmup.Setup(value => value.GetAvailableSessionsAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SessionSummary("session-1", "agent-a", ChannelKey.From("signalr"), SessionStatus.Active, SessionType.UserAgent, true, 0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)
            ]);

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession
            {
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                ChannelType = ChannelKey.From("signalr"),
                SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent
            });

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub(dispatcher: dispatcher.Object, sessions: sessions.Object, warmup: warmup.Object, connectionId: "conn-1");

        await hub.SendMessage("  agent-a  ", "  signalr  ", "hello");

        dispatcher.Verify(value => value.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.ChannelType == ChannelKey.From("signalr") &&
                    m.ConversationId == "session-1" &&
                    m.SessionId == "session-1" &&
                    m.TargetAgentId == "agent-a"),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DefaultAgentId_ThrowsHubException()
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.SendMessage(default, ChannelKey.From("signalr"), "hello");

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("Agent ID is required.");
    }

    [Fact]
    public async Task GatewayHub_SendMessage_DefaultChannelType_ThrowsHubException()
    {
        var hub = CreateHub();

        Func<Task> act = () => hub.SendMessage(BotNexus.Domain.Primitives.AgentId.From("agent-a"), default, "hello");

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("Channel type is required.");
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
        supervisor.Setup(value => value.StopAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), CancellationToken.None)).Returns(Task.CompletedTask);

        var hub = CreateHub(clients: clients.Object, sessions: sessions.Object, supervisor: supervisor.Object);

        await hub.ResetSession("agent-a", "session-1");

        sessions.Verify(value => value.ArchiveAsync("session-1", CancellationToken.None), Times.Once);
        sessions.Verify(value => value.DeleteAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompactSession_Hub_ReturnsCompactionStats()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(value => value.GetAsync("session-1", CancellationToken.None)).ReturnsAsync(session);
        sessions.Setup(value => value.SaveAsync(session, CancellationToken.None)).Returns(Task.CompletedTask);

        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(value => value.CompactAsync(session.Session, It.IsAny<CompactionOptions>(), CancellationToken.None))
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
            compactionOptions: new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()));

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
        ISessionWarmupService? warmup = null,
        IOptionsMonitor<CompactionOptions>? compactionOptions = null,
        string connectionId = "conn-test")
    {
        var hub = new GatewayHub(
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            registry ?? Mock.Of<IAgentRegistry>(),
            sessions ?? Mock.Of<ISessionStore>(),
            dispatcher ?? Mock.Of<IChannelDispatcher>(),
            activity ?? Mock.Of<IActivityBroadcaster>(),
            compactor ?? Mock.Of<ISessionCompactor>(),
            warmup ?? Mock.Of<ISessionWarmupService>(),
            compactionOptions ?? new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
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
