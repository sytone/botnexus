using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;
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

        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("telegram"), ChannelAddress.From("12345"));

        resolved.ShouldNotBeNull();
        resolved!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_WithCompositeAddress_MatchesExactly()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var expected = CreateConversation(Agent("agent-a"), "Threaded", CreateBinding("teams", "channel-1/topic:thread-42"));
        await store.CreateAsync(expected);
        await store.CreateAsync(CreateConversation(Agent("agent-a"), "Other", CreateBinding("teams", "channel-1/topic:thread-99")));

        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1/topic:thread-42"));

        resolved.ShouldNotBeNull();
        resolved!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_BareAddress_DoesNotMatchCompositeAddress()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        // Conversation bound to channel-1/topic:thread-42 only — no bare-address binding
        var expected = CreateConversation(Agent("agent-a"), "Topic-only", CreateBinding("teams", "channel-1/topic:thread-42"));
        await store.CreateAsync(expected);

        // Querying with the bare chat address should NOT match the composite binding
        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1"));
        resolved.ShouldBeNull();

        // But querying with the composite address should match
        var resolvedWithComposite = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1/topic:thread-42"));
        resolvedWithComposite.ShouldNotBeNull();
        resolvedWithComposite!.ConversationId.ShouldBe(expected.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_BareAddress_MatchesBareAddressBinding()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var expected = CreateConversation(Agent("agent-a"), "Bare", CreateBinding("teams", "channel-1"));
        await store.CreateAsync(expected);

        var resolved = await fixture.CreateStore().ResolveByBindingAsync(Agent("agent-a"), ChannelKey.From("teams"), ChannelAddress.From("channel-1"));

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
            CreateBinding("teams", "abc/topic:thread-1"));
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

        conversation.ChannelBindings.Add(CreateBinding("teams", "abc/topic:thread-1"));
        await store.SaveAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.ChannelBindings.Count.ShouldBe(2);
        loaded.ChannelBindings.Any(b => b.ChannelType == ChannelKey.From("teams") && b.ChannelAddress == ChannelAddress.From("abc/topic:thread-1")).ShouldBeTrue();
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

    // ── Initiator + ListForCitizenAsync ────────────────────────────────────────

    [Fact]
    public async Task Initiator_RoundTrips_Null_User_And_Agent()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var alice = CitizenId.Of(UserId.From("alice"));
        var helper = CitizenId.Of(Agent("helper"));

        var noInit = CreateConversation(Agent("agent-a"), "no-init");
        var userInit = CreateConversation(Agent("agent-a"), "user-init");
        userInit.Initiator = alice;
        var agentInit = CreateConversation(Agent("agent-b"), "agent-init");
        agentInit.Initiator = helper;

        await store.CreateAsync(noInit);
        await store.CreateAsync(userInit);
        await store.CreateAsync(agentInit);

        var fresh = fixture.CreateStore();
        (await fresh.GetAsync(noInit.ConversationId))!.Initiator.ShouldBeNull();
        (await fresh.GetAsync(userInit.ConversationId))!.Initiator!.Value.ShouldBe(alice);
        (await fresh.GetAsync(agentInit.ConversationId))!.Initiator!.Value.ShouldBe(helper);
    }

    [Fact]
    public async Task Kind_RoundTrips_AllValues_AcrossProcessRestart()
    {
        // Regression: Conversation.Kind was added for Phase 4 / F-3 but the original SqliteConversationStore
        // did not include it in the INSERT/SELECT SQL or in CloneConversation. Every kind was silently
        // demoted to HumanAgent on the first cache hit and again on every restart. This pin catches a
        // regression of either path: an in-process GetAsync (clone), and a fresh-store reload (SQL).
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var human = CreateConversation(Agent("agent-a"), "human");
        human.Kind = ConversationKind.HumanAgent;
        var agentAgent = CreateConversation(Agent("agent-b"), "agent-agent");
        agentAgent.Kind = ConversationKind.AgentAgent;
        var subAgent = CreateConversation(Agent("agent-c"), "sub-agent");
        subAgent.Kind = ConversationKind.AgentSubAgent;

        await store.CreateAsync(human);
        await store.CreateAsync(agentAgent);
        await store.CreateAsync(subAgent);

        // In-process: validates CloneConversation copies Kind through the cache.
        (await store.GetAsync(human.ConversationId))!.Kind.ShouldBe(ConversationKind.HumanAgent);
        (await store.GetAsync(agentAgent.ConversationId))!.Kind.ShouldBe(ConversationKind.AgentAgent,
            customMessage: "CloneConversation must copy Kind. If it returns HumanAgent here, the discriminator " +
                "is lost on every in-process GetAsync after the first.");
        (await store.GetAsync(subAgent.ConversationId))!.Kind.ShouldBe(ConversationKind.AgentSubAgent);

        // Fresh store: validates the SQL writes and reads back through LoadConversationAsync.
        var fresh = fixture.CreateStore();
        (await fresh.GetAsync(human.ConversationId))!.Kind.ShouldBe(ConversationKind.HumanAgent);
        (await fresh.GetAsync(agentAgent.ConversationId))!.Kind.ShouldBe(ConversationKind.AgentAgent,
            customMessage: "SQLite schema must store Kind. If it returns HumanAgent here, the discriminator is " +
                "lost on every gateway restart -- Phase 4 / F-3 contract is broken in production.");
        (await fresh.GetAsync(subAgent.ConversationId))!.Kind.ShouldBe(ConversationKind.AgentSubAgent);
    }

    [Fact]
    public async Task ListForCitizenAsync_Throws_OnInvalidCitizen()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await Should.ThrowAsync<ArgumentException>(() => store.ListForCitizenAsync(default));
    }

    [Fact]
    public async Task ListForCitizenAsync_User_ReturnsOnly_ConversationsTheyInitiated()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var alice = CitizenId.Of(UserId.From("alice"));
        var bob = CitizenId.Of(UserId.From("bob"));

        var aliceConv = CreateConversation(Agent("a1"), "alice-conv");
        aliceConv.Initiator = alice;
        var bobConv = CreateConversation(Agent("a1"), "bob-conv");
        bobConv.Initiator = bob;
        var noInit = CreateConversation(Agent("a1"), "no-init");

        await store.CreateAsync(aliceConv);
        await store.CreateAsync(bobConv);
        await store.CreateAsync(noInit);

        var fresh = fixture.CreateStore();
        var aliceList = await fresh.ListForCitizenAsync(alice);
        aliceList.Select(c => c.ConversationId).ShouldBe(new[] { aliceConv.ConversationId }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListForCitizenAsync_Agent_ReturnsUnion_OfInitiatedAndOwned()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var helper = Agent("helper");
        var other = Agent("other");
        var alice = CitizenId.Of(UserId.From("alice"));
        var helperCitizen = CitizenId.Of(helper);

        var owned = CreateConversation(helper, "owned-by-helper");
        owned.Initiator = alice;
        var initiatedByHelper = CreateConversation(other, "initiated-by-helper");
        initiatedByHelper.Initiator = helperCitizen;
        var unrelated = CreateConversation(other, "unrelated");
        unrelated.Initiator = alice;

        await store.CreateAsync(owned);
        await store.CreateAsync(initiatedByHelper);
        await store.CreateAsync(unrelated);

        var fresh = fixture.CreateStore();
        var list = await fresh.ListForCitizenAsync(helperCitizen);

        list.Select(c => c.ConversationId).ShouldBe(
            new[] { owned.ConversationId, initiatedByHelper.ConversationId },
            ignoreOrder: true);
    }

    [Fact]
    public async Task ListForCitizenAsync_IncludesArchivedConversations()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var alice = CitizenId.Of(UserId.From("alice"));

        var conv = CreateConversation(Agent("a1"), "archived");
        conv.Initiator = alice;
        await store.CreateAsync(conv);
        await store.ArchiveAsync(conv.ConversationId);

        var list = await fixture.CreateStore().ListForCitizenAsync(alice);
        list.Count.ShouldBe(1);
        list[0].Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task Migration_AddsInitiatorColumn_ToPreExistingDatabase_WithoutDataLoss()
    {
        // Seed a database with the schema as it existed before the initiator column was added
        // (the canvas_html column was the prior-most migration). The store must add the column
        // on first access and not lose any existing conversation rows.
        using var fixture = new StoreFixture();

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE conversations (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    purpose TEXT,
                    is_default INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Active',
                    active_session_id TEXT,
                    metadata TEXT NOT NULL DEFAULT '{}',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    instructions TEXT,
                    canvas_html TEXT
                );

                CREATE TABLE conversation_bindings (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    thread_id TEXT,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT
                );

                INSERT INTO conversations (id, agent_id, title, status, metadata, created_at, updated_at)
                VALUES ('legacy-1', 'agent-a', 'pre-migration', 'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var store = fixture.CreateStore();
        // First access triggers EnsureCreatedAsync -> EnsureInitiatorColumnAsync.
        var loaded = await store.GetAsync(ConversationId.From("legacy-1"));

        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("pre-migration");
        loaded.Initiator.ShouldBeNull();

        // Save with an Initiator to prove the new column is writable + readable end-to-end.
        var alice = CitizenId.Of(UserId.From("alice"));
        loaded.Initiator = alice;
        await store.SaveAsync(loaded);

        var roundTrip = await fixture.CreateStore().GetAsync(ConversationId.From("legacy-1"));
        roundTrip!.Initiator!.Value.ShouldBe(alice);
    }

    [Fact]
    public async Task Migration_FoldsLegacyThreadIdColumn_IntoCompositeChannelAddress()
    {
        // Seed a database with the pre-#512 schema: conversation_bindings has a thread_id
        // column. The store must drop the column on first access and rewrite any non-null
        // thread_id values into the channel_address as "/topic:<value>" so existing bindings
        // still resolve under the new (channelType, channelAddress)-only matching rule.
        using var fixture = new StoreFixture();

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE conversations (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    purpose TEXT,
                    is_default INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Active',
                    active_session_id TEXT,
                    metadata TEXT NOT NULL DEFAULT '{}',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    instructions TEXT,
                    canvas_html TEXT,
                    initiator TEXT
                );

                CREATE TABLE conversation_bindings (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    thread_id TEXT,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT
                );

                CREATE INDEX idx_bindings_lookup
                    ON conversation_bindings(channel_type, channel_address, thread_id);

                INSERT INTO conversations (id, agent_id, title, status, metadata, created_at, updated_at)
                VALUES ('conv-numeric',     'agent-a', 'Numeric topic',     'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
                       ('conv-nonnumeric',  'agent-a', 'Non-numeric topic', 'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
                       ('conv-bare',        'agent-a', 'Bare chat',         'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');

                INSERT INTO conversation_bindings
                    (binding_id, conversation_id, channel_type, channel_address, thread_id, mode, threading_mode, bound_at)
                VALUES
                    ('b-numeric',    'conv-numeric',    'telegram', '12345', '67',         'Interactive', 'Single', '2024-01-01T00:00:00Z'),
                    ('b-nonnumeric', 'conv-nonnumeric', 'telegram', '12345', 'topic:99',   'Interactive', 'Single', '2024-01-01T00:00:00Z'),
                    ('b-bare',       'conv-bare',       'telegram', '99999', NULL,         'Interactive', 'Single', '2024-01-01T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var store = fixture.CreateStore();

        // First access triggers EnsureCreatedAsync -> MigrateThreadIdIntoChannelAddressAsync.
        var numeric = await store.GetAsync(ConversationId.From("conv-numeric"));
        var nonNumeric = await store.GetAsync(ConversationId.From("conv-nonnumeric"));
        var bare = await store.GetAsync(ConversationId.From("conv-bare"));

        numeric.ShouldNotBeNull();
        nonNumeric.ShouldNotBeNull();
        bare.ShouldNotBeNull();

        numeric!.ChannelBindings.Count.ShouldBe(1);
        numeric.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("12345/topic:67"));

        nonNumeric!.ChannelBindings.Count.ShouldBe(1);
        nonNumeric.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("12345/topic:topic:99"));

        bare!.ChannelBindings.Count.ShouldBe(1);
        bare.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("99999"));

        // The thread_id column must have been dropped — verify against the SQLite schema directly.
        await using (var verify = new SqliteConnection(fixture.ConnectionString))
        {
            await verify.OpenAsync();
            await using var pragma = verify.CreateCommand();
            pragma.CommandText = "PRAGMA table_info('conversation_bindings');";
            await using var reader = await pragma.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));

            columns.ShouldNotContain("thread_id");
            columns.ShouldContain("channel_address");
        }

        // Re-running the migration is a no-op: a second instance must not crash on
        // missing thread_id column nor double-encode existing addresses.
        var store2 = fixture.CreateStore();
        var numericAgain = await store2.GetAsync(ConversationId.From("conv-numeric"));
        numericAgain!.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("12345/topic:67"));
    }

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

    private static ChannelBinding CreateBinding(string channelType, string channelAddress)
        => new()
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From(channelType),
            ChannelAddress = ChannelAddress.From(channelAddress),
            BoundAt = DateTimeOffset.UtcNow,
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single
        };

}
