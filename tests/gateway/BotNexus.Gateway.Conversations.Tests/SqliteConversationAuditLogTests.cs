using BotNexus.Gateway.Conversations;
using Xunit;

namespace BotNexus.Gateway.Conversations.Tests;

public sealed class SqliteConversationAuditLogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationAuditLog _sut;

    public SqliteConversationAuditLogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}.sqlite");
        _sut = new SqliteConversationAuditLog($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // SQLite may still hold a lock; best-effort cleanup.
        }
    }

    [Fact]
    public async Task LogAsync_And_GetAsync_RoundTrips()
    {
        var entry = new ConversationAuditEntry
        {
            ConversationId = "c_test123",
            Action = "created",
            Actor = "api",
            Source = "rest-api",
            PreviousValue = null,
            NewValue = "My Conversation",
            Timestamp = DateTimeOffset.UtcNow
        };

        await _sut.LogAsync(entry);

        var entries = await _sut.GetAsync("c_test123");
        Assert.Single(entries);
        Assert.Equal("created", entries[0].Action);
        Assert.Equal("api", entries[0].Actor);
        Assert.Equal("rest-api", entries[0].Source);
        Assert.Null(entries[0].PreviousValue);
        Assert.Equal("My Conversation", entries[0].NewValue);
    }

    [Fact]
    public async Task GetAsync_ReturnsNewestFirst()
    {
        await _sut.LogAsync(new ConversationAuditEntry
        {
            ConversationId = "c_order",
            Action = "created",
            Actor = "api",
            Source = "rest-api",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await _sut.LogAsync(new ConversationAuditEntry
        {
            ConversationId = "c_order",
            Action = "title_changed",
            Actor = "api",
            Source = "rest-api",
            PreviousValue = "Old",
            NewValue = "New",
            Timestamp = DateTimeOffset.UtcNow
        });

        var entries = await _sut.GetAsync("c_order");
        Assert.Equal(2, entries.Count);
        Assert.Equal("title_changed", entries[0].Action);
        Assert.Equal("created", entries[1].Action);
    }

    [Fact]
    public async Task GetAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            await _sut.LogAsync(new ConversationAuditEntry
            {
                ConversationId = "c_limit",
                Action = $"action_{i}",
                Actor = "api",
                Source = "rest-api",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }

        var entries = await _sut.GetAsync("c_limit", limit: 3);
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public async Task GetAsync_FiltersOnConversationId()
    {
        await _sut.LogAsync(new ConversationAuditEntry
        {
            ConversationId = "c_a",
            Action = "created",
            Actor = "api",
            Source = "rest-api",
            Timestamp = DateTimeOffset.UtcNow
        });
        await _sut.LogAsync(new ConversationAuditEntry
        {
            ConversationId = "c_b",
            Action = "created",
            Actor = "api",
            Source = "rest-api",
            Timestamp = DateTimeOffset.UtcNow
        });

        var entries = await _sut.GetAsync("c_a");
        Assert.Single(entries);
        Assert.Equal("c_a", entries[0].ConversationId);
    }

    [Fact]
    public async Task GetAsync_ReturnsEmpty_WhenNoEntries()
    {
        var entries = await _sut.GetAsync("c_nonexistent");
        Assert.Empty(entries);
    }
}
