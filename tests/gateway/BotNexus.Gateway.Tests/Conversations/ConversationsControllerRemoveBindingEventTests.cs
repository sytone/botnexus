using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>Post-commit REST binding-removal facts for issue #2088.</summary>
public sealed class ConversationsControllerRemoveBindingEventTests
{
    private static readonly AgentId Agent = AgentId.From("agent-2088-removal");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveBinding_PublishesDetachedBindingAndCommittedRemainingSnapshot_WithoutLegacyNotification(bool sqlite)
    {
        using var fixture = new RemovalStoreFixture(sqlite);
        var store = fixture.Store;
        var id = await CreateConversationAsync(store);
        var removed = NewBinding("removed");
        var retained = NewBinding("retained");
        await store.AddBindingAsync(id, removed);
        await store.AddBindingAsync(id, retained);
        var sink = new PersistedRemovalSink(store);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store, publisher, notifier.Object);

        var result = await controller.RemoveBinding(id.Value, removed.BindingId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await publisher.WaitForDrainAsync();
        var persisted = (await store.GetAsync(id)).ShouldNotBeNull();
        var remaining = persisted.ChannelBindings.ShouldHaveSingleItem();
        remaining.BindingId.ShouldBe(retained.BindingId);
        var fact = sink.Event.ShouldNotBeNull();
        sink.EventCount.ShouldBe(1);
        sink.WasRemovedWhenConsumed.ShouldBeTrue();
        fact.AgentId.ShouldBe(Agent);
        fact.ConversationId.ShouldBe(id);
        fact.Binding.ShouldBe(ConversationBindingSnapshot.From(removed));
        fact.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        fact.OccurredAt.ShouldBe(persisted.UpdatedAt);
        notifier.VerifyNoOtherCalls();
        // A fresh SQLite store proves durable state, not just the controller's cache.
        var reopened = (await fixture.ReadDurableAsync(id)).ShouldNotBeNull();
        reopened.ChannelBindings.ShouldHaveSingleItem().BindingId.ShouldBe(retained.BindingId);
        removed.ChannelAddress = ChannelAddress.From("mutated-after-publication");
        fact.Binding.ChannelAddress.ShouldBe(ChannelAddress.From("removed"));
        remaining.ChannelAddress = ChannelAddress.From("mutated-remaining");
        persisted.ChannelBindings.Clear();
        fact.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("retained"));
    }

    [Fact]
    public async Task RemoveBinding_WhenStoreRejectsMutation_PublishesNoSuccessEventAndKeepsBinding()
    {
        var id = ConversationId.Create();
        var binding = NewBinding("unchanged");
        var conversation = NewConversation(id);
        conversation.ChannelBindings.Add(binding);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        store.Setup(s => s.RemoveBindingAsync(id, binding.BindingId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store.Object, publisher, notifier.Object);

        var result = await controller.RemoveBinding(id.Value, binding.BindingId.Value, CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        conversation.ChannelBindings.ShouldHaveSingleItem().BindingId.ShouldBe(binding.BindingId);
        notifier.VerifyNoOtherCalls();
        store.VerifyAll();
    }

    [Fact]
    public async Task RemoveBinding_WhenStoreThrows_PublishesNoSuccessEventAndKeepsBinding()
    {
        var id = ConversationId.Create();
        var binding = NewBinding("unchanged");
        var conversation = NewConversation(id);
        conversation.ChannelBindings.Add(binding);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        store.Setup(s => s.RemoveBindingAsync(id, binding.BindingId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store failed"));
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store.Object, publisher, notifier.Object);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            controller.RemoveBinding(id.Value, binding.BindingId.Value, CancellationToken.None));

        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        conversation.ChannelBindings.ShouldHaveSingleItem().BindingId.ShouldBe(binding.BindingId);
        notifier.VerifyNoOtherCalls();
        store.VerifyAll();
    }

    [Fact]
    public async Task RemoveBinding_WhenPublisherRejects_LeavesCommittedRemovalIntact()
    {
        var store = new InMemoryConversationStore();
        var id = await CreateConversationAsync(store);
        var binding = NewBinding("reject");
        await store.AddBindingAsync(id, binding);
        var publisher = new RejectingPublisher();
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store, publisher, notifier.Object);

        (await controller.RemoveBinding(id.Value, binding.BindingId.Value, CancellationToken.None))
            .ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(id)).ShouldNotBeNull().ChannelBindings.ShouldBeEmpty();
        publisher.Calls.ShouldBe(1);
        notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveBinding_WhenPublisherThrows_LeavesCommittedRemovalIntact()
    {
        var store = new InMemoryConversationStore();
        var id = await CreateConversationAsync(store);
        var binding = NewBinding("throws");
        await store.AddBindingAsync(id, binding);
        var publisher = new ThrowingPublisher();
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store, publisher, notifier.Object);

        (await controller.RemoveBinding(id.Value, binding.BindingId.Value, CancellationToken.None))
            .ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(id)).ShouldNotBeNull().ChannelBindings.ShouldBeEmpty();
        publisher.Calls.ShouldBe(1);
        notifier.VerifyNoOtherCalls();
    }

    private static ConversationsController CreateController(
        IConversationStore store, IConversationEventPublisher publisher,
        params IConversationChangeNotifier[] notifiers) =>
        new(store, new InMemorySessionStore(), conversationChangeNotifiers: notifiers,
            conversationEventPublisher: publisher);

    private static ChannelBinding NewBinding(string address) => new()
    {
        BindingId = BindingId.Create(),
        ChannelType = ChannelKey.From("telegram"),
        ChannelAddress = ChannelAddress.From(address),
        BoundAt = DateTimeOffset.UtcNow,
        Mode = BindingMode.NotifyOnly,
        ThreadingMode = ThreadingMode.Prefix,
        DisplayPrefix = "removal-test"
    };

    private static async Task<ConversationId> CreateConversationAsync(IConversationStore store)
    {
        var conversation = NewConversation(ConversationId.Create());
        await store.CreateAsync(conversation);
        return conversation.ConversationId;
    }

    private static Conversation NewConversation(ConversationId id) => new()
    {
        ConversationId = id,
        AgentId = Agent,
        Title = "binding removal event test",
        Status = ConversationStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class RemovalStoreFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-removal-{Guid.NewGuid():N}");
        private readonly string? _connectionString;
        public IConversationStore Store { get; }

        public RemovalStoreFixture(bool sqlite)
        {
            if (sqlite)
            {
                Directory.CreateDirectory(_directory);
                _connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(_directory, "conversations.db"),
                    Pooling = false
                }.ToString();
                Store = CreateSqliteStore(_connectionString);
            }
            else
                Store = new InMemoryConversationStore();
        }

        public Task<Conversation?> ReadDurableAsync(ConversationId id) =>
            (_connectionString is null ? Store : CreateSqliteStore(_connectionString)).GetAsync(id);

        private static SqliteConversationStore CreateSqliteStore(string connectionString) =>
            new(connectionString, NullLogger<SqliteConversationStore>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class PersistedRemovalSink(IConversationStore store) : IConversationEventSink
    {
        public ConversationBindingRemovedEvent? Event { get; private set; }
        public bool WasRemovedWhenConsumed { get; private set; }
        public int EventCount { get; private set; }

        public async Task OnConversationEventAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            if (fact is not ConversationBindingRemovedEvent removed)
                return;
            EventCount++;
            Event = removed;
            var persisted = await store.GetAsync(removed.ConversationId, cancellationToken);
            WasRemovedWhenConsumed = persisted is not null &&
                persisted.ChannelBindings.All(b => b.BindingId != removed.Binding.BindingId);
        }
    }

    private sealed class CapturingSink : IConversationEventSink
    {
        public List<ConversationEvent> Events { get; } = [];
        public Task OnConversationEventAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Events.Add(fact);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingPublisher : IConversationEventPublisher
    {
        public int Calls { get; private set; }
        public ValueTask<bool> PublishAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(false);
        }
        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingPublisher : IConversationEventPublisher
    {
        public int Calls { get; private set; }
        public ValueTask<bool> PublishAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("publisher failed");
        }
        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
