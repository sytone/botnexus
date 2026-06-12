using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Conversations;

public sealed class ConversationAuditStoreTests : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConversationAuditStore _store;
    private readonly SqliteConnection _keepAlive;

    public ConversationAuditStoreTests()
    {
        var dbName = $"audit_test_{Guid.NewGuid():N}";
        _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        _store = new SqliteConversationAuditStore(
            _connectionString,
            NullLogger<SqliteConversationAuditStore>.Instance);
    }

    public void Dispose() { _keepAlive.Dispose(); }

    [Fact]
    public async Task RecordAsync_StoresEntry_And_GetByConversation_ReturnsIt()
    {
        var entry = new ConversationAuditEntry
        {
            ConversationId = "conv-1",
            AgentId = "nova",
            Action = "created",
            Actor = "api",
            Source = "rest-api",
            NewValue = "Test title",
            Timestamp = DateTimeOffset.UtcNow
        };

        await _store.RecordAsync(entry);
        var results = await _store.GetByConversationAsync("conv-1");

        Assert.Single(results);
        Assert.Equal("created", results[0].Action);
        Assert.Equal("nova", results[0].AgentId);
        Assert.Equal("api", results[0].Actor);
        Assert.Equal("rest-api", results[0].Source);
        Assert.Equal("Test title", results[0].NewValue);
    }

    [Fact]
    public async Task GetByConversation_ReturnsEmpty_WhenNoEntries()
    {
        var results = await _store.GetByConversationAsync("nonexistent");
        Assert.Empty(results);
    }

    [Fact]
    public async Task RecordAsync_TruncatesPreviousValue_To200Chars()
    {
        var longValue = new string('x', 500);
        var entry = new ConversationAuditEntry
        {
            ConversationId = "conv-2",
            AgentId = "nova",
            Action = "title_changed",
            Actor = "farnsworth",
            Source = "tool",
            PreviousValue = longValue,
            NewValue = "short",
            Timestamp = DateTimeOffset.UtcNow
        };

        await _store.RecordAsync(entry);
        var results = await _store.GetByConversationAsync("conv-2");

        Assert.Equal(200, results[0].PreviousValue!.Length);
    }

    [Fact]
    public async Task GetByConversation_OrdersMostRecentFirst()
    {
        for (int i = 0; i < 3; i++)
        {
            await _store.RecordAsync(new ConversationAuditEntry
            {
                ConversationId = "conv-3",
                AgentId = "nova",
                Action = $"action_{i}",
                Actor = "api",
                Source = "rest-api",
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        var results = await _store.GetByConversationAsync("conv-3");
        Assert.Equal("action_2", results[0].Action);
        Assert.Equal("action_0", results[2].Action);
    }

    [Fact]
    public async Task GetByConversation_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.RecordAsync(new ConversationAuditEntry
            {
                ConversationId = "conv-4",
                AgentId = "nova",
                Action = $"action_{i}",
                Actor = "api",
                Source = "rest-api",
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        var results = await _store.GetByConversationAsync("conv-4", limit: 3);
        Assert.Equal(3, results.Count);
    }
}
