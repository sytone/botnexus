using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Pins the POST create migration from transport-specific change notifications to the
/// channel-neutral conversation event seam (issue #2088).
/// </summary>
public sealed class ConversationsControllerCreateEventTests
{
    [Fact]
    public async Task Create_PublishesPersistedConversationSnapshotAfterStoreCommit()
    {
        var persistedStore = new InMemoryConversationStore();
        var persistedBinding = new ChannelBinding
        {
            BindingId = BindingId.From("binding-created"),
            ChannelType = ChannelKey.From("telegram"),
            AdapterId = "bot-a",
            ChannelAddress = ChannelAddress.From("chat-2088"),
            Mode = BindingMode.NotifyOnly,
            ThreadingMode = ThreadingMode.Prefix
        };
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(async (Conversation candidate, CancellationToken cancellationToken) =>
            {
                // Model a store that stamps/normalizes the aggregate before returning it. The
                // controller must publish this returned authority, not its pre-save candidate.
                candidate.Title = "Store-normalized title";
                await persistedStore.CreateAsync(candidate, cancellationToken);
                await persistedStore.AddBindingAsync(candidate.ConversationId, persistedBinding, cancellationToken);
                return (await persistedStore.GetAsync(candidate.ConversationId, cancellationToken)).ShouldNotBeNull();
            });
        var sink = new PersistedConversationCapturingSink(persistedStore);
        await using var publisher = new ConversationEventPublisher([sink]);
        var legacyNotifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store.Object, publisher, [legacyNotifier.Object]);

        var result = await controller.Create(
            new CreateConversationRequest("agent-2088", "Pre-save title", "Persisted purpose"),
            CancellationToken.None);

        var createdResult = result.ShouldBeOfType<CreatedAtActionResult>();
        var response = createdResult.Value.ShouldBeOfType<ConversationResponse>();
        await publisher.WaitForDrainAsync();

        var persisted = await persistedStore.GetAsync(ConversationId.From(response.ConversationId));
        persisted.ShouldNotBeNull();
        sink.Event.ShouldNotBeNull();
        sink.WasPersistedWhenConsumed.ShouldBeTrue();
        sink.Event.ConversationId.ShouldBe(persisted.ConversationId);
        sink.Event.AgentId.ShouldBe(persisted.AgentId);
        sink.Event.Title.ShouldBe("Store-normalized title");
        sink.Event.Title.ShouldBe(persisted.Title);
        sink.Event.OccurredAt.ShouldBe(persisted.CreatedAt);
        sink.Event.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        legacyNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_WhenStoreMutationFails_PublishesNoCreatedEvent()
    {
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store failed"));
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var controller = CreateController(store.Object, publisher);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => controller.Create(
            new CreateConversationRequest("agent-2088", "Never persisted"),
            CancellationToken.None));

        exception.Message.ShouldBe("store failed");
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WhenPublisherRejectsEvent_LeavesCommittedConversationIntact()
    {
        var store = new InMemoryConversationStore();
        var controller = CreateController(store, new RejectingPublisher());

        var result = await controller.Create(
            new CreateConversationRequest("agent-2088", "Accepted by store"),
            CancellationToken.None);

        var response = result.ShouldBeOfType<CreatedAtActionResult>()
            .Value.ShouldBeOfType<ConversationResponse>();
        var persisted = await store.GetAsync(ConversationId.From(response.ConversationId));
        persisted.ShouldNotBeNull();
        persisted.Title.ShouldBe("Accepted by store");
    }

    [Fact]
    public async Task Create_WhenPublisherThrows_LeavesCommittedConversationIntact()
    {
        var store = new InMemoryConversationStore();
        var controller = CreateController(store, new ThrowingPublisher());

        var result = await controller.Create(
            new CreateConversationRequest("agent-2088", "Committed before publication"),
            CancellationToken.None);

        var response = result.ShouldBeOfType<CreatedAtActionResult>()
            .Value.ShouldBeOfType<ConversationResponse>();
        var persisted = await store.GetAsync(ConversationId.From(response.ConversationId));
        persisted.ShouldNotBeNull();
        persisted.Title.ShouldBe("Committed before publication");
    }

    private static ConversationsController CreateController(
        IConversationStore store,
        IConversationEventPublisher publisher,
        IEnumerable<IConversationChangeNotifier>? notifiers = null) =>
        new(
            store,
            new InMemorySessionStore(),
            conversationChangeNotifiers: notifiers,
            conversationEventPublisher: publisher);

    private sealed class PersistedConversationCapturingSink(IConversationStore store) : IConversationEventSink
    {
        public ConversationCreatedEvent? Event { get; private set; }

        public bool WasPersistedWhenConsumed { get; private set; }

        public async Task OnConversationEventAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default)
        {
            if (conversationEvent is not ConversationCreatedEvent createdEvent)
                return;

            Event = createdEvent;
            WasPersistedWhenConsumed = await store.GetAsync(createdEvent.ConversationId, cancellationToken) is not null;
        }
    }

    private sealed class CapturingSink : IConversationEventSink
    {
        public List<ConversationEvent> Events { get; } = [];

        public Task OnConversationEventAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(conversationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingPublisher : IConversationEventPublisher
    {
        public ValueTask<bool> PublishAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingPublisher : IConversationEventPublisher
    {
        public ValueTask<bool> PublishAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("publisher failed");

        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
