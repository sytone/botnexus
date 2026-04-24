using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
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

        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe("s1");
        reloaded.AgentId.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task GetAsync_WithMissingSession_ReturnsNull()
    {
        using var fixture = new StoreFixture();

        var missing = await fixture.CreateStore().GetAsync("missing");

        missing.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_WithHistoryAndMetadata_PersistsValues()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Metadata["tenant"] = "a";
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.ShouldNotBeNull();
        reloaded!.History.Where(e => e.Content == "hello").ShouldHaveSingleItem();
        reloaded.Metadata.ShouldContainKey("tenant");
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingSessionAndMetadata()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.ChannelType = ChannelKey.From("signalr");
        session.CallerId = "caller-a";
        session.Metadata["version"] = 1L;
        await store.SaveAsync(session);

        session.Metadata["version"] = 2L;
        session.Metadata["theme"] = "dark";
        session.Status = SessionStatus.Suspended;
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(SessionStatus.Suspended);
        reloaded.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        reloaded.CallerId.ShouldBe("caller-a");
        reloaded.Metadata.ShouldContainKey("version");
        reloaded.Metadata["version"]!.ToString().ShouldBe("2");
        reloaded.Metadata.ShouldContainKey("theme");
        reloaded.Metadata["theme"]!.ToString().ShouldBe("dark");
    }

    [Fact]
    public async Task SaveAsync_WithMultipleHistoryEntries_PersistsOrderedHistory()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");

        session.AddEntries([
            new SessionEntry { Role = MessageRole.System, Content = "boot" },
            new SessionEntry { Role = MessageRole.User, Content = "hello" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "world" }
        ]);

        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");
        reloaded.ShouldNotBeNull();
        reloaded!.GetHistorySnapshot().Select(entry => entry.Content)
            .ToList().ShouldBe(new[] { "boot", "hello", "world" });
    }

    [Fact]
    public async Task DeleteAsync_WithExistingSession_RemovesSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);

        await store.DeleteAsync("s1");

        (await fixture.CreateStore().GetAsync("s1")).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesHistoryRowsForSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        await store.SaveAsync(session);

        await store.DeleteAsync("s1");

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id = 's1'";
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        count.ShouldBe(0);
    }

    [Fact]
    public async Task ArchiveAsync_SetStatusToClosed()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);

        await store.ArchiveAsync("s1");

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM sessions WHERE id = 's1'";
        var status = (string?)await command.ExecuteScalarAsync();
        status.ShouldBe(SessionStatus.Sealed.ToString());
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

        sessions.Select(s => s.SessionId.Value).OrderBy(id => id).ShouldBe(new[] { "s1", "s2", "s3" });
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

        allSessions.Count().ShouldBe(3);
        filtered.ShouldAllBe(s => s.AgentId == "agent-a");
    }

    [Fact]
    public async Task ListByChannelAsync_FiltersByAgentAndNormalizedChannel_OrderedByCreatedAtDesc()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-old"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-new"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-other-channel"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("telegram")
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-null-channel"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        });

        var sessions = await store.ListByChannelAsync("agent-a", ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId.Value).ShouldBe(new[] { "s-new", "s-old" }, ignoreOrder: false);
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
                session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}" });
                session.Metadata["index"] = i;
                await store.SaveAsync(session);
                return await store.GetAsync(sessionId);
            });

        var reloaded = await Task.WhenAll(operations);

        reloaded.ShouldAllBe(session => session != null);
        reloaded.Select(session => session!.SessionId).ShouldBeUnique();
        reloaded.ShouldAllBe(session => session!.History.Count == 1);
    }

    [Fact]
    public async Task FirstUse_AutoCreatesTables()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        File.Exists(fixture.DatabasePath).ShouldBeFalse();

        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);

        File.Exists(fixture.DatabasePath).ShouldBeTrue();

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

        tables.ShouldBe(new[] { "session_history", "sessions" }, ignoreOrder: false);

        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(session_history)";
        var columns = new List<string>();
        await using var columnReader = await columnCommand.ExecuteReaderAsync();
        while (await columnReader.ReadAsync())
            columns.Add(columnReader.GetString(1));

        columns.ShouldContain("is_compaction_summary");
    }

    [Fact]
    public async Task GetAsync_WithLegacySchema_MigratesCompactionColumn()
    {
        using var fixture = new StoreFixture();
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT,
                    channel_type TEXT,
                    caller_id TEXT,
                    status TEXT,
                    metadata TEXT,
                    created_at TEXT,
                    updated_at TEXT
                );

                CREATE TABLE session_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT,
                    role TEXT,
                    content TEXT,
                    timestamp TEXT,
                    tool_name TEXT,
                    tool_call_id TEXT
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true });
        await store.SaveAsync(session);

        await using var verifyConnection = new SqliteConnection(fixture.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var columnCommand = verifyConnection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(session_history)";
        var columns = new List<string>();
        await using var columnReader = await columnCommand.ExecuteReaderAsync();
        while (await columnReader.ReadAsync())
            columns.Add(columnReader.GetString(1));

        columns.ShouldContain("is_compaction_summary");
    }

    [Fact]
    public async Task GetExistenceAsync_ReturnsOwnedAndParticipantSessions_WithFiltersApplied()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("owned"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            CreatedAt = now.AddDays(-2)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("participant"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-b"),
            SessionType = BotNexus.Domain.Primitives.SessionType.Cron,
            Participants =
            [
                new BotNexus.Domain.Primitives.SessionParticipant { Type = BotNexus.Domain.Primitives.ParticipantType.Agent, Id = "agent-a" }
            ],
            CreatedAt = now.AddDays(-1)
        });

        var sessions = await store.GetExistenceAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            new ExistenceQuery
            {
                TypeFilter = BotNexus.Domain.Primitives.SessionType.Cron,
                From = now.AddDays(-1.5),
                Limit = 10
            });

        sessions.Select(session => session.SessionId.Value).ShouldHaveSingleItem().ShouldBe("participant");
    }

    private static async Task CreateAndSaveAsync(SqliteSessionStore store, string sessionId, string agentId)
    {
        var session = await store.GetOrCreateAsync(sessionId, agentId);
        await store.SaveAsync(session);
    }

    /// <summary>Proves the global lock is gone: 24 different sessions save concurrently without deadlock or data loss.</summary>
    [Fact]
    public async Task SaveAsync_ManySessions_ConcurrentlyWithoutDeadlock()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var tasks = Enumerable.Range(0, 24).Select(async i =>
        {
            var session = await store.GetOrCreateAsync($"s{i:D2}", "agent-a");
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"msg-{i}" });
            await store.SaveAsync(session);
        });

        await Task.WhenAll(tasks); // must complete without timeout or exception

        var all = await store.ListAsync();
        all.Count().ShouldBe(24);
    }

    /// <summary>Proves different sessions don't block each other: session A save doesn't block session B save.</summary>
    [Fact]
    public async Task SaveAsync_TwoDifferentSessions_DoNotBlockEachOther()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var a = await store.GetOrCreateAsync("session-a", "agent-a");
        var b = await store.GetOrCreateAsync("session-b", "agent-b");
        a.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello-a" });
        b.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello-b" });

        await Task.WhenAll(store.SaveAsync(a), store.SaveAsync(b));

        var ra = await store.GetAsync("session-a");
        var rb = await store.GetAsync("session-b");
        ra!.History.Last().Content.ShouldBe("hello-a");
        rb!.History.Last().Content.ShouldBe("hello-b");
    }

    /// <summary>Proves WAL mode is enabled — allows concurrent reads while writing.</summary>
    [Fact]
    public async Task Database_EnablesWalMode()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        // Trigger DB init
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await cmd.ExecuteScalarAsync();
        mode.ShouldBe("wal", StringCompareShould.IgnoreCase);
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





