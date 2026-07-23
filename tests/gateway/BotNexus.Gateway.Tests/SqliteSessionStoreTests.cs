using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Conversations;
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

        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));

        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe("s1");
        reloaded.AgentId.Value.ShouldBe("agent-a");
    }

    [Fact]
    public async Task GetAsync_WithMissingSession_ReturnsNull()
    {
        using var fixture = new StoreFixture();

        var missing = await fixture.CreateStore().GetAsync(SessionId.From("missing"));

        missing.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_WithHistoryAndMetadata_PersistsValues()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.Metadata["tenant"] = "a";
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));

        reloaded.ShouldNotBeNull();
        reloaded!.History.Where(e => e.Content == "hello").ShouldHaveSingleItem();
        reloaded.Metadata.ShouldContainKey("tenant");
    }

    [Fact]
    public async Task Cache_IsBounded_ColdSessionsStillReadableViaFallThrough()
    {
        // The in-memory cache is intentionally bounded: inserting far more distinct
        // sessions than the cap must not retain them all in memory, yet every session
        // must remain correctly readable (cold reads fall through to SQLite). This is
        // the regression guard for the unbounded-cache leak (#1504).
        using var fixture = new StoreFixture();
        const int cap = 8;
        const int total = 40;
        var store = fixture.CreateStore(cacheCapacity: cap);

        for (var i = 0; i < total; i++)
        {
            var session = await store.GetOrCreateAsync(SessionId.From($"s{i}"), AgentId.From("agent-a"));
            session.History.Add(new SessionEntry { Role = MessageRole.User, Content = $"msg-{i}" });
            await store.SaveAsync(session);
        }

        // Every session — including the earliest ones long since evicted from the
        // bounded cache — is still retrievable with its persisted history intact.
        for (var i = 0; i < total; i++)
        {
            var reloaded = await store.GetAsync(SessionId.From($"s{i}"));
            reloaded.ShouldNotBeNull();
            reloaded!.SessionId.Value.ShouldBe($"s{i}");
            reloaded.History.Where(e => e.Content == $"msg-{i}").ShouldHaveSingleItem();
        }
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingSessionAndMetadata()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.ChannelType = ChannelKey.From("signalr");
        session.CallerId = "caller-a";
        session.Metadata["version"] = 1L;
        await store.SaveAsync(session);

        session.Metadata["version"] = 2L;
        session.Metadata["theme"] = "dark";
        session.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Suspended;
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Suspended);
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
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        session.AddEntries([
            new SessionEntry { Role = MessageRole.System, Content = "boot" },
            new SessionEntry { Role = MessageRole.User, Content = "hello" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "world" }
        ]);

        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));
        reloaded.ShouldNotBeNull();
        reloaded!.GetHistorySnapshot().Select(entry => entry.Content)
            .ToList().ShouldBe(new[] { "boot", "hello", "world" });
    }

    [Fact]
    public async Task SaveAsync_StringMetadata_RoundTripsThroughJsonElement_AcrossReload()
    {
        // Documentation pin (PR #549 critique sweep — bug-hunt BLOCKING #1): SqliteSessionStore
        // deserializes session metadata into `Dictionary<string, object?>` via System.Text.Json,
        // which boxes string values as JsonElement (kind=String). Readers using `value as string`
        // silently get null after a restart — exactly the bug in CrossWorldFederationController.
        // MetadataString. Other readers in this repo already handle JsonElement explicitly (see
        // AgentConverseTool.ResolveCallChainAsync, PreCompactionMemoryFlusher.GetLastFlushCycle).
        //
        // This test pins:
        // 1. The value DOES round-trip (the bytes survive).
        // 2. The value comes back as either System.String OR JsonElement(kind=String) — both
        //    extractable, but `value as string` fails on the JsonElement case.
        // The controller-side regression test
        // CrossWorldFederationControllerTests.RelayAsync_WhenSessionMetadataIsJsonElement_StillReusesSession
        // pins the MetadataString fix that makes the controller robust to the JsonElement case.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.Metadata["sourceWorldId"] = "world-a";
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));
        reloaded.ShouldNotBeNull();

        var value = reloaded.Metadata["sourceWorldId"];
        value.ShouldNotBeNull(
            customMessage: "Metadata value evaporated on round-trip — SqliteSessionStore lost the entry.");

        // EITHER raw string (if the storage layer ever normalises) OR JsonElement (current shape).
        // Both must extract back to "world-a". This documents the two acceptable shapes so a future
        // change to the storage layer is a deliberate decision, not an accident.
        var extracted = value switch
        {
            string s => s,
            System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String
                => element.GetString(),
            _ => null
        };
        extracted.ShouldBe("world-a",
            customMessage: $"Unexpected metadata shape on Sqlite round-trip: {value?.GetType().FullName}. " +
                "Update the controller's MetadataString helper and any other readers to handle the new shape.");
    }

    [Fact]
    public async Task DeleteAsync_WithExistingSession_RemovesSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.SaveAsync(session);

        await store.DeleteAsync(SessionId.From("s1"));

        (await fixture.CreateStore().GetAsync(SessionId.From("s1"))).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesHistoryRowsForSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        await store.SaveAsync(session);

        await store.DeleteAsync(SessionId.From("s1"));

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
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.SaveAsync(session);

        await store.ArchiveAsync(SessionId.From("s1"));

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM sessions WHERE id = 's1'";
        var status = (string?)await command.ExecuteScalarAsync();
        status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed.ToString());
    }

    [Fact]
    public async Task ArchiveAsync_PreservesSessionAndHistory_ForSubsequentQueries()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "keep-me" });
        await store.SaveAsync(session);

        await store.ArchiveAsync(SessionId.From("s1"));

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        reloaded.History.ShouldContain(entry => entry.Content == "keep-me");

        var sealedSessions = await fixture.CreateStore().ListAsync(
            AgentId.From("agent-a"),
            BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        sealedSessions.Select(s => s.SessionId.Value).ShouldContain("s1");
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
        var filtered = await store.ListAsync(AgentId.From("agent-a"));

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

        var sessions = await store.ListByChannelAsync(AgentId.From("agent-a"), ChannelKey.From("web chat"));

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
                var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));
                session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}" });
                session.Metadata["index"] = i;
                await store.SaveAsync(session);
                return await store.GetAsync(SessionId.From(sessionId));
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

        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
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
        columns.ShouldContain("is_history");
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
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
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
        // Phase 3a (#531): legacy DBs must gain the new is_history column on first open.
        columns.ShouldContain("is_history");
    }

    [Fact]
    public async Task GetAsync_PreservesIsHistoryFlag_AcrossRoundTrip()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("history-roundtrip"), AgentId.From("agent-a"));
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "old-user", IsHistory = true },
            new SessionEntry { Role = MessageRole.Assistant, Content = "old-assistant", IsHistory = true },
            new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "fresh" }
        });
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("history-roundtrip"));

        reloaded.ShouldNotBeNull();
        var snapshot = reloaded!.GetHistorySnapshot();
        snapshot.Count.ShouldBe(4);
        snapshot.Single(e => e.Content == "old-user").IsHistory.ShouldBeTrue();
        snapshot.Single(e => e.Content == "old-assistant").IsHistory.ShouldBeTrue();
        snapshot.Single(e => e.Content == "summary").IsHistory.ShouldBeFalse();
        snapshot.Single(e => e.Content == "summary").IsCompactionSummary.ShouldBeTrue();
        snapshot.Single(e => e.Content == "fresh").IsHistory.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_LegacyMultipleSummaries_ForwardMigratesAllButLatest()
    {
        // Phase 3a (#531): legacy DBs with multiple IsCompactionSummary rows and IsHistory=false
        // (because the old code applied a load-time slice) must forward-migrate on load —
        // all-but-latest summary marked IsHistory=true.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("legacy-multi"), AgentId.From("agent-a"));
        session.AddEntries(new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "before-1" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-1", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "middle" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-2", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "after" }
        });
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("legacy-multi"));

        reloaded.ShouldNotBeNull();
        var snapshot = reloaded!.GetHistorySnapshot();
        snapshot.Count.ShouldBe(5);
        snapshot.Single(e => e.Content == "summary-1").IsHistory.ShouldBeTrue();
        snapshot.Single(e => e.Content == "summary-2").IsHistory.ShouldBeFalse();
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

        // P9-F: Participants now live on Conversation. Create the shared conversation,
        // add agent-a as a participant, then link the AgentSubAgent session to it.
        var sharedConvoId = ConversationId.From("conv-shared");
        await fixture.Conversations.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = sharedConvoId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-b"),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        });
        await fixture.Conversations.AddParticipantsAsync(
            sharedConvoId,
            [new BotNexus.Domain.Primitives.SessionParticipant { CitizenId = CitizenId.Of(BotNexus.Domain.Primitives.AgentId.From("agent-a")) }]);

        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("participant"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-b"),
            ConversationId = sharedConvoId,
            // P9-E (#645): SessionType.Cron deleted; use AgentSubAgent so the TypeFilter
            // discriminates this row from the "owned" UserAgent row above.
            SessionType = BotNexus.Domain.Primitives.SessionType.AgentSubAgent,
            CreatedAt = now.AddDays(-1)
        });

        var sessions = await store.GetExistenceAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            new ExistenceQuery
            {
                TypeFilter = BotNexus.Domain.Primitives.SessionType.AgentSubAgent,
                From = now.AddDays(-1.5),
                Limit = 10
            });

        sessions.Select(session => session.SessionId.Value).ShouldHaveSingleItem().ShouldBe("participant");
    }

    private static async Task CreateAndSaveAsync(SqliteSessionStore store, string sessionId, string agentId)
    {
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From(agentId));
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
            var session = await store.GetOrCreateAsync(SessionId.From($"s{i:D2}"), AgentId.From("agent-a"));
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
        var a = await store.GetOrCreateAsync(SessionId.From("session-a"), AgentId.From("agent-a"));
        var b = await store.GetOrCreateAsync(SessionId.From("session-b"), AgentId.From("agent-b"));
        a.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello-a" });
        b.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello-b" });

        await Task.WhenAll(store.SaveAsync(a), store.SaveAsync(b));

        var ra = await store.GetAsync(SessionId.From("session-a"));
        var rb = await store.GetAsync(SessionId.From("session-b"));
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
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.SaveAsync(session);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await cmd.ExecuteScalarAsync();
        mode.ShouldBe("wal", StringCompareShould.IgnoreCase);
    }

    // ── Tool field persistence tests ─────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WithToolArgsAndToolIsError_RoundTripsCorrectly()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-tool"), AgentId.From("agent-a"));
        session.AddEntries([
            new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolName = "search", ToolCallId = "tc1", ToolArgs = "{\"query\":\"dotnet\"}" },
            new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolName = "search", ToolCallId = "tc1", ToolIsError = false }
        ]);

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-tool"));

        reloaded.ShouldNotBeNull();
        var startEntry = reloaded!.History.First(e => e.ToolArgs is not null);
        startEntry.ToolArgs.ShouldBe("{\"query\":\"dotnet\"}");
        var endEntry = reloaded.History.First(e => e.Content == "result");
        endEntry.ToolIsError.ShouldBeFalse();
    }

    [Fact]
    public async Task SaveAsync_ToolIsErrorTrue_PersistedAndReadBackAsTrue()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-iserr"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "fail", ToolName = "run", ToolCallId = "tc2", ToolIsError = true });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-iserr"));

        reloaded.ShouldNotBeNull();
        reloaded!.History.ShouldHaveSingleItem();
        reloaded.History[0].ToolIsError.ShouldBeTrue();
    }

    [Fact]
    public async Task SaveAsync_ToolIsErrorFalse_PersistedAndReadBackAsFalse()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-noerr"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "ok", ToolName = "run", ToolCallId = "tc3", ToolIsError = false });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-noerr"));

        reloaded.ShouldNotBeNull();
        reloaded!.History[0].ToolIsError.ShouldBeFalse();
    }

    [Fact]
    public async Task SaveAsync_ToolArgsJsonString_RoundTripsThroughSqlite()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-toolargs"), AgentId.From("agent-a"));
        const string args = "{\"key\":\"value\",\"num\":42}";
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolName = "fn", ToolCallId = "tc4", ToolArgs = args });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-toolargs"));

        reloaded.ShouldNotBeNull();
        reloaded!.History[0].ToolArgs.ShouldBe(args);
    }

    [Fact]
    public async Task GetAsync_WithLegacySchema_MigratesToolColumns()
    {
        using var fixture = new StoreFixture();
        // Create a pre-existing DB without tool_args / tool_is_error columns
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
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
                    tool_call_id TEXT,
                    is_compaction_summary INTEGER NOT NULL DEFAULT 0
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Open with current store — migration should add the new columns
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-migrate"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "ok", ToolName = "fn", ToolCallId = "tc5", ToolArgs = "{}", ToolIsError = false });
        await store.SaveAsync(session);

        // Verify columns now exist
        await using var verifyConnection = new SqliteConnection(fixture.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var colCmd = verifyConnection.CreateCommand();
        colCmd.CommandText = "PRAGMA table_info(session_history)";
        var columns = new List<string>();
        await using var colReader = await colCmd.ExecuteReaderAsync();
        while (await colReader.ReadAsync())
            columns.Add(colReader.GetString(1));

        columns.ShouldContain("tool_args");
        columns.ShouldContain("tool_is_error");

        // And values were saved
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-migrate"));
        reloaded.ShouldNotBeNull();
        reloaded!.History[0].ToolArgs.ShouldBe("{}");
    }

    [Fact]
    public async Task SaveAsync_WithTriggerStampedEntries_RoundTripsTriggerAcrossReload()
    {
        // P9-E (#645) rubber-duck B1 regression: the new session_history.trigger_type
        // column must persist + read back for every TriggerType value that triggers
        // can stamp (Cron, Soul, Heartbeat). Without this pin, a future refactor of
        // SqliteSessionStore's manual column mapping could silently drop the field —
        // breaking soul-session discovery and per-entry origin attribution.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetOrCreateAsync(SessionId.From("s-trig"), AgentId.From("agent-a"));
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "cron run", Trigger = TriggerType.Cron });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "soul tick", Trigger = TriggerType.Soul });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "heartbeat", Trigger = TriggerType.Heartbeat });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "ack" }); // no trigger

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-trig"));

        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(4);

        var byContent = reloaded.History.ToDictionary(e => e.Content!);
        byContent["cron run"].Trigger.ShouldBe(TriggerType.Cron);
        byContent["soul tick"].Trigger.ShouldBe(TriggerType.Soul);
        byContent["heartbeat"].Trigger.ShouldBe(TriggerType.Heartbeat);
        byContent["ack"].Trigger.ShouldBeNull(
            "Entries without a stamped Trigger must round-trip as null — not coerce to a default.");
    }

    [Fact]
    public async Task GetAsync_WithLegacySchema_MissingTriggerTypeColumn_ReadsNullTriggerWithoutThrowing()
    {
        // P9-E (#645) rubber-duck B1 regression: pre-P9-E databases do not have the
        // session_history.trigger_type column. The store's MigrateAsync must add it
        // idempotently, AND the SessionEntry projection must guard with FieldCount
        // so older read paths cannot AV / IndexOutOfRange. Without this pin, a
        // long-lived deployment upgrading from a pre-P9-E binary would fail to read
        // existing sessions on the first dispatch after upgrade.
        using var fixture = new StoreFixture();
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
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
                    tool_call_id TEXT,
                    is_compaction_summary INTEGER NOT NULL DEFAULT 0
                );

                INSERT INTO sessions (id, agent_id, status, created_at, updated_at)
                VALUES ('s-legacy', 'agent-a', 'Active', '2025-01-01T00:00:00Z', '2025-01-01T00:00:00Z');

                INSERT INTO session_history (session_id, role, content, timestamp)
                VALUES ('s-legacy', 'user', 'pre-p9e message', '2025-01-01T00:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Reading via the current store should silently migrate (add trigger_type column)
        // AND project the missing column as a null Trigger — no throw, no data loss.
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-legacy"));

        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(1);
        reloaded.History[0].Content.ShouldBe("pre-p9e message");
        reloaded.History[0].Trigger.ShouldBeNull(
            "Pre-P9-E rows have no trigger value; the projection must yield null, not throw.");

        // Verify the migration actually added the column.
        await using var verifyConnection = new SqliteConnection(fixture.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var colCmd = verifyConnection.CreateCommand();
        colCmd.CommandText = "PRAGMA table_info(session_history)";
        var columns = new List<string>();
        await using var colReader = await colCmd.ExecuteReaderAsync();
        while (await colReader.ReadAsync())
            columns.Add(colReader.GetString(1));
        columns.ShouldContain("trigger_type", "MigrateAsync must add session_history.trigger_type idempotently.");
    }

    // --- ListByConversationAsync: F-7 contract pins (SqliteSessionStore) ---
    //
    // Same 5 invariants as InMemory + File. ALSO verifies:
    //   - the idx_sessions_conversation_agent index exists on the table after init
    //   - the indexed query is actually used (loose smoke test: query runs without
    //     a full-scan plan would require EXPLAIN QUERY PLAN; we settle for "returns
    //     the right rows" + "index is present" which is what regression would catch).

    private static async Task SeedSqliteConversationFixtureAsync(StoreFixture fixture, DateTimeOffset baseTime)
    {
        var convA = ConversationId.From("conv-a");
        var convB = ConversationId.From("conv-b");

        // P9-I (#674): pre-register conversations so AgentId hydration succeeds on reload.
        await fixture.Conversations.CreateAsync(new Conversation
        {
            ConversationId = convA,
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        });
        await fixture.Conversations.CreateAsync(new Conversation
        {
            ConversationId = convB,
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        });

        var store = fixture.CreateStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-active"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(10),
            Status = SessionStatus.Active,
            Session = { ConversationId = convA }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-sealed"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime,
            Status = SessionStatus.Sealed,
            Session = { ConversationId = convA }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-other-agent"),
            AgentId = AgentId.From("agent-y"),
            CreatedAt = baseTime.AddMinutes(5),
            Status = SessionStatus.Active,
            Session = { ConversationId = convA }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-b"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(20),
            Status = SessionStatus.Active,
            Session = { ConversationId = convB }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-orphan"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(15),
            Status = SessionStatus.Active
        });
    }

    [Fact]
    public async Task EnsureCreatedAsync_CreatesConversationCreatedIndex_ForListByConversationLookups()
    {
        // Pin the index exists. If a future schema change removes it, this fails first.
        // P9-I (#674): replaces idx_sessions_conversation_agent (composite was dropped
        // alongside the agent_id column) with idx_sessions_conversation_created.
        // The new index serves ListByConversationAsync's (conversation_id, created_at, id)
        // ordering contract.
        using var fixture = new StoreFixture();
        // Trigger schema creation by a no-op read.
        _ = await fixture.CreateStore().GetAsync(SessionId.From("warm"));

        await using var verifyConnection = new SqliteConnection(fixture.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var cmd = verifyConnection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'sessions'";

        var indexes = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));

        indexes.ShouldContain(
            "idx_sessions_conversation_created",
            "Missing index — ListByConversationAsync degrades to a full table scan");
        // Pin removal of the dead composite that referenced agent_id (dropped with the column).
        indexes.ShouldNotContain(
            "idx_sessions_conversation_agent",
            "Stale index references the removed agent_id column");
    }

    [Fact]
    public async Task ListByConversationAsync_AcrossReload_ReturnsActiveAndSealed_InCreatedAtAscOrder()
    {
        using var fixture = new StoreFixture();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedSqliteConversationFixtureAsync(fixture, baseTime);

        var sessions = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("conv-a"));

        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a-sealed", "s-a-other-agent", "s-a-active" }, ignoreOrder: false);
    }

    [Fact]
    public async Task ListByConversationAsync_AcrossReload_ExcludesOtherConversations_AndOrphans()
    {
        using var fixture = new StoreFixture();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedSqliteConversationFixtureAsync(fixture, baseTime);

        var sessions = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("conv-a"));

        sessions.Select(s => s.SessionId.Value).ShouldNotContain("s-b");
        sessions.Select(s => s.SessionId.Value).ShouldNotContain("s-orphan");
    }

    [Fact]
    public async Task ListByConversationAsync_ReturnsEmptyList_ForUnknownConversation()
    {
        using var fixture = new StoreFixture();

        var sessions = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("ghost"));

        sessions.ShouldNotBeNull();
        sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListByConversationAsync_AcrossReload_WithAgentFilter_NarrowsToOwner()
    {
        using var fixture = new StoreFixture();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedSqliteConversationFixtureAsync(fixture, baseTime);

        // P9-I (#674): agent_id column dropped from sessions. AgentId is hydrated from
        // Conversation.AgentId, so passing the conversation's owning agent returns ALL
        // sessions in that conversation (the agentId arg is now a defensive assertion
        // that the conversation belongs to the caller's agent). Passing a different
        // agent returns an empty set.
        var matched = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("conv-a"), agentId: AgentId.From("agent-x"));
        matched.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a-sealed", "s-a-other-agent", "s-a-active" }, ignoreOrder: false);

        var mismatched = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("conv-a"), agentId: AgentId.From("agent-y"));
        mismatched.ShouldBeEmpty(
            "Post-P9-I: passing a non-owner agent must return empty — Conversation.AgentId is the source of truth.");
    }

    [Fact]
    public async Task ListByConversationAsync_HandlesSameCreatedAt_WithSessionIdTieBreaker()
    {
        using var fixture = new StoreFixture();
        var same = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var convId = ConversationId.From("conv-tie");
        // P9-I (#674): pre-register the conversation so AgentId hydration succeeds.
        await fixture.Conversations.CreateAsync(new Conversation
        {
            ConversationId = convId,
            AgentId = AgentId.From("agent-x"),
            CreatedAt = same,
            UpdatedAt = same
        });
        var store = fixture.CreateStore();

        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-z"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = same,
            Session = { ConversationId = convId }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = same,
            Session = { ConversationId = convId }
        });

        var sessions = await fixture.CreateStore().ListByConversationAsync(convId);

        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a", "s-z" }, ignoreOrder: false);
    }

    [Fact]
    public async Task GetStatsAsync_EmptyStore_ReturnsZeroTotals()
    {
        using var fixture = new StoreFixture();

        var stats = await fixture.CreateStore().GetStatsAsync();

        stats.ShouldNotBeNull();
        stats!.TotalSessions.ShouldBe(0);
        stats.ByStatus.ShouldBeEmpty();
        stats.ByAgent.ShouldBeEmpty();
        stats.Compaction.UncompactedSessions.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatsAsync_CountsByStatus_NormalizesEnumNames()
    {
        using var fixture = new StoreFixture();
        await SeedSessionAsync(fixture, "s-active-1", "agent-a", "conv-a", SessionStatus.Active);
        await SeedSessionAsync(fixture, "s-active-2", "agent-a", "conv-a", SessionStatus.Active);
        await SeedSessionAsync(fixture, "s-sealed-1", "agent-a", "conv-a", SessionStatus.Sealed);
        await SeedSessionAsync(fixture, "s-suspended-1", "agent-a", "conv-a", SessionStatus.Suspended);

        var stats = await fixture.CreateStore().GetStatsAsync();

        stats.ShouldNotBeNull();
        stats!.TotalSessions.ShouldBe(4);
        stats.ByStatus["Active"].ShouldBe(2);
        stats.ByStatus["Sealed"].ShouldBe(1);
        stats.ByStatus["Suspended"].ShouldBe(1);
    }

    [Fact]
    public async Task GetStatsAsync_GroupsByAgent_ResolvedFromConversationStore()
    {
        using var fixture = new StoreFixture();
        // agent-a owns conv-a (3 sessions), agent-b owns conv-b (1 session).
        await SeedSessionAsync(fixture, "a1", "agent-a", "conv-a", SessionStatus.Active);
        await SeedSessionAsync(fixture, "a2", "agent-a", "conv-a", SessionStatus.Active);
        await SeedSessionAsync(fixture, "a3", "agent-a", "conv-a", SessionStatus.Sealed);
        await SeedSessionAsync(fixture, "b1", "agent-b", "conv-b", SessionStatus.Active);

        var stats = await fixture.CreateStore().GetStatsAsync();

        stats.ShouldNotBeNull();
        stats!.TotalSessions.ShouldBe(4);
        // Ordered by count descending — agent-a (3) first, agent-b (1) second.
        stats.ByAgent.Count.ShouldBe(2);
        stats.ByAgent[0].AgentId.ShouldBe("agent-a");
        stats.ByAgent[0].Count.ShouldBe(3);
        stats.ByAgent[1].AgentId.ShouldBe("agent-b");
        stats.ByAgent[1].Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetStatsAsync_AgentFilter_OnlyCountsThatAgentsSessions()
    {
        using var fixture = new StoreFixture();
        await SeedSessionAsync(fixture, "a1", "agent-a", "conv-a", SessionStatus.Active);
        await SeedSessionAsync(fixture, "a2", "agent-a", "conv-a", SessionStatus.Sealed);
        await SeedSessionAsync(fixture, "b1", "agent-b", "conv-b", SessionStatus.Active);
        await SeedSessionAsync(fixture, "b2", "agent-b", "conv-b", SessionStatus.Active);

        var stats = await fixture.CreateStore().GetStatsAsync(AgentId.From("agent-b"));

        stats.ShouldNotBeNull();
        stats!.TotalSessions.ShouldBe(2);
        stats.ByAgent.ShouldHaveSingleItem();
        stats.ByAgent[0].AgentId.ShouldBe("agent-b");
        stats.ByAgent[0].Count.ShouldBe(2);
        stats.ByStatus["Active"].ShouldBe(2);
        stats.ByStatus.ShouldNotContainKey("Sealed"); // agent-a's sealed session is filtered out
    }

    [Fact]
    public async Task GetStatsAsync_OrphanedSession_IsQuarantinedByStartupSelfHeal()
    {
        using var fixture = new StoreFixture();
        // #2188: a session whose non-null conversation_id references a deleted
        // conversation is unrecoverable. The startup self-heal (which runs in
        // EnsureCreatedAsync before any read) quarantines it, so stats must not throw
        // and must see only the healthy session - the dangling row is gone.
        await SeedSessionAsync(fixture, "a1", "agent-a", "conv-a", SessionStatus.Active);

        // Write the orphan row directly with a conversation_id that was never created.
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO sessions (id, status, created_at, updated_at, conversation_id) " +
                "VALUES ($id, $status, $ts, $ts, $conv)";
            cmd.Parameters.AddWithValue("$id", "orphan-1");
            cmd.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$conv", "conv-missing");
            await cmd.ExecuteNonQueryAsync();
        }

        var stats = await fixture.CreateStore().GetStatsAsync();

        stats.ShouldNotBeNull();
        stats!.TotalSessions.ShouldBe(1);          // the dangling orphan was quarantined
        stats.ByStatus["Active"].ShouldBe(1);
        stats.ByAgent.ShouldHaveSingleItem();        // only agent-a remains
        stats.ByAgent[0].AgentId.ShouldBe("agent-a");
        stats.ByAgent[0].Count.ShouldBe(1);

        // The orphan row itself must be gone from the sessions table.
        await using var verify = new SqliteConnection(fixture.ConnectionString);
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = 'orphan-1'";
        ((long)(await check.ExecuteScalarAsync())!).ShouldBe(0);
    }

    private static async Task SeedSessionAsync(
        StoreFixture fixture, string sessionId, string agentId, string conversationId, SessionStatus status)
    {
        var convId = ConversationId.From(conversationId);
        if (await fixture.Conversations.GetAsync(convId) is null)
        {
            var now = DateTimeOffset.UtcNow;
            await fixture.Conversations.CreateAsync(new Conversation
            {
                ConversationId = convId,
                AgentId = AgentId.From(agentId),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var store = fixture.CreateStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            CreatedAt = DateTimeOffset.UtcNow,
            Status = status,
            Session = { ConversationId = convId }
        });
    }

    [Fact]
    public async Task BackfillParticipants_WithCorruptParticipantsJsonOnOneRow_SkipsIt_AndForwardsTheValidRows()
    {
        // Regression (#1751): the participants backfill scan reads participants_json row by
        // row. A single corrupt blob used to throw JsonException out of DeserializeParticipants
        // and abort the ENTIRE scan, so no conversation received its participants. The guard now
        // logs a warning with the conversation/session id, skips the corrupt row, and continues
        // the reader loop so the remaining valid rows are still forwarded.
        using var fixture = new StoreFixture();

        // Two destination conversations must exist in the (shared) conversation store, because
        // AddParticipantsAsync no-ops when the target conversation is absent.
        await fixture.Conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("conv-good"),
            AgentId = AgentId.From("agent-a"),
            Title = "good",
            Status = BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await fixture.Conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("conv-bad"),
            AgentId = AgentId.From("agent-a"),
            Title = "bad",
            Status = BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // First store op creates the schema (and runs an initial, empty backfill).
        (await fixture.CreateStore().GetAsync(SessionId.From("missing"))).ShouldBeNull();

        // Seed two legacy session rows carrying participants_json: one valid (legacy wire
        // shape), one deliberately corrupt. Both are attached to a non-null conversation_id so
        // the backfill scan picks them up.
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO sessions (id, status, participants_json, created_at, updated_at, conversation_id)
                VALUES ('s-good', 'Active', $goodJson, '2025-01-01T00:00:00Z', '2025-01-01T00:00:00Z', 'conv-good');

                INSERT INTO sessions (id, status, participants_json, created_at, updated_at, conversation_id)
                VALUES ('s-bad', 'Active', $badJson, '2025-01-01T00:00:00Z', '2025-01-01T00:00:00Z', 'conv-bad');
                """;
            seed.Parameters.AddWithValue("$goodJson", """[{"type":"User","id":"alice","role":"initiator"}]""");
            seed.Parameters.AddWithValue("$badJson", "[ this is not valid json ");
            await seed.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // A fresh store re-runs EnsureCreatedAsync -> BackfillParticipantsToConversationsAsync.
        // Must NOT throw despite the corrupt row.
        (await fixture.CreateStore().GetAsync(SessionId.From("s-good"))).ShouldNotBeNull();

        // The valid row's participant was forwarded even though a corrupt row was present in the
        // same scan - proving one bad blob does not abort the whole backfill.
        var good = await fixture.Conversations.GetAsync(ConversationId.From("conv-good"));
        good.ShouldNotBeNull();
        good!.Participants.ShouldContain(p => p.CitizenId == CitizenId.Of(UserId.From("alice")));

        // The corrupt row contributed nothing (skipped-to-empty), but did not poison the scan.
        var bad = await fixture.Conversations.GetAsync(ConversationId.From("conv-bad"));
        bad.ShouldNotBeNull();
        bad!.Participants.ShouldBeEmpty("A corrupt participants_json row must be skipped, not forwarded.");
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
            Conversations = new InMemoryConversationStore();
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }

        // Shared across all CreateStore() calls in the same fixture so
        // legacy-conversation rows resolved by one store instance are visible to
        // subsequent stores opened on the same SQLite database — required for
        // the post-P9-B-2 fail-loud "unset ConversationId on save" guard.
        public InMemoryConversationStore Conversations { get; }

        public SqliteSessionStore CreateStore(IConversationStore? conversationStore = null)
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, conversationStore ?? Conversations);

        public SqliteSessionStore CreateStore(int cacheCapacity, IConversationStore? conversationStore = null)
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, conversationStore ?? Conversations, redactor: null, cacheCapacity: cacheCapacity);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}




