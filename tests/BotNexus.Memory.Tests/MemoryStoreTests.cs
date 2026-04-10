using BotNexus.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;
using FluentAssertions;
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

        names.Should().Contain(["memories", "memories_fts", "schema_version", "memories_ai", "memories_ad", "memories_au"]);
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

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("entry-1");
        loaded.AgentId.Should().Be("agent-a");
        loaded.SessionId.Should().Be("session-1");
        loaded.TurnIndex.Should().Be(3);
        loaded.Content.Should().Be("Remember this message.");
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

        results.Select(entry => entry.Id).Should().Equal("entry-2", "entry-1");
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingEntries_ByFTS5()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "copilotmemorywaveonetesting"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "totally unrelated text"));

        var results = await context.Store.SearchAsync("copilotmemorywaveonetesting");

        results.Should().ContainSingle();
        results[0].Id.Should().Be("entry-1");
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyForNoMatch()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "some indexed text"));

        var results = await context.Store.SearchAsync("nonexistent-term");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_AppliesTemporalDecay_RecentScoresHigher()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-old", "agent-a", "rankingkeyword", createdAt: now.AddDays(-90)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-recent", "agent-a", "rankingkeyword", createdAt: now.AddHours(-1)));

        var results = await context.Store.SearchAsync("rankingkeyword", 2);

        results.Select(entry => entry.Id).Should().Equal("entry-recent", "entry-old");
    }

    [Fact]
    public async Task SearchAsync_WithSourceTypeFilter_FiltersCorrectly()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "sharedkeyword", sourceType: "manual"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "sharedkeyword", sourceType: "conversation"));

        var results = await context.Store.SearchAsync("sharedkeyword", 10, new MemorySearchFilter { SourceType = "manual" });

        results.Should().ContainSingle();
        results[0].SourceType.Should().Be("manual");
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

        results.Should().ContainSingle();
        results[0].Id.Should().Be("entry-mid");
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "to be deleted"));

        await context.Store.DeleteAsync("entry-1");
        var loaded = await context.Store.GetByIdAsync("entry-1");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "alpha"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "beta"));

        await context.Store.ClearAsync();
        var stats = await context.Store.GetStatsAsync();

        stats.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectCounts()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "alpha", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "beta", createdAt: DateTimeOffset.UtcNow));

        var stats = await context.Store.GetStatsAsync();

        stats.EntryCount.Should().Be(2);
        stats.DatabaseSizeBytes.Should().BeGreaterThan(0);
        stats.LastIndexedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InsertAsync_WithDuplicateId_HandlesGracefully()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "first"));

        var act = () => context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "duplicate"));

        await act.Should().ThrowAsync<SqliteException>();
        var loaded = await context.Store.GetByIdAsync("entry-1");
        loaded!.Content.Should().Be("first");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
