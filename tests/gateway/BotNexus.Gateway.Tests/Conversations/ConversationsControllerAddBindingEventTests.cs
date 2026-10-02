using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>Coverage for the AddBinding lifecycle event migration in issue #2088.</summary>
public sealed class ConversationsControllerAddBindingEventTests
{
    private static readonly AgentId Agent = AgentId.From("agent-2088");

    [Fact]
    public async Task AddBinding_PublishesPersistedBindingSnapshotAfterCommit_WithoutLegacyNotification()
    {
        var store = new InMemoryConversationStore();
        var conversationId = await CreateConversationAsync(store);
        var sink = new PersistedBindingSink(store);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store, publisher, notifier.Object);

        var result = await controller.AddBinding(conversationId.Value, Binding("chat-2088"), CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status201Created);
        await publisher.WaitForDrainAsync();

        var persisted = (await store.GetAsync(conversationId)).ShouldNotBeNull();
        var persistedBinding = persisted.ChannelBindings.ShouldHaveSingleItem();
        var published = sink.Event.ShouldNotBeNull();
        sink.WasPersistedWhenConsumed.ShouldBeTrue();
        published.AgentId.ShouldBe(Agent);
        published.ConversationId.ShouldBe(conversationId);
        published.Binding.ShouldBe(ConversationBindingSnapshot.From(persistedBinding));
        published.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        notifier.VerifyNoOtherCalls();

        // A published binding is a value snapshot, not the mutable store object.
        persistedBinding.ChannelAddress = ChannelAddress.From("changed-after-publication");
        published.Binding.ChannelAddress.ShouldBe(ChannelAddress.From("chat-2088"));
    }

    [Fact]
    public async Task AddBinding_WhenTransactionalAppendFails_PublishesNoSuccessEvent()
    {
        var conversationId = ConversationId.Create();
        var existingConversation = NewConversation(conversationId);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingConversation);
        store.Setup(s => s.ResolveByBindingAsync(
                Agent,
                It.IsAny<ChannelKey>(),
                It.IsAny<ChannelAddress>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);
        store.Setup(s => s.AddBindingAsync(
                conversationId,
                It.IsAny<ChannelBinding>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var controller = CreateController(store.Object, publisher);

        var result = await controller.AddBinding(conversationId.Value, Binding("append-fails"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        store.VerifyAll();
    }

    [Fact]
    public async Task AddBinding_WhenPublisherRejectsEvent_LeavesCommittedBindingIntact()
    {
        var store = new InMemoryConversationStore();
        var conversationId = await CreateConversationAsync(store);
        var controller = CreateController(store, new RejectingPublisher());

        var result = await controller.AddBinding(conversationId.Value, Binding("publisher-rejects"), CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status201Created);
        (await store.GetAsync(conversationId)).ShouldNotBeNull().ChannelBindings
            .ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("publisher-rejects"));
    }

    [Fact]
    public async Task AddBinding_WhenPublisherThrows_LeavesCommittedBindingIntact()
    {
        var store = new InMemoryConversationStore();
        var conversationId = await CreateConversationAsync(store);
        var controller = CreateController(store, new ThrowingPublisher());

        var result = await controller.AddBinding(conversationId.Value, Binding("publisher-throws"), CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status201Created);
        (await store.GetAsync(conversationId)).ShouldNotBeNull().ChannelBindings
            .ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("publisher-throws"));
    }

    [Fact]
    public async Task AddBinding_WhenAddressConflicts_PublishesNoEvent()
    {
        var store = new InMemoryConversationStore();
        var firstConversationId = await CreateConversationAsync(store);
        var secondConversationId = await CreateConversationAsync(store);
        await store.AddBindingAsync(firstConversationId, new ChannelBinding
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("already-bound"),
            BoundAt = DateTimeOffset.UtcNow
        });
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var controller = CreateController(store, publisher);

        var result = await controller.AddBinding(secondConversationId.Value, Binding("already-bound"), CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        (await store.GetAsync(secondConversationId)).ShouldNotBeNull().ChannelBindings.ShouldBeEmpty();
    }

    private static ConversationsController CreateController(
        IConversationStore store,
        IConversationEventPublisher publisher,
        params IConversationChangeNotifier[] notifiers) =>
        new(
            store,
            new InMemorySessionStore(),
            conversationChangeNotifiers: notifiers,
            conversationEventPublisher: publisher);

    private static AddBindingRequest Binding(string address) =>
        new(ChannelType: "telegram", ChannelAddress: address, Mode: "NotifyOnly", ThreadingMode: "Prefix", DisplayPrefix: "test");

    private static async Task<ConversationId> CreateConversationAsync(InMemoryConversationStore store)
    {
        var conversation = NewConversation(ConversationId.Create());
        await store.CreateAsync(conversation);
        return conversation.ConversationId;
    }

    private static Conversation NewConversation(ConversationId conversationId) => new()
    {
        ConversationId = conversationId,
        AgentId = Agent,
        Title = "binding event test",
        Status = ConversationStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class PersistedBindingSink(IConversationStore store) : IConversationEventSink
    {
        public ConversationBindingAddedEvent? Event { get; private set; }

        public bool WasPersistedWhenConsumed { get; private set; }

        public async Task OnConversationEventAsync(
            ConversationEvent conversationEvent,
            CancellationToken cancellationToken = default)
        {
            if (conversationEvent is not ConversationBindingAddedEvent added)
                return;

            Event = added;
            var persisted = await store.GetAsync(added.ConversationId, cancellationToken);
            WasPersistedWhenConsumed = persisted?.ChannelBindings.Any(binding =>
                ConversationBindingSnapshot.From(binding) == added.Binding) == true;
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
