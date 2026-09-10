using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Content search over transcripts (Interface Review P1). Titles were searchable and what was
/// actually said was not, so "the conversation where we fixed the gateway restart" was findable
/// only if someone had titled it that — which matters here more than most places, because agents
/// run unattended and generate transcripts nobody titles.
///
/// Runs against a real SQLite database rather than a double: the whole feature IS the FTS5 index,
/// its triggers and its backfill, and a fake would assert nothing about any of them.
/// </summary>
public sealed class SqliteSessionStoreContentSearchTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SqliteSessionStoreContentSearchTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SqliteSessionStoreContentSearchTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _sessionDbPath = Path.Combine(_directoryPath, "sessions.db");
        _conversationDbPath = Path.Combine(_directoryPath, "conversations.db");
    }

    public void Dispose()
    {
        // Scoped to this test's own directory: ClearAllPools() is process-global and would dispose
        // sibling tests' live SQLite handles under parallel collections (#3324, #3392).
        SqlitePoolCleanup.ClearPoolsUnder(_directoryPath);
        if (Directory.Exists(_directoryPath))
            Directory.Delete(_directoryPath, recursive: true);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private SqliteConversationStore CreateConversationStore()
        => new($"Data Source={_conversationDbPath};Pooling=False",
               NullLogger<SqliteConversationStore>.Instance);

    private SqliteSessionStore CreateSessionStore(SqliteConversationStore conversationStore)
        => new($"Data Source={_sessionDbPath};Pooling=False",
               NullLogger<SqliteSessionStore>.Instance,
               conversationStore);

    private static async Task SeedAsync(
        SqliteSessionStore store, string sessionId, params SessionEntry[] entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From("gantry-manager"),
            Status = SessionStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        foreach (var entry in entries)
            session.AddEntry(entry);

        await store.SaveAsync(session, CancellationToken.None);
    }

    private static SessionEntry User(string content)
        => new() { Role = MessageRole.User, Content = content };

    private static SessionEntry Assistant(string content)
        => new() { Role = MessageRole.Assistant, Content = content };

    // ─── the point of the feature ────────────────────────────────────────────

    [Fact]
    public async Task Finds_a_conversation_by_what_was_said_in_it()
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", User("why does the gateway restart lose its pid file"));
        await SeedAsync(store, "s-2", User("what is the weather like"));

        var hits = await store.SearchHistoryAsync("gateway restart", 10, CancellationToken.None);

        hits.Count.ShouldBe(1);
        hits[0].SessionId.ShouldBe("s-1");
        hits[0].Snippet.ShouldContain("pid file");
    }

    [Fact]
    public async Task Requires_every_term_to_match()
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", User("the gateway is fine"));
        await SeedAsync(store, "s-2", User("the gateway restart is broken"));

        var hits = await store.SearchHistoryAsync("gateway restart", 10, CancellationToken.None);

        // Two words means both: someone narrowing a search expects it to narrow.
        hits.Count.ShouldBe(1);
        hits[0].SessionId.ShouldBe("s-2");
    }

    [Fact]
    public async Task Returns_the_conversation_id_so_a_caller_can_navigate_to_it()
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", User("the gateway restart lost its pid file"));

        var hits = await store.SearchHistoryAsync("pid", 10, CancellationToken.None);

        hits.ShouldNotBeEmpty();
        hits[0].ConversationId.ShouldNotBeNullOrWhiteSpace(
            "a hit nobody can navigate to is not an answer");
    }

    [Fact]
    public async Task Is_reachable_through_ISessionStore_not_only_the_concrete_type()
    {
        // THE test in this file. Every other one here calls the concrete SqliteSessionStore, and
        // they all passed while the feature returned nothing in production.
        //
        // A class's interface map is fixed where the interface is added - SessionStoreBase. With
        // only a default interface method to bind to, ISessionStore.SearchHistoryAsync bound to
        // that empty default there, and SqliteSessionStore declaring a matching public method did
        // not re-map it; the method was merely new. So the concrete type found 99 rows and the
        // interface found none - and ConversationsController holds the interface.
        var conversations = CreateConversationStore();
        ISessionStore store = CreateSessionStore(conversations);
        await SeedAsync((SqliteSessionStore)store, "s-1", User("the gateway restart lost its pid file"));

        var hits = await store.SearchHistoryAsync("pid", 10, CancellationToken.None);

        hits.ShouldNotBeEmpty(
            "callers hold ISessionStore; a method the interface cannot dispatch to is dead code");
    }

    // ─── the backfill, which is where this quietly fails ─────────────────────

    [Fact]
    public async Task Finds_history_that_predates_the_index()
    {
        // The triggers only see rows written after they exist. On any database that already holds
        // history — which is every real one — a fresh index would be empty and search would
        // confidently return nothing. That looks like an answer, which makes it the worst failure
        // available, so it gets its own test.
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-old", User("the gateway restart lost its pid file"));

        SqlitePoolCleanup.ClearPoolFor(_sessionDbPath);
        await DropIndexAsync();

        var conversations2 = CreateConversationStore();
        var store2 = CreateSessionStore(conversations2);
        var hits = await store2.SearchHistoryAsync("pid", 10, CancellationToken.None);

        hits.ShouldNotBeEmpty("history written before the index existed must still be findable");
    }

    /// <summary>
    /// Reduces the database to the state a pre-feature one is in: history present, no index, no
    /// triggers, and no marker saying the index was ever built. Dropping the index but keeping the
    /// marker would be a different scenario — and one that silently passes, which is how the first
    /// version of this test fooled itself.
    /// </summary>
    private async Task DropIndexAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS session_history_ai;
            DROP TRIGGER IF EXISTS session_history_ad;
            DROP TRIGGER IF EXISTS session_history_au;
            DROP TABLE IF EXISTS session_history_fts;
            DROP TABLE IF EXISTS search_index_state;
            """;
        await command.ExecuteNonQueryAsync();
    }

    // ─── what must NOT come back ─────────────────────────────────────────────

    [Fact]
    public async Task Does_not_return_replay_banners()
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[PLATFORM RESTART - AUTOMATIC REPLAY 1 of 2] gateway",
            IsReplayBanner = true
        });

        var hits = await store.SearchHistoryAsync("gateway", 10, CancellationToken.None);

        hits.ShouldBeEmpty("a banner is machinery, not something anyone said");
    }

    [Fact]
    public async Task Does_not_rank_tool_output_against_prose()
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1",
            new SessionEntry { Role = MessageRole.Tool, Content = "gateway gateway gateway gateway" },
            Assistant("we fixed the gateway"));

        var hits = await store.SearchHistoryAsync("gateway", 10, CancellationToken.None);

        // Tool spew would otherwise outrank the sentence that actually answers the question.
        hits.ShouldAllBe(h => h.Role != "tool");
        hits.ShouldContain(h => h.Snippet.Contains("we fixed"));
    }

    // ─── input that would otherwise throw ────────────────────────────────────

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("*")]
    [InlineData("gateway AND")]
    [InlineData("NEAR(")]
    [InlineData("col:value")]
    [InlineData("-gateway")]
    public async Task Survives_input_that_is_valid_prose_but_invalid_fts_syntax(string query)
    {
        // FTS5 has its own grammar, so raw input is not merely imprecise - a stray quote or bare
        // star throws, and a search box that 500s on an apostrophe is worse than no search box.
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", User("the gateway restart"));

        var hits = await store.SearchHistoryAsync(query, 10, CancellationToken.None);

        hits.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    public async Task An_empty_or_punctuation_only_query_returns_nothing_rather_than_everything(string query)
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", User("the gateway restart"));

        var hits = await store.SearchHistoryAsync(query, 10, CancellationToken.None);

        hits.ShouldBeEmpty();
    }

    // ─── the index tracks the table ──────────────────────────────────────────

    [Fact]
    public async Task An_edited_message_is_found_by_its_new_text_and_not_its_old()
    {
        var conversations = CreateConversationStore();
        var store = CreateSessionStore(conversations);
        await SeedAsync(store, "s-1", User("the aardvark restart"));

        await using (var connection = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE session_history SET content = 'the buffalo restart' WHERE content LIKE '%aardvark%';";
            await update.ExecuteNonQueryAsync();
        }

        (await store.SearchHistoryAsync("buffalo", 10, CancellationToken.None)).ShouldNotBeEmpty();
        (await store.SearchHistoryAsync("aardvark", 10, CancellationToken.None))
            .ShouldBeEmpty("a stale index would still answer for text that is no longer there");
    }
}
