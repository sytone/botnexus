using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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
/// Pins the REST archive migration from transport-specific change notifications to the
/// channel-neutral conversation event seam (issue #2088).
/// </summary>
public sealed class ConversationsControllerArchiveEventTests
{
    [Fact]
    public async Task Archive_PublishesDurableArchivedSnapshotWithBindingSnapshot()
    {
        var store = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(store);
        var binding = CreateBinding("binding-archive");
        (await store.AddBindingAsync(conversation.ConversationId, binding)).ShouldBeTrue();
        var sink = new PersistedConversationCapturingSink(store);
        await using var publisher = new ConversationEventPublisher([sink]);
        var legacyNotifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store, publisher, [legacyNotifier.Object]);

        var result = await controller.Archive(conversation.ConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await publisher.WaitForDrainAsync();
        var persisted = await store.GetAsync(conversation.ConversationId);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(ConversationStatus.Archived);
        sink.Event.ShouldNotBeNull();
        sink.WasArchivedWhenConsumed.ShouldBeTrue();
        sink.Event.ConversationId.ShouldBe(persisted.ConversationId);
        sink.Event.AgentId.ShouldBe(persisted.AgentId);
        sink.Event.OccurredAt.ShouldBe(persisted.UpdatedAt);
        sink.Event.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        sink.Event.Bindings.ShouldHaveSingleItem().BindingId.ShouldBe(binding.BindingId);
        legacyNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Archive_WhenStoreMutationFails_PublishesNoArchivedEvent()
    {
        var inner = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(inner);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
            .Returns((ConversationId _, CancellationToken cancellationToken) => inner.GetAsync(conversation.ConversationId, cancellationToken));
        store.Setup(s => s.ArchiveAsync(
                conversation.ConversationId,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("archive failed"));
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var controller = CreateController(store.Object, publisher);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => controller.Archive(
            conversation.ConversationId.Value,
            CancellationToken.None));

        exception.Message.ShouldBe("archive failed");
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Archive_WhenPublisherRejectsEvent_LeavesCommittedArchiveIntact()
    {
        var store = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(store);
        var controller = CreateController(store, new RejectingPublisher());

        var result = await controller.Archive(conversation.ConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        var persisted = await store.GetAsync(conversation.ConversationId);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task Archive_WhenPublisherThrows_LeavesCommittedArchiveIntact()
    {
        var store = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(store);
        var controller = CreateController(store, new ThrowingPublisher());

        var result = await controller.Archive(conversation.ConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        var persisted = await store.GetAsync(conversation.ConversationId);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(ConversationStatus.Archived);
    }

    private static async Task<Conversation> CreateConversationAsync(InMemoryConversationStore store)
    {
        var conversation = ConversationFactory.CreateForChannel(
            ConversationId.Create(),
            AgentId.From("agent-2088"),
            title: "Archive lifecycle",
            initiator: null);
        return await store.CreateAsync(conversation);
    }

    private static ChannelBinding CreateBinding(string bindingId) => new()
    {
        BindingId = BindingId.From(bindingId),
        ChannelType = ChannelKey.From("telegram"),
        AdapterId = "bot-a",
        ChannelAddress = ChannelAddress.From("chat-2088"),
        Mode = BindingMode.NotifyOnly,
        ThreadingMode = ThreadingMode.Prefix
    };

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
        public ConversationArchivedEvent? Event { get; private set; }

        public bool WasArchivedWhenConsumed { get; private set; }

        public async Task OnConversationEventAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default)
        {
            if (conversationEvent is not ConversationArchivedEvent archivedEvent)
                return;

            Event = archivedEvent;
            var persisted = await store.GetAsync(archivedEvent.ConversationId, cancellationToken);
            WasArchivedWhenConsumed = persisted?.Status == ConversationStatus.Archived;
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
