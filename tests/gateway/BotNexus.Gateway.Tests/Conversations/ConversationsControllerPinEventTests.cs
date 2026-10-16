using BotNexus.Domain.Primitives;
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

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>Producer-only post-commit pin facts for the bounded REST slice of #2088.</summary>
public sealed class ConversationsControllerPinEventTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pin_PublishesDetachedCommittedSnapshot_WithoutLegacyNotification(bool sqlite, bool pin)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store, !pin);
        var before = (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull();
        var sink = new CapturingSink(fixture, pin);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);

        (await MutateAsync(controller, conversation.ConversationId, pin)).ShouldBeOfType<NoContentResult>();
        await publisher.WaitForDrainAsync();

        var persisted = (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull();
        AssertState(persisted, pin);
        persisted.UpdatedAt.ShouldBeGreaterThan(before.UpdatedAt);
        var fact = sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>();
        sink.WasDurableWhenConsumed.ShouldBeTrue();
        fact.AgentId.ShouldBe(persisted.AgentId);
        fact.ConversationId.ShouldBe(persisted.ConversationId);
        fact.OccurredAt.ShouldBe(persisted.UpdatedAt);
        fact.ChangedFields.ShouldBe([nameof(Conversation.IsPinned)], ignoreOrder: false);
        fact.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        persisted.ChannelBindings.ShouldHaveSingleItem().ChannelAddress = ChannelAddress.From("mutated-after-publication");
        persisted.ChannelBindings.Clear();
        fact.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("pin-address"));
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pin_WhenAlreadyRequestedState_PublishesNoEvent(bool sqlite, bool pin)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store, pin);
        var sink = new CapturingSink(fixture, pin);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);

        (await MutateAsync(controller, conversation.ConversationId, pin)).ShouldBeOfType<NoContentResult>();
        await publisher.WaitForDrainAsync();
        AssertState((await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull(), pin);
        sink.Events.ShouldBeEmpty();
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "write-throws")]
    [InlineData(true, "write-throws")]
    [InlineData(false, "missing-post-state")]
    [InlineData(true, "missing-post-state")]
    [InlineData(false, "unchanged-post-state")]
    [InlineData(true, "unchanged-post-state")]
    [InlineData(false, "read-throws")]
    [InlineData(true, "read-throws")]
    public async Task Pin_WhenStoreFailsOrHasNoCommittedChange_PublishesNoEvent(bool pin, string failure)
    {
        var inner = new InMemoryConversationStore();
        var conversation = await SeedAsync(inner, !pin);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        var wrote = false;
        store.Setup(s => s.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
            .Returns(() => wrote && failure == "read-throws"
                ? Task.FromException<Conversation?>(new InvalidOperationException("read failed"))
                : Task.FromResult<Conversation?>(wrote && failure == "missing-post-state" ? null : conversation));
        store.Setup(s => s.PinAsync(conversation.ConversationId, pin, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (failure == "write-throws") return Task.FromException(new InvalidOperationException("write failed"));
                wrote = true;
                return Task.CompletedTask;
            });
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store.Object, publisher, notifier.Object);

        if (failure.EndsWith("throws", StringComparison.Ordinal))
            (await Should.ThrowAsync<InvalidOperationException>(() => MutateAsync(controller, conversation.ConversationId, pin)))
                .Message.ShouldBe(failure == "write-throws" ? "write failed" : "read failed");
        else if (failure == "missing-post-state")
            (await MutateAsync(controller, conversation.ConversationId, pin)).ShouldBeOfType<NotFoundResult>();
        else
            (await MutateAsync(controller, conversation.ConversationId, pin)).ShouldBeOfType<NoContentResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        conversation.IsPinned.ShouldBe(!pin);
        notifier.VerifyNoOtherCalls();
        store.VerifyAll();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Pin_WhenPublisherRejectsOrThrows_LeavesCommittedStateIntact(bool sqlite, bool pin, bool throws)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store, !pin);
        var publisher = new FailingPublisher(throws);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);

        (await MutateAsync(controller, conversation.ConversationId, pin)).ShouldBeOfType<NoContentResult>();
        AssertState((await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull(), pin);
        publisher.Calls.ShouldBe(1);
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "hidden")]
    [InlineData(true, "hidden")]
    [InlineData(false, "read-only")]
    [InlineData(true, "read-only")]
    public async Task Pin_WhenWriteGuardRejects_DoesNotMutateOrPublish(bool pin, string state)
    {
        var inner = new InMemoryConversationStore();
        var conversation = await SeedAsync(inner, !pin,
            state == "read-only" ? ConversationVisibility.InspectableReadOnly : ConversationVisibility.InternalHidden);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(state == "missing" ? null : conversation);
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(store.Object, publisher, notifier.Object);

        var result = await MutateAsync(controller, conversation.ConversationId, pin);
        if (state == "read-only") result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(403);
        else result.ShouldBeOfType<NotFoundResult>();
        await publisher.WaitForDrainAsync();
        conversation.IsPinned.ShouldBe(!pin);
        sink.Events.ShouldBeEmpty();
        notifier.VerifyNoOtherCalls();
        store.Verify(s => s.PinAsync(It.IsAny<ConversationId>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Task<ActionResult> MutateAsync(ConversationsController controller, ConversationId id, bool pin) =>
        pin ? controller.Pin(id.Value, CancellationToken.None) : controller.Unpin(id.Value, CancellationToken.None);

    private static ConversationsController CreateController(IConversationStore store, IConversationEventPublisher publisher,
        IConversationChangeNotifier notifier) =>
        new(store, new InMemorySessionStore(), conversationChangeNotifiers: [notifier], conversationEventPublisher: publisher);

    private static void AssertState(Conversation conversation, bool pin)
    {
        conversation.IsPinned.ShouldBe(pin);
        if (pin) conversation.PinnedAt.ShouldNotBeNull();
        else conversation.PinnedAt.ShouldBeNull();
        conversation.Title.ShouldBe("unchanged title");
        conversation.ModelOverride.ShouldBe("unchanged-model");
        conversation.ThinkingOverride.ShouldBe("low");
        conversation.ContextWindowOverride.ShouldBe(2048);
        conversation.ToolOverrideJson.ShouldBe("{\"disabledTools\":[\"exec\"]}");
        conversation.CompactionTokenCeiling.ShouldBe(8192);
    }

    private static async Task<Conversation> SeedAsync(IConversationStore store, bool pin,
        ConversationVisibility visibility = ConversationVisibility.UserFacing)
    {
        var old = DateTimeOffset.UtcNow.AddDays(-1);
        var conversation = await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = AgentId.From("agent-2088-pin"),
            Title = "unchanged title", IsPinned = pin, PinnedAt = pin ? old : null,
            ModelOverride = "unchanged-model", ThinkingOverride = "low", ContextWindowOverride = 2048,
            ToolOverrideJson = "{\"disabledTools\":[\"exec\"]}", CompactionTokenCeiling = 8192,
            CreatedAt = old, UpdatedAt = old, Visibility = visibility
        });
        await store.AddBindingAsync(conversation.ConversationId, new ChannelBinding
        {
            BindingId = BindingId.Create(), ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("pin-address"), Mode = BindingMode.NotifyOnly,
            ThreadingMode = ThreadingMode.Prefix, DisplayPrefix = "pin-test"
        });
        return (await store.GetAsync(conversation.ConversationId)).ShouldNotBeNull();
    }

    private sealed class CapturingSink(StoreFixture? fixture = null, bool pin = false) : IConversationEventSink
    {
        public List<ConversationEvent> Events { get; } = [];
        public bool WasDurableWhenConsumed { get; private set; }
        public async Task OnConversationEventAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Events.Add(fact);
            if (fixture is null) return;
            var persisted = await fixture.ReadDurableAsync(fact.ConversationId);
            WasDurableWhenConsumed = persisted is not null && persisted.IsPinned == pin && persisted.UpdatedAt == fact.OccurredAt;
        }
    }

    private sealed class FailingPublisher(bool throws) : IConversationEventPublisher
    {
        public int Calls { get; private set; }
        public ValueTask<bool> PublishAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (throws) throw new InvalidOperationException("publisher failed");
            return ValueTask.FromResult(false);
        }
        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-pin-{Guid.NewGuid():N}");
        private readonly string? _connectionString;
        public IConversationStore Store { get; }
        public StoreFixture(bool sqlite)
        {
            if (sqlite)
            {
                Directory.CreateDirectory(_directory);
                _connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(_directory, "conversations.db"), Pooling = false
                }.ToString();
                Store = CreateSqliteStore(_connectionString);
            }
            else Store = new InMemoryConversationStore();
        }
        public Task<Conversation?> ReadDurableAsync(ConversationId id) =>
            (_connectionString is null ? Store : CreateSqliteStore(_connectionString)).GetAsync(id);
        private static SqliteConversationStore CreateSqliteStore(string connectionString) =>
            new(connectionString, NullLogger<SqliteConversationStore>.Instance);
        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
