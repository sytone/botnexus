using BotNexus.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests;

public sealed class MemoryStoreTests : IDisposable
{
    [Fact]
    public async Task InitializeAsync_CreatesSchemaAndTables()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={context.DbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type IN ('table','trigger')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        names.ShouldContain("memories");
        names.ShouldContain("memories_fts");
        names.ShouldContain("schema_version");
        names.ShouldContain("memories_ai");
        names.ShouldContain("memories_ad");
        names.ShouldContain("memories_au");
    }

    [Fact]
    public async Task InsertAsync_StoresAndRetrievesByIg()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var expected = MemoryStoreTestContext.CreateEntry(
            id: "entry-1",
            agentId: "agent-a",
            content: "Remember this message.",
            sessionId: "session-1",
            turnIndex: 3);

        var inserted = await context.Store.InsertAsync(expected);
        var loaded = await context.Store.GetByIdAsync(inserted.Id);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe("entry-1");
        loaded.AgentId.ShouldBe("agent-a");
        loaded.SessionId.ShouldBe("session-1");
        loaded.TurnIndex.ShouldBe(3);
        loaded.Content.ShouldBe("Remember this message.");
    }

    [Fact]
    public async Task GetBySessionAsync_ReturnsEntriesForSession()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "A", sessionId: "session-1", createdAt: now.AddMinutes(-5)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "B", sessionId: "session-1", createdAt: now));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-3", "agent-a", "C", sessionId: "session-2", createdAt: now.AddMinutes(-2)));

        var results = await context.Store.GetBySessionAsync("session-1", 10);

        results.Select(entry => entry.Id).ShouldBe(new[] { "entry-2", "entry-1" });
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingEntries_ByFTS5()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "copilotmemorywaveonetesting"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "totally unrelated text"));

        var results = await context.Store.SearchAsync("copilotmemorywaveonetesting");

        results.ShouldHaveSingleItem();
        results[0].Id.ShouldBe("entry-1");
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyForNoMatch()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "some indexed text"));

        var results = await context.Store.SearchAsync("nonexistent-term");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_AppliesTemporalDecay_RecentScoresHigher()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-old", "agent-a", "rankingkeyword", createdAt: now.AddDays(-90)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-recent", "agent-a", "rankingkeyword", createdAt: now.AddHours(-1)));

        var results = await context.Store.SearchAsync("rankingkeyword", 2);

        results.Select(entry => entry.Id).ShouldBe(new[] { "entry-recent", "entry-old" });
    }

    [Fact]
    public async Task SearchAsync_WithSourceTypeFilter_FiltersCorrectly()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "sharedkeyword", sourceType: "manual"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "sharedkeyword", sourceType: "conversation"));

        var results = await context.Store.SearchAsync("sharedkeyword", 10, new MemorySearchFilter { SourceType = "manual" });

        results.ShouldHaveSingleItem();
        results[0].SourceType.ShouldBe("manual");
    }

    [Fact]
    public async Task SearchAsync_WithDateRangeFilter_FiltersCorrectly()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-old", "agent-a", "timewindowkeyword", createdAt: now.AddDays(-10)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-mid", "agent-a", "timewindowkeyword", createdAt: now.AddDays(-2)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-new", "agent-a", "timewindowkeyword", createdAt: now));

        var results = await context.Store.SearchAsync(
            "timewindowkeyword",
            10,
            new MemorySearchFilter
            {
                AfterDate = now.AddDays(-3),
                BeforeDate = now.AddHours(-1)
            });

        results.ShouldHaveSingleItem();
        results[0].Id.ShouldBe("entry-mid");
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "to be deleted"));

        await context.Store.DeleteAsync("entry-1");
        var loaded = await context.Store.GetByIdAsync("entry-1");

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "alpha"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "beta"));

        await context.Store.ClearAsync();
        var stats = await context.Store.GetStatsAsync();

        stats.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectCounts()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "alpha", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "beta", createdAt: DateTimeOffset.UtcNow));

        var stats = await context.Store.GetStatsAsync();

        stats.EntryCount.ShouldBe(2);
        stats.DatabaseSizeBytes.ShouldBeGreaterThan(0);
        stats.LastIndexedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task InsertAsync_WithDuplicateId_HandlesGracefully()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "first"));

        var act = () => context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "duplicate"));

        await act.ShouldThrowAsync<SqliteException>();
        var loaded = await context.Store.GetByIdAsync("entry-1");
        loaded!.Content.ShouldBe("first");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
