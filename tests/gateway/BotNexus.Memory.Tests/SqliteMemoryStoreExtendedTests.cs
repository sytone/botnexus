using System.Text;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests;

public sealed class SqliteMemoryStoreExtendedTests : IDisposable
{
    [Fact]
    public async Task InsertAndGet_WithAllFieldsPopulated_RoundTrips()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var expected = MemoryStoreTestContext.CreateEntry(
            id: "full-1",
            agentId: "agent-all",
            content: "all fields content",
            sourceType: "manual",
            sessionId: "session-all",
            turnIndex: 42,
            createdAt: now,
            metadataJson: """{"tags":["alpha","beta"]}""") with
        {
            Embedding = [1, 2, 3, 4],
            UpdatedAt = now.AddMinutes(1),
            ExpiresAt = now.AddDays(7),
            IsArchived = true
        };

        await context.Store.InsertAsync(expected);
        var actual = await context.Store.GetByIdAsync("full-1");

        actual.ShouldNotBeNull();
        actual!.Id.ShouldBe(expected.Id);
        actual.AgentId.ShouldBe(expected.AgentId);
        actual.SessionId.ShouldBe(expected.SessionId);
        actual.TurnIndex.ShouldBe(expected.TurnIndex);
        actual.SourceType.ShouldBe(expected.SourceType);
        actual.Content.ShouldBe(expected.Content);
        actual.MetadataJson.ShouldBe(expected.MetadataJson);
        actual.Embedding.ShouldBe(expected.Embedding);
        actual.CreatedAt.ShouldBe(expected.CreatedAt);
        actual.UpdatedAt.ShouldBe(expected.UpdatedAt);
        actual.ExpiresAt.ShouldBe(expected.ExpiresAt);
        actual.IsArchived.ShouldBe(expected.IsArchived);
    }

    [Fact]
    public async Task Search_WithSessionAndTagFilters_ReturnsMatchingEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-1", "agent-a", "filtertoken", sessionId: "session-1", metadataJson: """{"tags":["billing","prod"]}"""));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-2", "agent-a", "filtertoken", sessionId: "session-2", metadataJson: """{"tags":["billing"]}"""));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("entry-3", "agent-a", "filtertoken", sessionId: "session-1", metadataJson: """{"tags":["dev"]}"""));

        var results = await context.Store.SearchAsync(
            "filtertoken",
            filter: new MemorySearchFilter { SessionId = "session-1", Tags = ["billing", "prod"] });

        results.ShouldHaveSingleItem();
        results[0].Id.ShouldBe("entry-1");
    }

    [Fact]
    public async Task Search_WithDateAndSourceFilters_ReturnsExpectedEntries()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("old", "agent-a", "range-token", sourceType: "manual", createdAt: now.AddDays(-10)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("middle", "agent-a", "range-token", sourceType: "manual", createdAt: now.AddDays(-2)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("new", "agent-a", "range-token", sourceType: "conversation", createdAt: now));

        var results = await context.Store.SearchAsync(
            "range-token",
            filter: new MemorySearchFilter
            {
                SourceType = "manual",
                AfterDate = now.AddDays(-3),
                BeforeDate = now.AddHours(-1)
            });

        results.ShouldHaveSingleItem().Id.ShouldBe("middle");
    }

    [Fact]
    public async Task DeleteAsync_ExistingEntry_RemovesIt()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("delete-me", "agent-a", "delete token"));

        await context.Store.DeleteAsync("delete-me");

        (await context.Store.GetByIdAsync("delete-me")).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_MissingEntry_DoesNotThrow()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var act = () => context.Store.DeleteAsync("missing-id");

        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task EmptyStore_BehavesAsExpected()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var search = await context.Store.SearchAsync("nothing");
        var stats = await context.Store.GetStatsAsync();
        var get = await context.Store.GetByIdAsync("none");

        search.ShouldBeEmpty();
        stats.EntryCount.ShouldBe(0);
        stats.LastIndexedAt.ShouldBeNull();
        get.ShouldBeNull();
    }

    [Fact]
    public async Task Search_WithSpecialCharacters_DoesNotBreakAndFindsContent()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        const string content = "special token sql'quote \"double\" (paren) +plus -minus";
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("special", "agent-a", content));

        var results = await context.Store.SearchAsync("special token sql quote double paren plus minus");

        results.ShouldHaveSingleItem().Id.ShouldBe("special");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Search_SQLInjectionPayload_DoesNotDeleteData()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("secure-1", "agent-a", "safe-content-token"));

        var results = await context.Store.SearchAsync("""token' OR 1=1; DROP TABLE memories; --""");
        var stats = await context.Store.GetStatsAsync();

        results.ShouldBeEmpty();
        stats.EntryCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TagsFilter_WithInjectionPayload_DoesNotBypassFiltering()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("tag-safe", "agent-a", "tagged-token", metadataJson: """{"tags":["safe"]}"""));

        var results = await context.Store.SearchAsync(
            "tagged-token",
            filter: new MemorySearchFilter { Tags = ["safe' OR 1=1 --"] });

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task VeryLongContent_CanBeStoredAndSearched()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var content = $"{new string('x', 20_000)} long-token-{Guid.NewGuid():N}";
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("long-content", "agent-a", content));

        var results = await context.Store.SearchAsync("long token");

        results.ShouldContain(entry => entry.Id == "long-content");
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_AreSupported()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var writeTasks = Enumerable.Range(0, 40)
            .Select(i => context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry($"concurrent-{i}", "agent-a", $"batch token {i}")));
        await Task.WhenAll(writeTasks);

        var readTasks = Enumerable.Range(0, 20)
            .Select(_ => context.Store.SearchAsync("batch token", topK: 10));
        var results = await Task.WhenAll(readTasks);

        results.ShouldAllBe(set => set.Count > 0);
    }

    [Fact]
    public async Task Dispose_ThenReopenStore_PersistsEntries()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "memory.db");
        await using (var store = new SqliteMemoryStore(dbPath, new FileSystem()))
        {
            await store.InitializeAsync();
            await store.InsertAsync(MemoryStoreTestContext.CreateEntry("persisted-1", "agent-a", "persist me"));
        }

        await using (var reopened = new SqliteMemoryStore(dbPath, new FileSystem()))
        {
            await reopened.InitializeAsync();
            var loaded = await reopened.GetByIdAsync("persisted-1");
            loaded.ShouldNotBeNull();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(tempDirectory, true);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnicodeEdgeCases_AreStoredAndRetrieved()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var unicode = "emoji 😀 rtl \u05D0\u05D1\u05D2 zero\u200Bwidth";
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("unicode-1", "agent-a", unicode));

        var loaded = await context.Store.GetByIdAsync("unicode-1");

        loaded.ShouldNotBeNull();
        loaded!.Content.ShouldBe(unicode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VeryLargeBatchInsert_CompletesAndUpdatesStats()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        for (var i = 0; i < 200; i++)
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry($"bulk-{i}", "agent-a", $"bulk token {i}"));

        var stats = await context.Store.GetStatsAsync();
        stats.EntryCount.ShouldBe(200);
    }

    [Fact]
    public async Task Search_RespectsTopKClamp()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        for (var i = 0; i < 130; i++)
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry($"topk-{i}", "agent-a", "topk-clamp-token"));

        var results = await context.Store.SearchAsync("topk clamp token", topK: 500);

        results.Count.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task ArchivedEntries_AreExcludedFromSearch()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("live", "agent-a", "archive-token"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("archived", "agent-a", "archive-token") with { IsArchived = true });

        var results = await context.Store.SearchAsync("archive token", topK: 10);

        results.ShouldHaveSingleItem().Id.ShouldBe("live");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
