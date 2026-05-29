using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Phase 9 / P9-A (issue #613) — pins the world-id stamping and lazy-backfill contract across all
/// three <c>IConversationStore</c> implementations. Each store must:
/// <list type="number">
///   <item>Stamp <c>Conversation.WorldId</c> with the current world's id when a caller creates or
///         saves a conversation with an empty WorldId (so callers never need to know about it).</item>
///   <item>Lazy-backfill <c>WorldId</c> on read when a legacy row was persisted before the column
///         existed (post-migration, the row exists but the field is empty).</item>
///   <item>Round-trip an explicit non-empty WorldId verbatim (cross-world relays surface the source
///         world id via metadata; the conversation's own WorldId stays the receiver's).</item>
/// </list>
/// </summary>
public sealed class ConversationWorldIdTests
{
    private static AgentId Agent(string id) => AgentId.From(id);

    private static Conversation NewConversation(string title)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = Agent("agent-a"),
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    // ---------------------------------------------------------------------
    // InMemoryConversationStore
    // ---------------------------------------------------------------------

    [Fact]
    public async Task InMemory_WorldId_StampedOnCreate_FromInjectedWorldContext()
    {
        var ctx = new FakeWorldContext("world-x");
        var store = new InMemoryConversationStore(ctx);

        var conv = NewConversation("stamp-on-create");
        conv.WorldId.ShouldBe(string.Empty,
            customMessage: "Test should construct a fresh conversation with no WorldId so the stamp is observable.");

        await store.CreateAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.WorldId.ShouldBe("world-x",
            customMessage: "InMemoryConversationStore.CreateAsync must stamp WorldId from the injected IWorldContext.");
    }

    [Fact]
    public async Task InMemory_WorldId_ExplicitValuePreservedThroughCreate()
    {
        var ctx = new FakeWorldContext("world-x");
        var store = new InMemoryConversationStore(ctx);

        var conv = NewConversation("explicit-preserve");
        conv.WorldId = "world-y"; // Caller explicitly sets it (e.g. cross-world receiver scenario).
        await store.CreateAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.WorldId.ShouldBe("world-y",
            customMessage: "Stamp must only fill an EMPTY WorldId; an explicit value must round-trip verbatim.");
    }

    [Fact]
    public async Task InMemory_WorldId_StampedOnSave_WhenCallerClearsToEmpty()
    {
        // Validates that SaveAsync stamps the world id BEFORE the value is durably stored —
        // distinguishable from read-time backfill by mutating the world-context after the save.
        // If SaveAsync forgot to stamp, the empty value would be stored, then BackfillWorldId
        // on GetAsync would refill it with the CURRENT world id (world-y), causing the assertion
        // to fail. With a correct save-time stamp the value is locked to world-x.
        var ctx = new FakeWorldContext("world-x");
        var store = new InMemoryConversationStore(ctx);
        var conv = NewConversation("save-stamp");
        await store.CreateAsync(conv);

        conv.Title = "save-stamp-updated";
        conv.WorldId = string.Empty;
        await store.SaveAsync(conv);

        // Flip world context so read-time backfill would produce a different value if save-stamp didn't fire.
        ctx.Set("world-y");

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.WorldId.ShouldBe("world-x",
            customMessage: "SaveAsync must stamp WorldId before persistence; relying on read-time backfill is wrong.");
    }

    [Fact]
    public async Task InMemory_WorldId_NoWorldContext_LeavesEmpty()
    {
        // Tests / boot-strap scenarios where no IWorldContext is wired: the conversation stays as
        // the caller set it (string.Empty by default). No NullReferenceException; no surprise stamp.
        var store = new InMemoryConversationStore();
        var conv = NewConversation("no-context");
        await store.CreateAsync(conv);
        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.WorldId.ShouldBe(string.Empty);
    }

    // ---------------------------------------------------------------------
    // SqliteConversationStore
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sqlite_WorldId_RoundTrips_AcrossProcessRestart()
    {
        using var fixture = new StoreFixture();
        var ctx = new FakeWorldContext("world-prod-1");
        var store = fixture.CreateStore(ctx);
        var conv = NewConversation("roundtrip");
        await store.CreateAsync(conv);

        // Fresh store instance (different cache, fresh SQLite connection) — proves the value
        // is persisted at the SQL level, not just retained in the in-memory clone cache.
        var fresh = fixture.CreateStore(ctx);
        var loaded = await fresh.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.WorldId.ShouldBe("world-prod-1",
            customMessage: "WorldId must round-trip through the SQLite schema. If empty here, INSERT/SELECT " +
                "is missing the world_id binding -- the field is dropping on the floor at the SQL boundary.");
    }

    [Fact]
    public async Task Sqlite_WorldId_StampedOnCreate_WhenCallerLeavesEmpty()
    {
        using var fixture = new StoreFixture();
        var ctx = new FakeWorldContext("world-stamp");
        var store = fixture.CreateStore(ctx);
        var conv = NewConversation("stamp");
        conv.WorldId.ShouldBe(string.Empty);
        await store.CreateAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.WorldId.ShouldBe("world-stamp");
    }

    [Fact]
    public async Task Sqlite_WorldId_ExplicitValuePreservedThroughSave()
    {
        using var fixture = new StoreFixture();
        var ctx = new FakeWorldContext("world-default");
        var store = fixture.CreateStore(ctx);
        var conv = NewConversation("explicit");
        conv.WorldId = "world-other";
        await store.CreateAsync(conv);

        // Round through SaveAsync (the upsert path).
        conv.Title = "explicit-updated";
        await store.SaveAsync(conv);

        var fresh = fixture.CreateStore(ctx);
        var loaded = await fresh.GetAsync(conv.ConversationId);
        loaded!.WorldId.ShouldBe("world-other",
            customMessage: "SaveAsync must not overwrite an explicit non-empty WorldId.");
    }

    [Fact]
    public async Task Sqlite_Migration_AddsWorldIdColumn_AndBackfillsCurrentWorldId_ForLegacyRows()
    {
        // Seed a database with the schema as it existed BEFORE the world_id column was added.
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
                    initiator TEXT,
                    kind TEXT NOT NULL DEFAULT 'HumanAgent'
                );

                CREATE TABLE conversation_bindings (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT
                );

                INSERT INTO conversations (id, agent_id, title, status, metadata, created_at, updated_at, kind)
                VALUES ('legacy-w', 'agent-a', 'before-worldid', 'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', 'HumanAgent');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var ctx = new FakeWorldContext("world-backfill");
        var store = fixture.CreateStore(ctx);
        // First access triggers EnsureCreatedAsync -> EnsureWorldIdColumnAsync.
        var loaded = await store.GetAsync(ConversationId.From("legacy-w"));

        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("before-worldid");
        loaded.WorldId.ShouldBe("world-backfill",
            customMessage: "Legacy rows missing world_id must be lazy-backfilled to the current world id on read. " +
                "If this fails with empty string, BackfillWorldId is not being applied to the load path.");

        // Save back and re-read from a fresh store to confirm the backfilled value persists.
        await store.SaveAsync(loaded);
        var fresh = fixture.CreateStore(ctx);
        var roundTrip = await fresh.GetAsync(ConversationId.From("legacy-w"));
        roundTrip!.WorldId.ShouldBe("world-backfill");
    }

    [Fact]
    public async Task Sqlite_Kind_RoundTrips_ThroughSaveAsync_AfterCreateAsync_RegressionPin()
    {
        // PRE-EXISTING BUG FIXED IN P9-A: SqliteConversationStore.SaveConversationAsync's UPSERT
        // branch did not bind $kind, so a SaveAsync following a CreateAsync silently set kind=NULL,
        // which the loader mapped back to ConversationKind.HumanAgent (silent demotion). The
        // existing Kind_RoundTrips_AllValues_AcrossProcessRestart test only exercised CreateAsync
        // (the non-upsert branch), missing this. This pin uses SaveAsync after CreateAsync.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conv = NewConversation("kind-bug");
        conv.Kind = ConversationKind.AgentAgent;
        await store.CreateAsync(conv);

        // The Save path that previously demoted Kind: title-only mutation triggers the UPSERT.
        conv.Title = "kind-bug-saved";
        await store.SaveAsync(conv);

        var fresh = fixture.CreateStore();
        var loaded = await fresh.GetAsync(conv.ConversationId);
        loaded!.Kind.ShouldBe(ConversationKind.AgentAgent,
            customMessage: "SaveAsync (upsert path) must bind $kind. If this returns HumanAgent, the kind " +
                "is being demoted on every save -- regression of the latent bug P9-A fixed.");
    }
}
