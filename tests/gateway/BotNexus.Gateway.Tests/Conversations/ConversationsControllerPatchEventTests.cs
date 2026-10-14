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
/// Pins the REST metadata PATCH migration from transport-specific change notifications to the
/// channel-neutral conversation event seam (issue #2088).
/// </summary>
public sealed class ConversationsControllerPatchEventTests
{
    [Fact]
    public async Task Patch_PublishesPersistedSnapshotWithOnlySuppliedChangedFields()
    {
        var store = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(store);
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("binding-patch"),
            ChannelType = ChannelKey.From("telegram"),
            AdapterId = "bot-a",
            ChannelAddress = ChannelAddress.From("chat-2088"),
            Mode = BindingMode.NotifyOnly,
            ThreadingMode = ThreadingMode.Prefix
        };
        (await store.AddBindingAsync(conversation.ConversationId, binding)).ShouldBeTrue();

        var sink = new PersistedConversationCapturingSink(store);
        await using var publisher = new ConversationEventPublisher([sink]);
        var legacyNotifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store, publisher, [legacyNotifier.Object]);

        var result = await controller.Patch(
            conversation.ConversationId.Value,
            new PatchConversationRequest(
                Title: "Renamed",
                Purpose: conversation.Purpose,
                Instructions: "New instructions"),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        await publisher.WaitForDrainAsync();

        var persisted = await store.GetAsync(conversation.ConversationId);
        persisted.ShouldNotBeNull();
        sink.Event.ShouldNotBeNull();
        sink.WasDurableWhenConsumed.ShouldBeTrue();
        sink.Event.ConversationId.ShouldBe(persisted.ConversationId);
        sink.Event.AgentId.ShouldBe(persisted.AgentId);
        sink.Event.OccurredAt.ShouldBe(persisted.UpdatedAt);
        sink.Event.ChangedFields.ShouldBe([nameof(Conversation.Title), nameof(Conversation.Instructions)], ignoreOrder: false);
        sink.Event.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        legacyNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Patch_WhenStoreMutationFails_PublishesNoUpdatedEvent()
    {
        var inner = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(inner);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
            .Returns((ConversationId _, CancellationToken cancellationToken) => inner.GetAsync(conversation.ConversationId, cancellationToken));
        store.Setup(s => s.PatchMetadataAsync(
                conversation.ConversationId,
                It.IsAny<ConversationMetadataPatch>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store failed"));
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var controller = CreateController(store.Object, publisher);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => controller.Patch(
            conversation.ConversationId.Value,
            new PatchConversationRequest(Title: "Never persisted"),
            CancellationToken.None));

        exception.Message.ShouldBe("store failed");
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Patch_WhenPublisherRejectsEvent_LeavesCommittedMetadataIntact()
    {
        var store = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(store);
        var controller = CreateController(store, new RejectingPublisher());

        var result = await controller.Patch(
            conversation.ConversationId.Value,
            new PatchConversationRequest(Title: "Committed despite rejection"),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var persisted = await store.GetAsync(conversation.ConversationId);
        persisted.ShouldNotBeNull();
        persisted.Title.ShouldBe("Committed despite rejection");
    }

    [Fact]
    public async Task Patch_WhenPublisherThrows_LeavesCommittedMetadataIntact()
    {
        var store = new InMemoryConversationStore();
        var conversation = await CreateConversationAsync(store);
        var controller = CreateController(store, new ThrowingPublisher());

        var result = await controller.Patch(
            conversation.ConversationId.Value,
            new PatchConversationRequest(Purpose: "Committed despite exception"),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var persisted = await store.GetAsync(conversation.ConversationId);
        persisted.ShouldNotBeNull();
        persisted.Purpose.ShouldBe("Committed despite exception");
    }

    private static async Task<Conversation> CreateConversationAsync(InMemoryConversationStore store)
    {
        var conversation = ConversationFactory.CreateForChannel(
            ConversationId.Create(),
            AgentId.From("agent-2088"),
            title: "Original title",
            initiator: null,
            purpose: "Existing purpose",
            instructions: "Existing instructions");
        return await store.CreateAsync(conversation);
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
        public ConversationUpdatedEvent? Event { get; private set; }

        public bool WasDurableWhenConsumed { get; private set; }

        public async Task OnConversationEventAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default)
        {
            if (conversationEvent is not ConversationUpdatedEvent updatedEvent)
                return;

            Event = updatedEvent;
            var persisted = await store.GetAsync(updatedEvent.ConversationId, cancellationToken);
            WasDurableWhenConsumed = persisted is not null
                && persisted.Title == "Renamed"
                && persisted.Instructions == "New instructions";
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
