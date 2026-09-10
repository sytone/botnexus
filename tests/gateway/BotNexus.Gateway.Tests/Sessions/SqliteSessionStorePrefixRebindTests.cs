using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// RED contract tests for issue #4124. Rebinding cron sessions is an ID/metadata operation:
/// SQLite must filter before aggregate materialization and must never read transcript history.
/// </summary>
public sealed class SqliteSessionStorePrefixRebindTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-tests-{Guid.NewGuid():N}.db");
    private readonly InMemoryConversationStore _conversations = new();
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task RebindSessionsAsync_FiltersAgentAndExactPrefix_WithoutReadingHistoryOrMaterializingUnrelatedSessions()
    {
        var agent = AgentId.From("agent-a");
        var otherAgent = AgentId.From("agent-b");
        var legacy = await CreateConversationAsync(agent, "legacy");
        var canonical = await CreateConversationAsync(agent, "canonical");
        var otherOwner = await CreateConversationAsync(otherAgent, "other-owner");
        var store = CreateStore();

        await SeedAsync(store, "cron:job-1:first", agent, legacy, historyCount: 3);
        await SeedAsync(store, "cron:job-1:second", agent, legacy, historyCount: 2);
        await SeedAsync(store, "cron:job-10:not-this-job", agent, legacy, historyCount: 1);
        await SeedAsync(store, "cron:job-1:other-agent", otherAgent, otherOwner, historyCount: 1);
        await SeedAsync(store, "interactive-unrelated", agent, legacy, historyCount: 200);

        // A history-independent implementation keeps working when the transcript table is absent.
        // ListAsync/LoadSessionAsync cannot: they query session_history for every materialized row.
        await ExecuteAsync("DROP TABLE session_history;");

        var rebound = await store.RebindSessionsAsync(agent, "cron:job-1:", canonical);

        rebound.ShouldBe(2);
        (await ReadConversationIdAsync("cron:job-1:first")).ShouldBe(canonical.Value);
        (await ReadConversationIdAsync("cron:job-1:second")).ShouldBe(canonical.Value);
        (await ReadConversationIdAsync("cron:job-10:not-this-job")).ShouldBe(legacy.Value);
        (await ReadConversationIdAsync("cron:job-1:other-agent")).ShouldBe(otherOwner.Value);
        (await ReadConversationIdAsync("interactive-unrelated")).ShouldBe(legacy.Value);
    }

    [Fact]
    public async Task RebindSessionsAsync_CanonicalRowsRemainByteStable_AndEmptyMatchIsNoOp()
    {
        var agent = AgentId.From("agent-a");
        var canonical = await CreateConversationAsync(agent, "canonical");
        var store = CreateStore();
        await SeedAsync(store, "cron:job-1:canonical", agent, canonical, historyCount: 1);
        var updatedBefore = await ReadUpdatedAtAsync("cron:job-1:canonical");

        (await store.RebindSessionsAsync(agent, "cron:job-1:", canonical)).ShouldBe(0);
        (await ReadUpdatedAtAsync("cron:job-1:canonical")).ShouldBe(updatedBefore);
        (await store.RebindSessionsAsync(agent, "cron:missing:", canonical)).ShouldBe(0);
    }

    [Fact]
    public async Task RebindSessionsAsync_PreCancelledToken_DoesNotPersist()
    {
        var agent = AgentId.From("agent-a");
        var legacy = await CreateConversationAsync(agent, "legacy");
        var canonical = await CreateConversationAsync(agent, "canonical");
        var store = CreateStore();
        await SeedAsync(store, "cron:job-1:run", agent, legacy, historyCount: 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = () => store.RebindSessionsAsync(agent, "cron:job-1:", canonical, cancellation.Token);
        await Should.ThrowAsync<OperationCanceledException>(act);
        (await ReadConversationIdAsync("cron:job-1:run")).ShouldBe(legacy.Value);
    }

    private SqliteSessionStore CreateStore() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<ConversationId> CreateConversationAsync(AgentId agent, string title)
    {
        var conversation = await _conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agent,
            Title = title
        });
        return conversation.ConversationId;
    }

    private static async Task SeedAsync(SqliteSessionStore store, string id, AgentId agent, ConversationId conversation, int historyCount)
    {
        var session = await store.GetOrCreateAsync(SessionId.From(id), agent);
        session.ConversationId = conversation;
        session.AddEntries(Enumerable.Range(0, historyCount).Select(index => new SessionEntry
        {
            Role = MessageRole.User,
            Content = $"history-{index}",
            Timestamp = DateTimeOffset.UtcNow
        }));
        await store.SaveAsync(session);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadConversationIdAsync(string sessionId)
        => (string)(await ReadScalarAsync("conversation_id", sessionId))!;

    private async Task<string> ReadUpdatedAtAsync(string sessionId)
        => (string)(await ReadScalarAsync("updated_at", sessionId))!;

    private async Task<object?> ReadScalarAsync(string column, string sessionId)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        return await command.ExecuteScalarAsync();
    }
}
