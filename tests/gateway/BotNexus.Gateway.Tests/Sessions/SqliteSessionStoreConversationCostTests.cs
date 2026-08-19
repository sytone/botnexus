using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Tests for the SQLite conversation cost rollup (#2898). The aggregate must be derived from the
/// rows that already exist - no stored counter - and must count compaction summaries without
/// hydrating transcripts.
/// </summary>
public sealed class SqliteSessionStoreConversationCostTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SqliteSessionStoreConversationCostTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-tests-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(_connectionString);
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite file locks can linger briefly on Windows.
        }
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<ConversationId> SeedConversationAsync(string agentId = "alpha")
    {
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From(agentId)
        });
        return conversationId;
    }

    private async Task SeedSessionAsync(
        ConversationId conversationId,
        string sessionId,
        int messages,
        int compactionSummaries = 0)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("alpha"));
        session.Session.ConversationId = conversationId;

        var entries = new List<SessionEntry>();
        for (var i = 0; i < messages; i++)
        {
            entries.Add(new SessionEntry
            {
                Role = MessageRole.FromString(i % 2 == 0 ? "user" : "assistant"),
                Content = $"entry-{i}",
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        for (var i = 0; i < compactionSummaries; i++)
        {
            entries.Add(new SessionEntry
            {
                Role = MessageRole.FromString("system"),
                Content = $"summary-{i}",
                IsCompactionSummary = true,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        session.AddEntries(entries);
        await store.SaveAsync(session);
    }

    /// <summary>
    /// The rollup groups by conversation and sums across every session the conversation owns - the
    /// multi-session accumulation the feature exists to surface.
    /// </summary>
    [Fact]
    public async Task Rollup_aggregates_sessions_and_messages_per_conversation()
    {
        var conversation = await SeedConversationAsync();
        await SeedSessionAsync(conversation, "s1", messages: 4);
        await SeedSessionAsync(conversation, "s2", messages: 6);

        var costs = await CreateStore().GetConversationCostsAsync();

        var row = costs.ShouldHaveSingleItem();
        row.ConversationId.ShouldBe(conversation.Value);
        row.SessionCount.ShouldBe(2);
        // 10 ordinary entries across the two sessions.
        row.MessageCount.ShouldBe(10);
    }

    /// <summary>
    /// Compaction summaries are counted, and counted SEPARATELY from ordinary messages, so the
    /// context-pressure signal is not lost inside the transcript total.
    /// </summary>
    [Fact]
    public async Task Rollup_counts_compaction_summaries_separately()
    {
        var conversation = await SeedConversationAsync();
        await SeedSessionAsync(conversation, "s1", messages: 5, compactionSummaries: 3);

        var costs = await CreateStore().GetConversationCostsAsync();

        var row = costs.ShouldHaveSingleItem();
        row.CompactionSummaryCount.ShouldBe(3);
        // The summaries are transcript rows too, so the message total includes them - but the
        // compaction count is NOT the message count, which is the distinction under test.
        row.MessageCount.ShouldBe(8);
        row.CompactionSummaryCount.ShouldNotBe(row.MessageCount);
    }

    /// <summary>
    /// A conversation whose sessions carry no compaction summary reports a MEASURED zero, not null:
    /// this store did look, and the answer was none.
    /// </summary>
    [Fact]
    public async Task Conversation_with_no_compactions_reports_a_measured_zero()
    {
        var conversation = await SeedConversationAsync();
        await SeedSessionAsync(conversation, "s1", messages: 2);

        var costs = await CreateStore().GetConversationCostsAsync();

        costs.ShouldHaveSingleItem().CompactionSummaryCount.ShouldBe(0);
    }

    /// <summary>
    /// A session with no transcript at all still contributes to the session count - the ramp signal
    /// is about sessions, not messages - and reports zero messages rather than being dropped by the
    /// join.
    /// </summary>
    [Fact]
    public async Task Session_with_no_history_still_counts_toward_the_session_count()
    {
        var conversation = await SeedConversationAsync();
        await SeedSessionAsync(conversation, "s1", messages: 0);

        var costs = await CreateStore().GetConversationCostsAsync();

        var row = costs.ShouldHaveSingleItem();
        row.SessionCount.ShouldBe(1);
        row.MessageCount.ShouldBe(0);
    }

    /// <summary>
    /// Distinct conversations are reported as distinct rows and their counts do not bleed into each
    /// other - the failure a missing GROUP BY key would produce.
    /// </summary>
    [Fact]
    public async Task Distinct_conversations_do_not_share_counts()
    {
        var first = await SeedConversationAsync();
        var second = await SeedConversationAsync();
        await SeedSessionAsync(first, "s1", messages: 9);
        await SeedSessionAsync(second, "s2", messages: 1);

        var costs = (await CreateStore().GetConversationCostsAsync())
            .ToDictionary(c => c.ConversationId, StringComparer.Ordinal);

        costs.Count.ShouldBe(2);
        costs[first.Value].MessageCount.ShouldBe(9);
        costs[second.Value].MessageCount.ShouldBe(1);
    }

    /// <summary>
    /// Sad path: an empty store yields an empty rollup rather than a phantom row.
    /// </summary>
    [Fact]
    public async Task Empty_store_yields_an_empty_rollup()
    {
        (await CreateStore().GetConversationCostsAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// AC6: no stored counter column exists. Asserted against the live schema rather than by diff
    /// review alone, so a later commit that "optimised" the rollup into a maintained column reddens
    /// here by name.
    /// </summary>
    [Fact]
    public async Task No_stored_cost_counter_column_is_added_to_the_schema()
    {
        var conversation = await SeedConversationAsync();
        await SeedSessionAsync(conversation, "s1", messages: 1);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('sessions')";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        // Non-vacuity: the query really did read a populated schema.
        columns.ShouldContain("conversation_id");

        columns.ShouldNotContain(c => c.Contains("cost", StringComparison.OrdinalIgnoreCase));
        columns.ShouldNotContain(c => c.Contains("message_count", StringComparison.OrdinalIgnoreCase));
        columns.ShouldNotContain(c => c.Contains("session_count", StringComparison.OrdinalIgnoreCase));
        columns.ShouldNotContain(c => c.Contains("compaction_count", StringComparison.OrdinalIgnoreCase));
    }
}
