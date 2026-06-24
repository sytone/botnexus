using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Regression tests for <see cref="SqliteSessionStore.ListSummariesAsync"/> (#1581).
/// The summary path must derive <c>MessageCount</c> from a COUNT aggregate without
/// loading transcript content, honour the <c>updatedAfter</c> window on parsed values,
/// and resolve the agent from the conversation.
/// </summary>
public sealed class SqliteSessionStoreListSummariesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SqliteSessionStoreListSummariesTests()
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
        SqliteConnection.ClearAllPools();
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

    private async Task SeedSessionAsync(
        string sessionId,
        string agentId,
        SessionStatus status,
        DateTimeOffset updatedAt,
        int historyEntries,
        ChannelKey? channelType = null)
    {
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From(agentId)
        });

        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From(agentId));
        session.Session.ConversationId = conversationId;
        session.ChannelType = channelType;
        session.Status = status;
        session.AddEntries(Enumerable.Range(0, historyEntries)
            .Select(i => new SessionEntry
            {
                Role = MessageRole.FromString(i % 2 == 0 ? "user" : "assistant"),
                Content = $"entry-{i}",
                Timestamp = updatedAt
            }));
        // Set UpdatedAt last: AddEntries bumps it to "now".
        session.UpdatedAt = updatedAt;
        await store.SaveAsync(session);
    }

    [Fact]
    public async Task ListSummariesAsync_DerivesMessageCount_WithoutLoadingTranscript()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("sum-recent", "agent-a", SessionStatus.Active, now.AddHours(-1), historyEntries: 4);

        var store = CreateStore();
        var summaries = await store.ListSummariesAsync(now.AddHours(-24));

        var summary = summaries.ShouldHaveSingleItem();
        summary.SessionId.ShouldBe("sum-recent");
        summary.AgentId.ShouldBe("agent-a");
        summary.MessageCount.ShouldBe(4);
        summary.Status.ShouldBe(SessionStatus.Active);
        summary.SessionType.ShouldBe(SessionType.UserAgent);
        summary.IsInteractive.ShouldBeTrue();
    }

    [Fact]
    public async Task ListSummariesAsync_ExcludesSessionsOutsideRetentionWindow()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("sum-recent", "agent-a", SessionStatus.Active, now.AddHours(-2), historyEntries: 1);
        await SeedSessionAsync("sum-old", "agent-a", SessionStatus.Active, now.AddHours(-48), historyEntries: 1);

        var store = CreateStore();
        var ids = (await store.ListSummariesAsync(now.AddHours(-24)))
            .Select(summary => summary.SessionId)
            .ToList();

        ids.ShouldContain("sum-recent");
        ids.ShouldNotContain("sum-old");
    }

    [Fact]
    public async Task ListSummariesAsync_ResolvesAgentAndChannelFromMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("sum-a", "agent-a", SessionStatus.Active, now.AddHours(-1), historyEntries: 2);
        await SeedSessionAsync("sum-b", "agent-b", SessionStatus.Sealed, now.AddHours(-1), historyEntries: 3,
            channelType: ChannelKey.From("telegram"));

        var store = CreateStore();
        var summaries = await store.ListSummariesAsync(now.AddHours(-24));

        summaries.ShouldContain(s => s.SessionId == "sum-a");
        var b = summaries.Single(s => s.SessionId == "sum-b");
        b.AgentId.ShouldBe("agent-b");
        b.ChannelType!.Value.Value.ShouldBe("telegram");
        b.Status.ShouldBe(SessionStatus.Sealed);
        b.MessageCount.ShouldBe(3);
    }
}
