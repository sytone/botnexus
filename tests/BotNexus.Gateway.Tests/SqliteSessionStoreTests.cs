using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class SqliteSessionStoreTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithUnknownSession_CreatesAndPersistsSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.Should().NotBeNull();
        reloaded!.SessionId.Should().Be("s1");
        reloaded.AgentId.Should().Be("agent-a");
    }

    [Fact]
    public async Task GetAsync_WithMissingSession_ReturnsNull()
    {
        using var fixture = new StoreFixture();

        var missing = await fixture.CreateStore().GetAsync("missing");

        missing.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_WithHistoryAndMetadata_PersistsValues()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Metadata["tenant"] = "a";
        session.History.Add(new SessionEntry { Role = "user", Content = "hello" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.Should().NotBeNull();
        reloaded!.History.Should().ContainSingle(e => e.Content == "hello");
        reloaded.Metadata.Should().ContainKey("tenant");
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingSessionAndMetadata()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.ChannelType = "websocket";
        session.CallerId = "caller-a";
        session.Metadata["version"] = 1L;
        await store.SaveAsync(session);

        session.Metadata["version"] = 2L;
        session.Metadata["theme"] = "dark";
        session.Status = SessionStatus.Suspended;
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(SessionStatus.Suspended);
        reloaded.ChannelType.Should().Be("websocket");
        reloaded.CallerId.Should().Be("caller-a");
        reloaded.Metadata.Should().ContainKey("version");
        reloaded.Metadata["version"]!.ToString().Should().Be("2");
        reloaded.Metadata.Should().ContainKey("theme");
        reloaded.Metadata["theme"]!.ToString().Should().Be("dark");
    }

    [Fact]
    public async Task SaveAsync_WithMultipleHistoryEntries_PersistsOrderedHistory()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");

        session.AddEntries([
            new SessionEntry { Role = "system", Content = "boot" },
            new SessionEntry { Role = "user", Content = "hello" },
            new SessionEntry { Role = "assistant", Content = "world" }
        ]);

        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");
        reloaded.Should().NotBeNull();
        reloaded!.GetHistorySnapshot().Select(entry => entry.Content)
            .Should().ContainInOrder("boot", "hello", "world");
    }

    [Fact]
    public async Task DeleteAsync_WithExistingSession_RemovesSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);

        await store.DeleteAsync("s1");

        (await fixture.CreateStore().GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesHistoryRowsForSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.AddEntry(new SessionEntry { Role = "user", Content = "hello" });
        await store.SaveAsync(session);

        await store.DeleteAsync("s1");

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id = 's1'";
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        count.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_WithStoredSessions_ReturnsAllSessions()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await CreateAndSaveAsync(store, "s1", "agent-a");
        await CreateAndSaveAsync(store, "s2", "agent-b");
        await CreateAndSaveAsync(store, "s3", "agent-a");

        var sessions = await store.ListAsync();

        sessions.Select(s => s.SessionId).Should().BeEquivalentTo("s1", "s2", "s3");
    }

    [Fact]
    public async Task ListAsync_WithAndWithoutFilter_ReturnsExpectedSessions()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await CreateAndSaveAsync(store, "s1", "agent-a");
        await CreateAndSaveAsync(store, "s2", "agent-b");
        await CreateAndSaveAsync(store, "s3", "agent-a");

        var allSessions = await store.ListAsync();
        var filtered = await store.ListAsync("agent-a");

        allSessions.Should().HaveCount(3);
        filtered.Should().OnlyContain(s => s.AgentId == "agent-a");
    }

    [Fact]
    public async Task ConcurrentAccess_SavesAndLoadsWithoutCorruption()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var operations = Enumerable.Range(0, 24)
            .Select(async i =>
            {
                var sessionId = $"s{i:D2}";
                var session = await store.GetOrCreateAsync(sessionId, "agent-a");
                session.AddEntry(new SessionEntry { Role = "user", Content = $"m-{i}" });
                session.Metadata["index"] = i;
                await store.SaveAsync(session);
                return await store.GetAsync(sessionId);
            });

        var reloaded = await Task.WhenAll(operations);

        reloaded.Should().OnlyContain(session => session != null);
        reloaded.Select(session => session!.SessionId).Should().OnlyHaveUniqueItems();
        reloaded.Should().OnlyContain(session => session!.History.Count == 1);
    }

    [Fact]
    public async Task FirstUse_AutoCreatesTables()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        File.Exists(fixture.DatabasePath).Should().BeFalse();

        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);

        File.Exists(fixture.DatabasePath).Should().BeTrue();

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name IN ('sessions', 'session_history')
            ORDER BY name
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        tables.Should().Equal("session_history", "sessions");
    }

    private static async Task CreateAndSaveAsync(SqliteSessionStore store, string sessionId, string agentId)
    {
        var session = await store.GetOrCreateAsync(sessionId, agentId);
        await store.SaveAsync(session);
    }

    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture()
        {
            DirectoryPath = Path.Combine(
                AppContext.BaseDirectory,
                "SqliteSessionStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "sessions.db");
            ConnectionString = $"Data Source={DatabasePath};Pooling=False";
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }

        public SqliteSessionStore CreateStore()
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
