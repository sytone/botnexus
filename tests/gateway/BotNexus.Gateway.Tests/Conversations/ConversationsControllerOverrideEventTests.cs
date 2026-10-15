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

/// <summary>Post-commit REST override facts for the bounded override slice of #2088.</summary>
public sealed class ConversationsControllerOverrideEventTests
{
    private const string Tools = "{\"disabledTools\":[\"exec\"]}";

    [Theory]
    [InlineData(false, "set")]
    [InlineData(true, "set")]
    [InlineData(false, "tools")]
    [InlineData(true, "tools")]
    [InlineData(false, "clear")]
    [InlineData(true, "clear")]
    public async Task Override_PublishesDetachedCommittedSnapshotAndRetainsAudit_WithoutLegacyNotification(bool sqlite, string operation)
    {
        using var fixture = new StoreFixture(sqlite);
        var store = fixture.Store;
        var conversation = await SeedAsync(store);
        var sink = new CapturingSink(store, operation);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var entries = new List<ConversationAuditEntry>();
        var audit = new Mock<IConversationAuditLog>(MockBehavior.Strict);
        audit.Setup(a => a.LogAsync(It.IsAny<ConversationAuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationAuditEntry, CancellationToken>((entry, _) => entries.Add(entry))
            .Returns(Task.CompletedTask);
        var controller = CreateController(store, publisher, notifier.Object, audit.Object);

        (await MutateAsync(controller, conversation.ConversationId, operation)).ShouldBeOfType<OkObjectResult>();
        await publisher.WaitForDrainAsync();

        var persisted = (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull();
        AssertOverrides(persisted, operation);
        persisted.Title.ShouldBe("unchanged title");
        persisted.IsPinned.ShouldBeTrue();
        var fact = sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>();
        sink.WasDurableWhenConsumed.ShouldBeTrue();
        fact.AgentId.ShouldBe(persisted.AgentId);
        fact.ConversationId.ShouldBe(persisted.ConversationId);
        fact.OccurredAt.ShouldBe(persisted.UpdatedAt);
        var fields = new List<string> { nameof(Conversation.ModelOverride), nameof(Conversation.ThinkingOverride), nameof(Conversation.ContextWindowOverride) };
        if (operation != "set") fields.Add(nameof(Conversation.ToolOverrideJson));
        fact.ChangedFields.ShouldBe(fields, ignoreOrder: false);
        fact.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        var binding = persisted.ChannelBindings.ShouldHaveSingleItem();
        binding.ChannelAddress = ChannelAddress.From("mutated-after-publication");
        persisted.ChannelBindings.Clear();
        fact.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("override-address"));
        notifier.VerifyNoOtherCalls();
        entries.Select(e => e.Action).ShouldBe(operation == "clear"
            ? ["model_override_cleared"]
            : operation == "tools"
                ? ["model_override_set", "thinking_override_set", "context_override_set", "tool_override_set"]
                : new[] { "model_override_set", "thinking_override_set", "context_override_set" }, ignoreOrder: false);
        entries.ShouldAllBe(e => e.ConversationId == conversation.ConversationId.Value && e.Actor == "api" && e.Source == "rest-api");
        entries[0].PreviousValue.ShouldBe("old-model");
        entries[0].NewValue.ShouldBe(operation == "clear" ? null : "new-model");
    }

    [Theory]
    [InlineData("set", false)]
    [InlineData("clear", false)]
    [InlineData("set", true)]
    [InlineData("clear", true)]
    public async Task Override_WhenStoreReturnsNullOrThrows_PublishesNoEvent(string operation, bool throws)
    {
        var inner = new InMemoryConversationStore();
        var conversation = await SeedAsync(inner);
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        var mutation = store.Setup(s => s.PatchOverrideAsync(conversation.ConversationId,
            It.IsAny<ConversationOverridePatch>(), It.IsAny<CancellationToken>()));
        if (throws) mutation.ThrowsAsync(new InvalidOperationException("store failed"));
        else mutation.ReturnsAsync((Conversation?)null);
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var audit = new Mock<IConversationAuditLog>(MockBehavior.Strict);
        var controller = CreateController(store.Object, publisher, notifier.Object, audit.Object);

        if (throws)
            (await Should.ThrowAsync<InvalidOperationException>(() => MutateAsync(controller, conversation.ConversationId, operation)))
                .Message.ShouldBe("store failed");
        else
            (await MutateAsync(controller, conversation.ConversationId, operation)).ShouldBeOfType<NotFoundResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        conversation.ModelOverride.ShouldBe("old-model");
        notifier.VerifyNoOtherCalls();
        audit.VerifyNoOtherCalls();
        store.VerifyAll();
    }

    [Theory]
    [InlineData(false, "set", false)]
    [InlineData(true, "set", false)]
    [InlineData(false, "clear", false)]
    [InlineData(true, "clear", false)]
    [InlineData(false, "set", true)]
    [InlineData(true, "set", true)]
    [InlineData(false, "clear", true)]
    [InlineData(true, "clear", true)]
    public async Task Override_WhenPublisherRejectsOrThrows_LeavesCommittedStateIntact(bool sqlite, string operation, bool throws)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store);
        var publisher = new FailingPublisher(throws);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);

        (await MutateAsync(controller, conversation.ConversationId, operation)).ShouldBeOfType<OkObjectResult>();
        AssertOverrides((await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull(), operation);
        publisher.Calls.ShouldBe(1);
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "tools-only")]
    [InlineData(true, "tools-only")]
    [InlineData(false, "clear-tools")]
    [InlineData(true, "clear-tools")]
    [InlineData(false, "clear-empty")]
    [InlineData(true, "clear-empty")]
    public async Task Override_ChangedFieldsIncludesOnlyChangedPatchedValues(bool sqlite, string operation)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store);
        if (operation.StartsWith("clear", StringComparison.Ordinal))
        {
            await fixture.Store.PatchOverrideAsync(conversation.ConversationId, new ConversationOverridePatch
            {
                Model = FieldUpdate<string?>.Set(null), Thinking = FieldUpdate<string?>.Set(null),
                ContextWindow = FieldUpdate<int?>.Set(null),
                ToolOverrideJson = FieldUpdate<string?>.Set(operation == "clear-empty" ? null : Tools)
            });
        }
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);
        ActionResult result = operation.StartsWith("clear", StringComparison.Ordinal)
            ? await controller.ClearOverride(conversation.ConversationId.Value, CancellationToken.None)
            : await controller.SetOverride(conversation.ConversationId.Value,
                new(Model: " old-model ", Thinking: "low", ContextWindow: 2048,
                    ToolOverrideJson: operation == "unchanged" ? "ignored invalid JSON" : null,
                    ApplyToolOverride: operation == "tools-only"), CancellationToken.None);
        result.ShouldBeOfType<OkObjectResult>();
        await publisher.WaitForDrainAsync();
        if (operation is "unchanged" or "clear-empty") sink.Events.ShouldBeEmpty();
        else sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>().ChangedFields
            .ShouldBe([nameof(Conversation.ToolOverrideJson)], ignoreOrder: false);
        (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull().ToolOverrideJson
            .ShouldBe(operation == "unchanged" ? Tools : null);
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "set")]
    [InlineData(true, "set")]
    [InlineData(false, "reset")]
    [InlineData(true, "reset")]
    [InlineData(false, "preserve")]
    [InlineData(true, "preserve")]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "clear")]
    [InlineData(true, "clear")]
    [InlineData(false, "clear-empty")]
    [InlineData(true, "clear-empty")]
    [InlineData(false, "invalid-zero")]
    [InlineData(true, "invalid-zero")]
    [InlineData(false, "invalid-negative")]
    [InlineData(true, "invalid-negative")]
    [InlineData(false, "invalid-request-positive")]
    [InlineData(true, "invalid-request-positive")]
    public async Task Override_CompactionCeilingOnly_ReportsOnlyChangedPatchedCeiling(bool sqlite, string operation)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store);
        var clearing = operation.StartsWith("clear", StringComparison.Ordinal);
        int? initialCeiling = operation is "set" or "clear-empty" ? null : 8192;
        await fixture.Store.PatchOverrideAsync(conversation.ConversationId, new ConversationOverridePatch
        {
            Model = FieldUpdate<string?>.Set(clearing ? null : "old-model"),
            Thinking = FieldUpdate<string?>.Set(clearing ? null : "low"),
            ContextWindow = FieldUpdate<int?>.Set(clearing ? null : 2048),
            ToolOverrideJson = FieldUpdate<string?>.Set(clearing ? null : Tools),
            CompactionTokenCeiling = FieldUpdate<int?>.Set(initialCeiling)
        });
        var before = (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull();
        var sink = new CapturingSink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);
        int? requestedCeiling = operation switch
        {
            "reset" => null,
            "invalid-zero" => 0,
            "invalid-negative" => -1,
            "unchanged" => initialCeiling,
            _ => 16384
        };
        var result = clearing
            ? await controller.ClearOverride(conversation.ConversationId.Value, CancellationToken.None)
            : await controller.SetOverride(conversation.ConversationId.Value,
                new(Model: " old-model ", Thinking: operation == "invalid-request-positive" ? "invalid" : "low",
                    ContextWindow: 2048, ToolOverrideJson: "ignored invalid JSON", ApplyToolOverride: false,
                    CompactionTokenCeiling: requestedCeiling, ApplyCompactionTokenCeiling: operation != "preserve"),
                CancellationToken.None);
        var invalid = operation.StartsWith("invalid", StringComparison.Ordinal);
        if (invalid) result.ShouldBeOfType<BadRequestObjectResult>();
        else result.ShouldBeOfType<OkObjectResult>();
        await publisher.WaitForDrainAsync();

        var persisted = (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull();
        var changed = operation is "set" or "reset" or "clear";
        persisted.CompactionTokenCeiling.ShouldBe(changed ? (operation == "set" ? 16384 : null) : initialCeiling);
        persisted.ModelOverride.ShouldBe(before.ModelOverride);
        persisted.ThinkingOverride.ShouldBe(before.ThinkingOverride);
        persisted.ContextWindowOverride.ShouldBe(before.ContextWindowOverride);
        persisted.ToolOverrideJson.ShouldBe(before.ToolOverrideJson);
        persisted.Title.ShouldBe(before.Title);
        persisted.IsPinned.ShouldBe(before.IsPinned);
        if (invalid) persisted.UpdatedAt.ShouldBe(before.UpdatedAt);
        if (changed)
        {
            var fact = sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>();
            fact.ChangedFields.ShouldBe([nameof(Conversation.CompactionTokenCeiling)], ignoreOrder: false);
            fact.AgentId.ShouldBe(persisted.AgentId);
            fact.ConversationId.ShouldBe(persisted.ConversationId);
            fact.OccurredAt.ShouldBe(persisted.UpdatedAt);
            fact.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
            persisted.ChannelBindings.ShouldHaveSingleItem().ChannelAddress = ChannelAddress.From("changed-after-ceiling-fact");
            persisted.ChannelBindings.Clear();
            fact.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("override-address"));
        }
        else sink.Events.ShouldBeEmpty();
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "set")]
    [InlineData(true, "set")]
    [InlineData(false, "clear")]
    [InlineData(true, "clear")]
    public async Task Override_AllFiveFieldsChanged_ReportsStableOrder(bool sqlite, string operation)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store);
        await fixture.Store.PatchOverrideAsync(conversation.ConversationId, new ConversationOverridePatch
        {
            CompactionTokenCeiling = FieldUpdate<int?>.Set(8192)
        });
        var sink = new CapturingSink(fixture.Store, operation == "clear" ? "clear" : "tools");
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var controller = CreateController(fixture.Store, publisher, notifier.Object);
        var result = operation == "clear"
            ? await controller.ClearOverride(conversation.ConversationId.Value, CancellationToken.None)
            : await controller.SetOverride(conversation.ConversationId.Value,
                new(Model: "new-model", Thinking: "high", ContextWindow: 4096,
                    ToolOverrideJson: null, ApplyToolOverride: true,
                    CompactionTokenCeiling: 16384, ApplyCompactionTokenCeiling: true), CancellationToken.None);
        result.ShouldBeOfType<OkObjectResult>();
        await publisher.WaitForDrainAsync();
        var persisted = (await fixture.ReadDurableAsync(conversation.ConversationId)).ShouldNotBeNull();
        AssertOverrides(persisted, operation == "clear" ? "clear" : "tools");
        persisted.CompactionTokenCeiling.ShouldBe(operation == "clear" ? null : 16384);
        sink.WasDurableWhenConsumed.ShouldBeTrue();
        sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>().ChangedFields.ShouldBe(
            [nameof(Conversation.ModelOverride), nameof(Conversation.ThinkingOverride), nameof(Conversation.ContextWindowOverride),
                nameof(Conversation.ToolOverrideJson), nameof(Conversation.CompactionTokenCeiling)], ignoreOrder: false);
        notifier.VerifyNoOtherCalls();
    }

    private static Task<ActionResult> MutateAsync(ConversationsController controller, ConversationId id, string operation) =>
        operation == "clear" ? controller.ClearOverride(id.Value, CancellationToken.None)
            : controller.SetOverride(id.Value, new(Model: " new-model ", Thinking: "high", ContextWindow: 4096,
                ToolOverrideJson: null, ApplyToolOverride: operation == "tools"), CancellationToken.None);

    private static void AssertOverrides(Conversation conversation, string operation)
    {
        conversation.ModelOverride.ShouldBe(operation == "clear" ? null : "new-model");
        conversation.ThinkingOverride.ShouldBe(operation == "clear" ? null : "high");
        conversation.ContextWindowOverride.ShouldBe(operation == "clear" ? null : 4096);
        conversation.ToolOverrideJson.ShouldBe(operation == "set" ? Tools : null);
    }

    private static ConversationsController CreateController(IConversationStore store, IConversationEventPublisher publisher,
        IConversationChangeNotifier notifier, IConversationAuditLog? audit = null) =>
        new(store, new InMemorySessionStore(), auditLog: audit, conversationChangeNotifiers: [notifier], conversationEventPublisher: publisher);

    private static async Task<Conversation> SeedAsync(IConversationStore store)
    {
        var conversation = await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = AgentId.From("agent-2088-overrides"),
            Title = "unchanged title", IsPinned = true, ModelOverride = "old-model", ThinkingOverride = "low",
            ContextWindowOverride = 2048, ToolOverrideJson = Tools,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await store.AddBindingAsync(conversation.ConversationId, new ChannelBinding
        {
            BindingId = BindingId.Create(), ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("override-address"), Mode = BindingMode.NotifyOnly,
            ThreadingMode = ThreadingMode.Prefix, DisplayPrefix = "override-test"
        });
        return (await store.GetAsync(conversation.ConversationId)).ShouldNotBeNull();
    }

    private sealed class CapturingSink(IConversationStore? store = null, string? operation = null) : IConversationEventSink
    {
        public List<ConversationEvent> Events { get; } = [];
        public bool WasDurableWhenConsumed { get; private set; }
        public async Task OnConversationEventAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Events.Add(fact);
            if (store is null) return;
            var persisted = await store.GetAsync(fact.ConversationId, cancellationToken);
            WasDurableWhenConsumed = persisted is not null
                && persisted.ModelOverride == (operation == "clear" ? null : "new-model")
                && persisted.ThinkingOverride == (operation == "clear" ? null : "high")
                && persisted.ContextWindowOverride == (operation == "clear" ? null : 4096)
                && persisted.ToolOverrideJson == (operation == "set" ? Tools : null);
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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-override-{Guid.NewGuid():N}");
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
