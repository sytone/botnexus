using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class CompactionNotificationEventTests
{
    [Fact]
    public async Task PublishNotificationAsync_PersistsThenPublishesExactSessionItemThroughRealSeam()
    {
        var agentId = AgentId.From("agent-compaction");
        var conversationId = ConversationId.From("conversation-compaction");
        var sessionId = SessionId.From("session-compaction");
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("binding-compaction"),
            ChannelType = ChannelKey.From("web"),
            AdapterId = "desktop",
            ChannelAddress = ChannelAddress.From("browser-compaction"),
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single
        };
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            ActiveSessionId = sessionId,
            ChannelBindings = [binding]
        });
        var sessions = new InMemorySessionStore();
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ConversationId = conversationId
        };
        await sessions.SaveAsync(session);

        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var coordinator = CreateCoordinator(sessions, conversations, publisher);
        var outcome = SuccessfulOutcome();

        var accepted = await coordinator.TryPublishNotificationAsync(outcome, agentId, session, CancellationToken.None);
        await publisher.WaitForDrainAsync();

        accepted.ShouldBeTrue();
        var persisted = await sessions.GetAsync(sessionId);
        persisted.ShouldNotBeNull();
        var notification = persisted.History.ShouldHaveSingleItem();
        notification.Role.ShouldBe(MessageRole.Notification);
        notification.Content.ShouldBe(coordinator.BuildNotificationText(outcome));

        var received = sink.Received.ShouldHaveSingleItem().ShouldBeOfType<ConversationSessionItemPersistedEvent>();
        received.AgentId.ShouldBe(agentId);
        received.ConversationId.ShouldBe(conversationId);
        received.SessionId.ShouldBe(sessionId);
        received.Item.ShouldBe(notification);
        received.Bindings.ShouldHaveSingleItem().ShouldBe(ConversationBindingSnapshot.From(binding));
    }

    [Fact]
    public async Task PublishNotificationAsync_SaveFailure_PublishesNoSuccessEvent()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-compaction"),
            AgentId = AgentId.From("agent-compaction"),
            ConversationId = ConversationId.From("conversation-compaction")
        };
        var sessions = new Mock<ISessionStore>();
        sessions
            .Setup(value => value.SaveAsync(session, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("store unavailable"));
        var publisher = new Mock<IConversationEventPublisher>();
        var coordinator = new SessionCompactionCoordinator(
            Mock.Of<ISessionCompactor>(),
            sessions.Object,
            Mock.Of<IAgentSupervisor>(),
            publisher.Object,
            conversations: null,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<SessionCompactionCoordinator>.Instance);

        await Should.ThrowAsync<IOException>(() => coordinator.TryPublishNotificationAsync(
            SuccessfulOutcome(),
            session.AgentId,
            session,
            CancellationToken.None));

        publisher.Verify(
            value => value.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishNotificationAsync_PublisherFailure_DoesNotRollbackCommittedItem()
    {
        var agentId = AgentId.From("agent-compaction");
        var conversationId = ConversationId.From("conversation-compaction");
        var sessionId = SessionId.From("session-compaction");
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            ActiveSessionId = sessionId
        });
        var sessions = new InMemorySessionStore();
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ConversationId = conversationId
        };
        await sessions.SaveAsync(session);
        var publisher = new Mock<IConversationEventPublisher>();
        publisher
            .Setup(value => value.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publisher unavailable"));
        var coordinator = CreateCoordinator(sessions, conversations, publisher.Object);

        var accepted = await coordinator.TryPublishNotificationAsync(SuccessfulOutcome(), agentId, session, CancellationToken.None);

        accepted.ShouldBeFalse();
        var persisted = await sessions.GetAsync(sessionId);
        persisted.ShouldNotBeNull();
        persisted.History.ShouldHaveSingleItem().Role.ShouldBe(MessageRole.Notification);
    }

    private static SessionCompactionCoordinator CreateCoordinator(
        ISessionStore sessions,
        InMemoryConversationStore conversations,
        IConversationEventPublisher publisher) =>
        new(
            Mock.Of<ISessionCompactor>(),
            sessions,
            Mock.Of<IAgentSupervisor>(),
            publisher,
            conversations,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<SessionCompactionCoordinator>.Instance);

    private static SessionCompactionOutcome SuccessfulOutcome() =>
        new(
            Succeeded: true,
            Applied: true,
            HistoryOutcome: HistoryReplaceOutcome.Applied,
            EntriesSummarized: 4,
            EntriesPreserved: 2,
            TokensBefore: 600,
            TokensAfter: 200,
            FailureReason: null);

    private sealed class CapturingSink : IConversationEventSink
    {
        private readonly List<ConversationEvent> _received = [];

        public IReadOnlyList<ConversationEvent> Received => _received;

        public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
        {
            _received.Add(conversationEvent);
            return Task.CompletedTask;
        }
    }
}
