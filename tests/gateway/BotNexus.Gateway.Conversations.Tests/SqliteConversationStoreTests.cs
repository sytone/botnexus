using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Unit tests for <see cref="SqliteConversationStore"/>.
/// </summary>
public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task CreateAndRetrieveConversation_PersistsConversationAndBindings()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(
            Agent("agent-a"),
            "First",
            CreateBinding("telegram", "12345"));

        await store.CreateAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("First");
        loaded.ChannelBindings.Count.ShouldBe(1);
        loaded.ChannelBindings[0].ChannelType.ShouldBe(ChannelKey.From("telegram"));
        loaded.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("12345"));
    }

    [Fact]
    public async Task ListAsync_FiltersByAgentId()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await store.CreateAsync(CreateConversation(Agent("agent-a"), "A1"));
        await store.CreateAsync(CreateConversation(Agent("agent-b"), "B1"));
        await store.CreateAsync(CreateConversation(Agent("agent-a"), "A2"));

        var filtered = await fixture.CreateStore().ListAsync(Agent("agent-a"));

        filtered.Count.ShouldBe(2);
        filtered.ShouldAllBe(c => c.AgentId == Agent("agent-a"));
    }

    [Fact]
    public async Task SaveAsync_UpdatesTitle()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Before");
        await store.CreateAsync(conversation);

        conversation.Title = "After";
        await store.SaveAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("After");
    }

    [Fact]
    public async Task SaveAsync_UpdatesPurpose()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Planning");
        await store.CreateAsync(conversation);

        conversation.Purpose = "Coordinate release planning";
        await store.SaveAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Purpose.ShouldBe("Coordinate release planning");
    }

    [Fact]
    public async Task ArchiveAsync_SetsStatusToArchived()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Archive me");
        await store.CreateAsync(conversation);

        await store.ArchiveAsync(conversation.ConversationId);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task ArchiveAsync_ClearsActiveSessionId()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Archive with active session");
        conversation.ActiveSessionId = SessionId.Create();
        await store.CreateAsync(conversation);

        await store.ArchiveAsync(conversation.ConversationId);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task ListAndSave_ReactivatesArchivedConversation()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var existing = CreateConversation(Agent("agent-default"), "Default-like");
        existing.IsDefault = true;
        await store.CreateAsync(existing);

        await store.ArchiveAsync(existing.ConversationId);

        var archived = (await fixture.CreateStore().ListAsync(Agent("agent-default")))
            .Single(c => c.ConversationId == existing.ConversationId);
        archived.Status.ShouldBe(ConversationStatus.Archived);
        archived.ActiveSessionId = null;
        archived.Status = ConversationStatus.Active;
        await fixture.CreateStore().SaveAsync(archived);

        var reopened = await fixture.CreateStore().GetAsync(existing.ConversationId);
        reopened.ShouldNotBeNull();

        reopened!.ConversationId.ShouldBe(existing.ConversationId);
        reopened.Status.ShouldBe(ConversationStatus.Active);
        reopened.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveByBindingAsync_FindsCorrectConversation()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var expected = CreateConversation(Agent("agent-a"), "Expected", CreateBinding("telegram", "12345"));
        await store.CreateAsync(expected);
        await store.CreateAsync(CreateConversation(Agent("agent-a"), "Other", CreateBinding("telegram", "99999")));

        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("telegram"), ChannelAddress.From("12345"), null);

        resolved.ShouldNotBeNull();
        resolved!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_WithThreadId_MatchesExactly()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var expected = CreateConversation(Agent("agent-a"), "Threaded", CreateBinding("teams", "channel-1", "thread-42"));
        await store.CreateAsync(expected);
        await store.CreateAsync(CreateConversation(Agent("agent-a"), "Other", CreateBinding("teams", "channel-1", "thread-99")));

        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1"), ThreadId.From("thread-42"));

        resolved.ShouldNotBeNull();
        resolved!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_WithNullThreadId_OnlyMatchesNullThreadBinding()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        // Conversation bound to thread-42 only — no null-thread binding
        var expected = CreateConversation(Agent("agent-a"), "Address", CreateBinding("teams", "channel-1", "thread-42"));
        await store.CreateAsync(expected);

        // Querying with null threadId should NOT match the thread-42 binding
        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1"), null);
        resolved.ShouldBeNull();

        // But querying with the actual thread-42 should match
        var resolvedWithThread = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1"), ThreadId.From("thread-42"));
        resolvedWithThread.ShouldNotBeNull();
        resolvedWithThread!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_WithNullThreadId_MatchesNullThreadBinding()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var expected = CreateConversation(Agent("agent-a"), "Address", CreateBinding("teams", "channel-1", null));
        await store.CreateAsync(expected);

        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1"), null);

        resolved.ShouldNotBeNull();
        resolved!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task GetSummariesAsync_ReturnsBindingCountCorrectly()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(
            Agent("agent-a"),
            "Summary",
            CreateBinding("telegram", "123"),
            CreateBinding("teams", "abc", "thread-1"));
        await store.CreateAsync(conversation);

        var summaries = await fixture.CreateStore().GetSummariesAsync(Agent("agent-a"));

        summaries.Count.ShouldBe(1);
        summaries[0].ConversationId.ShouldBe(conversation.ConversationId.Value);
        summaries[0].BindingCount.ShouldBe(2);
        summaries[0].Purpose.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_AddBindingAndRetrieveViaGetAsync()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Bindings", CreateBinding("telegram", "123"));
        await store.CreateAsync(conversation);

        conversation.ChannelBindings.Add(CreateBinding("teams", "abc", "thread-1"));
        await store.SaveAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.ChannelBindings.Count.ShouldBe(2);
        loaded.ChannelBindings.Any(b => b.ChannelType == ChannelKey.From("teams") && b.ThreadId == ThreadId.From("thread-1")).ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateId_Throws()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Duplicate");
        await store.CreateAsync(conversation);

        await Should.ThrowAsync<InvalidOperationException>(() => store.CreateAsync(conversation));
    }

    [Fact]
    public async Task Migration_StaleSignalRConversations_ArchivedOnStartup()
    {
        // Arrange: create conversations with titles that match the old connection-ID pattern
        // (signalr:<32 hex chars>) using a first store instance.
        using var fixture = new StoreFixture();
        var seedStore = fixture.CreateStore();

        var staleConvId1 = ConversationId.Create();
        var staleConvId2 = ConversationId.Create();
        var staleHex1 = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4"; // 32 hex chars
        var staleHex2 = "0011223344556677889900112233445566"[..32]; // another 32 hex chars

        await seedStore.CreateAsync(new Conversation
        {
            ConversationId = staleConvId1,
            AgentId = Agent("agent-a"),
            Title = $"signalr:{staleHex1}",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelBindings = []
        });
        await seedStore.CreateAsync(new Conversation
        {
            ConversationId = staleConvId2,
            AgentId = Agent("agent-b"),
            Title = $"signalr:{staleHex2}",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelBindings = []
        });

        // Act: open a fresh store — EnsureCreatedAsync will run the migration
        var freshStore = fixture.CreateStore();
        // Trigger initialization by performing any read
        await freshStore.ListAsync(null);

        // Assert: both stale conversations should now be archived
        var conv1 = await freshStore.GetAsync(staleConvId1);
        var conv2 = await freshStore.GetAsync(staleConvId2);

        conv1.ShouldNotBeNull();
        conv1!.Status.ShouldBe(ConversationStatus.Archived);
        conv2.ShouldNotBeNull();
        conv2!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task Migration_SignalRConversationWithAgentIdTitle_NotArchived()
    {
        // Arrange: a conversation with title 'signalr:nova' — agent-id style, not a hex GUID
        using var fixture = new StoreFixture();
        var seedStore = fixture.CreateStore();

        var convId = ConversationId.Create();
        await seedStore.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = Agent("test-agent"),
            Title = "signalr:nova",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelBindings = []
        });

        // Act: fresh store triggers migration
        var freshStore = fixture.CreateStore();
        await freshStore.ListAsync(null);

        // Assert: agent-ID-style title should NOT be archived
        var conv = await freshStore.GetAsync(convId);
        conv.ShouldNotBeNull();
        conv!.Status.ShouldBe(ConversationStatus.Active);
    }

    private static AgentId Agent(string id) => AgentId.From(id);

    private static string TempDb()
        => Path.Combine(Path.GetTempPath(), $"bn-conv-test-{Guid.NewGuid():N}.db");

    private static Conversation CreateConversation(AgentId agentId, string title, params ChannelBinding[] bindings)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelBindings = bindings.ToList()
        };

    private static ChannelBinding CreateBinding(string channelType, string channelAddress, string? threadId = null)
        => new()
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From(channelType),
            ChannelAddress = ChannelAddress.From(channelAddress),
            ThreadId = ThreadId.FromNullable(threadId),
            BoundAt = DateTimeOffset.UtcNow,
            Mode = BindingMode.Interactive,
            ThreadingMode = threadId is null ? ThreadingMode.Single : ThreadingMode.NativeThread
        };

    /// <summary>
    /// Disposable SQLite store fixture backed by a temporary file.
    /// </summary>
    private sealed class StoreFixture : IDisposable
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="StoreFixture"/> class.
        /// </summary>
        public StoreFixture()
        {
            DatabasePath = TempDb();
            ConnectionString = $"Data Source={DatabasePath};Pooling=False";
        }

        /// <summary>
        /// Gets the database file path.
        /// </summary>
        public string DatabasePath { get; }

        /// <summary>
        /// Gets the SQLite connection string.
        /// </summary>
        public string ConnectionString { get; }

        /// <summary>
        /// Creates a new store instance over the fixture database.
        /// </summary>
        /// <returns>A fresh <see cref="SqliteConversationStore"/>.</returns>
        public SqliteConversationStore CreateStore()
            => new(ConnectionString, NullLogger<SqliteConversationStore>.Instance);

        /// <inheritdoc />
        public void Dispose()
        {
            if (File.Exists(DatabasePath))
                File.Delete(DatabasePath);
        }
    }
}
