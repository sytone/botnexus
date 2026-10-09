using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class InterruptedTurnNotificationServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static GatewaySession CreateSession(
        string sessionId,
        string agentId,
        bool withSentinel = false,
        string? callerId = null,
        ChannelKey? channelType = null)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            Status = SessionStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            SessionType = SessionType.UserAgent,
            ChannelType = channelType,
            CallerId = callerId
        };

        if (withSentinel)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress — gateway restarted if visible]",
                IsCrashSentinel = true
            });
        }

        return session;
    }

    private static IAgentRegistry CreateRegistry(params string[] agentIds)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll())
            .Returns(agentIds.Select(id => new AgentDescriptor
            {
                AgentId = AgentId.From(id),
                DisplayName = id,
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            }).ToList());
        return registry.Object;
    }

    private static Mock<ISessionStore> CreateStore(params GatewaySession[] sessions)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListUnresolvedCrashSentinelsAsync(
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int limit, string? cursor, CancellationToken _) =>
            {
                var page = sessions
                    .Where(session => session.History.Any(entry => entry.IsCrashSentinel))
                    .OrderBy(session => session.SessionId.Value, StringComparer.Ordinal)
                    .Where(session => cursor is null || string.CompareOrdinal(session.SessionId.Value, cursor) > 0)
                    .Take(limit)
                    .ToList();
                return new UnresolvedCrashSentinelPage(page.Select(session => new UnresolvedCrashSentinelRow(session.SessionId, session.AgentId)).ToList(), null);
            });
        store.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId id, CancellationToken _) =>
                sessions.SingleOrDefault(session => session.SessionId == id));
        store.Setup(s => s.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionSaveOutcome.Persisted);
        return store;
    }

    private static InterruptedTurnNotificationService CreateService(
        ISessionStore store,
        IAgentRegistry registry,
        IActivityBroadcaster? broadcaster = null,
        IConversationEventPublisher? eventPublisher = null,
        IConversationStore? conversationStore = null)
    {
        broadcaster ??= Mock.Of<IActivityBroadcaster>();
        eventPublisher ??= Mock.Of<IConversationEventPublisher>();
        return new InterruptedTurnNotificationService(
            store,
            registry,
            broadcaster,
            eventPublisher,
            NullLogger<InterruptedTurnNotificationService>.Instance,
            orchestrator: null,
            options: null,
            conversationStore);
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_SessionWithSentinel_AddsNotificationEntryAndRemovesSentinels()
    {
        var session = CreateSession("sess-1", "agent-a", withSentinel: true);
        var store = CreateStore(session);
        var service = CreateService(store.Object, CreateRegistry("agent-a"));

        await service.StartedAsync(CancellationToken.None);

        // Sentinel should be gone
        session.History.ShouldNotContain(e => e.IsCrashSentinel);

        // A notification entry should have been appended
        session.History.ShouldContain(e => e.Role == MessageRole.Notification);
        session.History.ShouldContain(e =>
            e.Role == MessageRole.Notification &&
            e.Content.Contains("gateway was restarted"));

        // Session should have been persisted
        store.Verify(s => s.SaveAsync(session, It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_SessionWithoutSentinel_IsNotTouched()
    {
        var session = CreateSession("sess-clean", "agent-a", withSentinel: false);
        var store = CreateStore(session);
        var service = CreateService(store.Object, CreateRegistry("agent-a"));

        await service.StartedAsync(CancellationToken.None);

        session.History.ShouldNotContain(e => e.Role == MessageRole.Notification);
        store.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_BroadcastsActivity_ForInterruptedSession()
    {
        var session = CreateSession("sess-2", "agent-b", withSentinel: true);
        var store = CreateStore(session);
        var broadcaster = new Mock<IActivityBroadcaster>();
        broadcaster.Setup(b => b.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var service = CreateService(store.Object, CreateRegistry("agent-b"), broadcaster: broadcaster.Object);

        await service.StartedAsync(CancellationToken.None);

        broadcaster.Verify(
            b => b.PublishAsync(
                It.Is<GatewayActivity>(a => a.AgentId == "agent-b" && a.SessionId == "sess-2"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_MultipleAgents_OnlyInterruptedSessionsNotified()
    {
        var interrupted = CreateSession("sess-int", "agent-x", withSentinel: true);
        var clean = CreateSession("sess-clean", "agent-x", withSentinel: false);
        var store = CreateStore(interrupted, clean);
        var service = CreateService(store.Object, CreateRegistry("agent-x"));

        await service.StartedAsync(CancellationToken.None);

        store.Verify(s => s.SaveAsync(interrupted, It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.SaveAsync(clean, It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartedAsync_PersistedNotification_PublishesTypedEventAfterCommitWithBindingSnapshots()
    {
        var agentId = AgentId.From("agent-events");
        var conversationId = ConversationId.From("conv-events");
        var sessionId = SessionId.From("sess-events");
        var binding = new ChannelBinding
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("recipient-1"),
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single
        };
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            AgentId = agentId,
            ConversationId = conversationId,
            ActiveSessionId = sessionId,
            ChannelBindings = [binding]
        });
        var sessions = new InMemorySessionStore(redactor: null, conversations);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId);
        session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "interrupted",
            IsCrashSentinel = true
        });
        await sessions.SaveAsync(session);

        var sink = new PersistedNotificationCapturingSink(sessions);
        await using var publisher = new ConversationEventPublisher([sink]);
        var service = CreateService(
            sessions,
            CreateRegistry(agentId.Value),
            eventPublisher: publisher,
            conversationStore: conversations);

        await service.StartedAsync(CancellationToken.None);
        binding.ChannelAddress = ChannelAddress.From("mutated-after-save");
        await publisher.WaitForDrainAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        var received = sink.Received.ShouldHaveSingleItem();
        received.AgentId.ShouldBe(agentId);
        received.ConversationId.ShouldBe(conversationId);
        received.SessionId.ShouldBe(sessionId);
        received.Item.Role.ShouldBe(MessageRole.Notification);
        received.Item.Content.ShouldContain("gateway was restarted");
        received.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("recipient-1"));
        sink.NotificationWasPersistedWhenConsumed.ShouldBeTrue();
    }

    [Fact]
    public async Task StartedAsync_SaveFails_DoesNotPublishSuccessEvent()
    {
        var session = CreateSession("sess-save-fails", "agent-save-fails", withSentinel: true);
        session.ConversationId = ConversationId.From("conv-save-fails");
        var store = CreateStore(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("save failed"));
        var publisher = new Mock<IConversationEventPublisher>();
        var service = CreateService(store.Object, CreateRegistry("agent-save-fails"), eventPublisher: publisher.Object);

        await service.StartedAsync(CancellationToken.None);

        publisher.Verify(
            p => p.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartedAsync_PublisherRejectsEvent_DoesNotRollBackCommittedNotification()
    {
        var agentId = AgentId.From("agent-rejected-event");
        var conversationId = ConversationId.From("conv-rejected-event");
        var sessionId = SessionId.From("sess-rejected-event");
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation { AgentId = agentId, ConversationId = conversationId });
        var sessions = new InMemorySessionStore(redactor: null, conversations);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId);
        session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "interrupted", IsCrashSentinel = true });
        await sessions.SaveAsync(session);
        var publisher = new ConversationEventPublisher([]);
        await publisher.DisposeAsync();
        var service = CreateService(sessions, CreateRegistry(agentId.Value), eventPublisher: publisher, conversationStore: conversations);

        await service.StartedAsync(CancellationToken.None);

        var persisted = await sessions.GetAsync(sessionId);
        persisted.ShouldNotBeNull();
        persisted.History.ShouldContain(entry => entry.Role == MessageRole.Notification);
        persisted.History.ShouldNotContain(entry => entry.IsCrashSentinel);
    }

    [Fact]
    public async Task StartedAsync_PublisherThrows_DoesNotRollBackCommittedNotification()
    {
        var agentId = AgentId.From("agent-publisher-fails");
        var conversationId = ConversationId.From("conv-publisher-fails");
        var sessionId = SessionId.From("sess-publisher-fails");
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation { AgentId = agentId, ConversationId = conversationId });
        var sessions = new InMemorySessionStore(redactor: null, conversations);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId);
        session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "interrupted", IsCrashSentinel = true });
        await sessions.SaveAsync(session);
        var service = CreateService(
            sessions,
            CreateRegistry(agentId.Value),
            eventPublisher: new ThrowingConversationEventPublisher(),
            conversationStore: conversations);

        await service.StartedAsync(CancellationToken.None);

        var persisted = await sessions.GetAsync(sessionId);
        persisted.ShouldNotBeNull();
        persisted.History.ShouldContain(entry => entry.Role == MessageRole.Notification);
        persisted.History.ShouldNotContain(entry => entry.IsCrashSentinel);
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var store = CreateStore();
        var service = CreateService(store.Object, CreateRegistry());
        var ex = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));
        ex.ShouldBeNull();
    }

    private sealed class PersistedNotificationCapturingSink(ISessionStore sessions) : IConversationEventSink
    {
        public List<ConversationSessionItemPersistedEvent> Received { get; } = [];

        public bool NotificationWasPersistedWhenConsumed { get; private set; }

        public async Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
        {
            if (conversationEvent is not ConversationSessionItemPersistedEvent persistedEvent)
                return;

            var persisted = await sessions.GetAsync(persistedEvent.SessionId!.Value, cancellationToken);
            NotificationWasPersistedWhenConsumed = persisted?.History.Any(entry =>
                entry.Role == MessageRole.Notification &&
                entry.Content == persistedEvent.Item.Content) == true;
            Received.Add(persistedEvent);
        }
    }

    private sealed class ThrowingConversationEventPublisher : IConversationEventPublisher
    {
        public ValueTask<bool> PublishAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publisher failed");

        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
