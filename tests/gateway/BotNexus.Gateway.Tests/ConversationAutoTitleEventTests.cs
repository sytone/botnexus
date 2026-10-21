using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>Producer-only title facts, without gateway or test-host infrastructure.</summary>
public sealed class ConversationAutoTitleEventTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task TitleSave_PublishesCommittedDetachedFact_AndLegacyNotification(bool sqlite, bool provisional)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store, "New conversation", provisional ? null : true);
        var sink = new Sink(fixture, "Generated title", provisional);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = Notifier(conversation);
        var saved = new List<Conversation>();
        var store = Wrap(fixture.Store, saved);
        var service = CreateService(store.Object, Client("Generated title"), notifier.Object, publisher);

        (await GenerateAsync(service, conversation, provisional)).ShouldBe("Generated title");
        await publisher.WaitForDrainAsync();

        sink.Errors.ShouldBeEmpty(); // The real publisher isolates sink exceptions; never hide assertion failures there.
        sink.Durable.ShouldBeTrue();
        var persisted = (await fixture.ReadAsync(conversation.ConversationId)).ShouldNotBeNull();
        var fact = sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>();
        fact.AgentId.ShouldBe(conversation.AgentId);
        fact.ConversationId.ShouldBe(conversation.ConversationId);
        fact.OccurredAt.ShouldBe(persisted.UpdatedAt);
        fact.Origin.ShouldBe(ConversationEventOrigin.None);
        fact.SessionId.ShouldBeNull();
        fact.ChangedFields.ShouldBe([nameof(Conversation.Title), nameof(Conversation.Metadata)], ignoreOrder: false);
        fact.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings), ignoreOrder: false);
        var mutable = saved.ShouldHaveSingleItem();
        mutable.ChannelBindings.ShouldHaveSingleItem().ChannelAddress = ChannelAddress.From("changed");
        mutable.ChannelBindings.Clear();
        fact.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("title-address"));
        VerifyNotifier(notifier, conversation);
    }

    [Theory]
    [InlineData(false, true, "New conversation", null, "New conversation", "Metadata")]
    [InlineData(true, true, "New conversation", null, "New conversation", "Metadata")]
    [InlineData(false, true, "New conversation", true, "Generated title", "Title")]
    [InlineData(true, true, "New conversation", true, "Generated title", "Title")]
    [InlineData(false, false, "Same title", true, "Same title", "Metadata")]
    [InlineData(true, false, "Same title", true, "Same title", "Metadata")]
    [InlineData(false, false, "New conversation", null, "Generated title", "Title")]
    [InlineData(true, false, "New conversation", null, "Generated title", "Title")]
    [InlineData(false, false, "New conversation", false, "New conversation", "Metadata")]
    [InlineData(true, false, "New conversation", false, "New conversation", "Metadata")]
    [InlineData(false, true, "New conversation", true, "New conversation", "")]
    [InlineData(true, true, "New conversation", true, "New conversation", "")]
    [InlineData(false, false, "New conversation", null, "New conversation", "")]
    [InlineData(true, false, "New conversation", null, "New conversation", "")]
    public async Task TitleSave_ReportsOnlyActualFields_StillSavesAndNotifiesOnNoOp(
        bool sqlite, bool provisional, string oldTitle, bool? flag, string newTitle, string field)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store, oldTitle, flag);
        var sink = new Sink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = Notifier(conversation);
        var saved = new List<Conversation>();
        var store = Wrap(fixture.Store, saved);
        var service = CreateService(store.Object, Client(newTitle), notifier.Object, publisher);
        (await GenerateAsync(service, conversation, provisional)).ShouldBe(newTitle);
        await publisher.WaitForDrainAsync();
        saved.Count.ShouldBe(1);
        if (field.Length == 0) sink.Events.ShouldBeEmpty();
        else sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ConversationUpdatedEvent>().ChangedFields.ShouldBe([field]);
        VerifyNotifier(notifier, conversation);
    }

    [Theory]
    [InlineData(true, "initial-custom")]
    [InlineData(false, "initial-custom")]
    [InlineData(true, "initial-missing")]
    [InlineData(false, "initial-missing")]
    [InlineData(true, "reread-custom")]
    [InlineData(false, "reread-custom")]
    [InlineData(true, "reread-missing")]
    [InlineData(false, "reread-missing")]
    [InlineData(true, "no-model")]
    [InlineData(false, "no-model")]
    [InlineData(true, "llm-throws")]
    [InlineData(false, "llm-throws")]
    [InlineData(true, "empty")]
    [InlineData(false, "empty")]
    [InlineData(true, "save-throws")]
    [InlineData(false, "save-throws")]
    public async Task TitleSave_GuardsAndFailures_EmitNeitherNotification(bool provisional, string failure)
    {
        using var fixture = new StoreFixture(true);
        var conversation = await SeedAsync(fixture.Store, "New conversation", null);
        var saved = new List<Conversation>();
        var store = Wrap(fixture.Store, saved);
        var reads = 0;
        store.Setup(s => s.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                reads++;
                var current = await fixture.Store.GetAsync(conversation.ConversationId);
                var intercept = failure.StartsWith("initial", StringComparison.Ordinal) || reads == 2;
                if (intercept && failure.EndsWith("missing", StringComparison.Ordinal)) return null;
                if (intercept && failure.EndsWith("custom", StringComparison.Ordinal) && current is not null)
                    current = current with { Title = "Human title" };
                return current;
            });
        if (failure == "save-throws")
            store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("save failed"));
        var sink = new Sink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        var client = failure == "no-model" ? new LlmClient(new ApiProviderRegistry(), new ModelRegistry())
            : Client(failure == "empty" ? "  " : "Generated title", failure == "llm-throws");
        var service = CreateService(store.Object, client, notifier.Object, publisher);
        if (failure == "save-throws")
            (await Should.ThrowAsync<InvalidOperationException>(() => GenerateAsync(service, conversation, provisional)))
                .Message.ShouldBe("save failed");
        else (await GenerateAsync(service, conversation, provisional)).ShouldBeNull();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        saved.ShouldBeEmpty();
        notifier.VerifyNoOtherCalls();
        (await fixture.ReadAsync(conversation.ConversationId)).ShouldNotBeNull().Title.ShouldBe("New conversation");
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
    public async Task TitleSave_PublisherFailure_DoesNotUndoSaveOrSuppressLegacy(bool sqlite, bool provisional, bool throws)
    {
        using var fixture = new StoreFixture(sqlite);
        var conversation = await SeedAsync(fixture.Store, "New conversation", null);
        var publisher = new FailingPublisher(throws);
        var notifier = Notifier(conversation);
        var service = CreateService(fixture.Store, Client("Generated title"), notifier.Object, publisher);
        (await GenerateAsync(service, conversation, provisional)).ShouldBe("Generated title");
        publisher.Calls.ShouldBe(1);
        var persisted = (await fixture.ReadAsync(conversation.ConversationId)).ShouldNotBeNull();
        persisted.Title.ShouldBe("Generated title");
        ConversationAutoTitleService.IsProvisionalTitle(persisted).ShouldBe(provisional);
        VerifyNotifier(notifier, conversation);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task TitleSave_PostCommitReloadMissingOrThrows_KeepsSaveAndLegacy(bool provisional, bool throws)
    {
        using var fixture = new StoreFixture(true);
        var c = await SeedAsync(fixture.Store, "New conversation", null);
        var saved = new List<Conversation>();
        var store = Wrap(fixture.Store, saved);
        store.Setup(s => s.GetAsync(c.ConversationId, It.IsAny<CancellationToken>()))
            .Returns(() => saved.Count == 0 ? fixture.Store.GetAsync(c.ConversationId)
                : throws ? Task.FromException<Conversation?>(new InvalidOperationException("reload failed"))
                : Task.FromResult<Conversation?>(null));
        var sink = new Sink();
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = Notifier(c);
        var service = CreateService(store.Object, Client("Generated title"), notifier.Object, publisher);
        (await GenerateAsync(service, c, provisional)).ShouldBe("Generated title");
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        (await fixture.ReadAsync(c.ConversationId)).ShouldNotBeNull().Title.ShouldBe("Generated title");
        VerifyNotifier(notifier, c);
    }

    private static ConversationAutoTitleService CreateService(IConversationStore store, LlmClient client,
        IConversationChangeNotifier notifier, IConversationEventPublisher publisher)
        => new(store, client, NullLogger.Instance, notifier, conversationEventPublisher: publisher);

    private static Task<string?> GenerateAsync(ConversationAutoTitleService service, Conversation conversation, bool provisional) => provisional
        ? service.GenerateProvisionalAndSaveAsync(conversation.ConversationId, conversation.AgentId, "user", null, 30, CancellationToken.None)
        : service.GenerateAndSaveAsync(conversation.ConversationId, conversation.AgentId, "user", "assistant", null, 30, CancellationToken.None);

    private static Mock<IConversationStore> Wrap(IConversationStore inner, List<Conversation> saved)
    {
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationId id, CancellationToken ct) => inner.GetAsync(id, ct));
        store.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(async (Conversation c, CancellationToken ct) => { await inner.SaveAsync(c, ct); saved.Add(c); });
        return store;
    }

    private static Mock<IConversationChangeNotifier> Notifier(Conversation c)
    {
        var notifier = new Mock<IConversationChangeNotifier>(MockBehavior.Strict);
        notifier.Setup(n => n.NotifyConversationChangedAsync("updated", c.AgentId.Value, c.ConversationId.Value,
            CancellationToken.None)).Returns(Task.CompletedTask);
        return notifier;
    }
    private static void VerifyNotifier(Mock<IConversationChangeNotifier> notifier, Conversation c)
    {
        notifier.Verify(n => n.NotifyConversationChangedAsync("updated", c.AgentId.Value, c.ConversationId.Value,
            CancellationToken.None), Times.Once);
        notifier.VerifyNoOtherCalls();
    }

    private static async Task<Conversation> SeedAsync(IConversationStore store, string title, bool? flag)
    {
        var c = new Conversation { ConversationId = ConversationId.Create(), AgentId = AgentId.From("title-agent"), Title = title };
        c.Metadata["unrelated"] = "keep";
        if (flag.HasValue) c.Metadata[ConversationAutoTitleService.ProvisionalTitleMetadataKey] = flag.Value;
        await store.CreateAsync(c);
        await store.AddBindingAsync(c.ConversationId, new ChannelBinding
        {
            BindingId = BindingId.Create(), ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("title-address"), Mode = BindingMode.NotifyOnly,
            ThreadingMode = ThreadingMode.Prefix
        });
        return (await store.GetAsync(c.ConversationId)).ShouldNotBeNull();
    }

    private sealed class Sink(StoreFixture? fixture = null, string title = "", bool provisional = false) : IConversationEventSink
    {
        public List<ConversationEvent> Events { get; } = [];
        public List<string> Errors { get; } = [];
        public bool Durable { get; private set; }
        public async Task OnConversationEventAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Events.Add(fact);
            if (fixture is null) return;
            try
            {
                var c = (await fixture.ReadAsync(fact.ConversationId)).ShouldNotBeNull();
                Durable = c.Title == title && ConversationAutoTitleService.IsProvisionalTitle(c) == provisional
                    && c.UpdatedAt == fact.OccurredAt;
                c.Metadata["unrelated"].ShouldNotBeNull().ToString().ShouldBe("keep");
            }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
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

    private static LlmClient Client(string title, bool throws = false)
    {
        var models = new ModelRegistry();
        models.Register("fake", new LlmModel("title-model", "title-model", "title-api", "fake", "https://example.invalid",
            false, ["text"], new ModelCost(0, 0, 0, 0), 4096, 512));
        var providers = new ApiProviderRegistry();
        providers.Register(new Provider(title, throws));
        return new LlmClient(providers, models);
    }
    private sealed class Provider(string title, bool throws) : IApiProvider
    {
        public string Api => "title-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null) => throw new NotSupportedException();
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            if (throws) throw new InvalidOperationException("model failed");
            var stream = new LlmStream();
            stream.Push(new DoneEvent(StopReason.Stop, new AssistantMessage([new TextContent(title)], Api, "fake", model.Id,
                new Usage(), StopReason.Stop, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            return stream;
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-title-{Guid.NewGuid():N}");
        private readonly string? _connectionString;
        public IConversationStore Store { get; }
        public StoreFixture(bool sqlite)
        {
            if (sqlite)
            {
                Directory.CreateDirectory(_directory);
                _connectionString = new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(_directory, "conversations.db"), Pooling = false }.ToString();
                Store = Sqlite();
            }
            else Store = new InMemoryConversationStore();
        }
        private SqliteConversationStore Sqlite() => new(_connectionString ?? throw new InvalidOperationException(),
            NullLogger<SqliteConversationStore>.Instance);
        public Task<Conversation?> ReadAsync(ConversationId id) => (_connectionString is null ? Store : Sqlite()).GetAsync(id);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
