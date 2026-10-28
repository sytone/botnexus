using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using Moq;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class KnowledgeSearchAllowlistTests
{
    [Fact]
    public async Task OmittedStore_ScopesBeforeLimit_AndMergesByScore()
    {
        var backend = new ScopedBackend(["Alpha", "Beta", "Denied"], [
            Hit("denied", "Denied", 100), Hit("a", "Alpha", 1),
            Hit("b", "Beta", 3), Hit("b2", "Beta", 2)]);
        var results = await Search(backend, ["alpha", "BETA", "Alpha", "missing"], limit: 2);
        Assert.Equal(new[] { "b", "b2" }, results.Select(r => r.Id));
        Assert.Equal(new[] { "Alpha", "Beta" }, backend.Searches.Select(s => s.Store));
        Assert.All(backend.Searches, s => Assert.Equal(2, s.Limit));
    }

    [Fact]
    public async Task BlankStore_IsTreatedAsOmitted()
    {
        foreach (var store in new[] { "", "   " })
        {
            var backend = new ScopedBackend(["Allowed", "Denied"], [Hit("a", "Allowed"), Hit("d", "Denied")]);
            Assert.Equal("a", Assert.Single(await Search(backend, ["allowed"], store)).Id);
            Assert.Equal("Allowed", Assert.Single(backend.Searches).Store);
        }
    }

    [Fact]
    public async Task ExplicitAllowedStore_UsesCanonicalDiscoveredName_AndFiltersBackendLeaks()
    {
        var backend = new ScopedBackend(["Allowed"], [Hit("a", "ALLOWED"), Hit("d", "Denied", 99)]) { LeakResults = true };
        var results = await Search(backend, ["allowed"], "aLLoWeD");
        Assert.Equal("a", Assert.Single(results).Id);
        Assert.Equal("Allowed", Assert.Single(backend.Searches).Store);
    }

    [Fact]
    public async Task OmittedStore_FiltersUnexpectedBackendResults()
    {
        var backend = new ScopedBackend(["Allowed"], [Hit("a", "Allowed"), Hit("d", "Denied", 99)]) { LeakResults = true };
        Assert.Equal("a", Assert.Single(await Search(backend, ["Allowed"])).Id);
    }

    [Fact]
    public async Task ExplicitDeniedStore_DoesNotCallBackend()
    {
        var backend = new ScopedBackend(["Denied"], []);
        var tool = new KnowledgeSearchTool(backend, new QmdConfig { AllowedStores = ["Allowed"] });
        var result = await tool.ExecuteAsync("test", new Dictionary<string, object?> { ["query"] = "query", ["store"] = "Denied" });
        Assert.Contains("Access denied", result.Content[0].Value);
        Assert.Empty(backend.Searches);
        Assert.Equal(0, backend.DiscoveryCalls);
    }

    [Fact]
    public async Task NoAvailableAllowedStores_DoesNotSearchGuessedNames()
    {
        var backend = new ScopedBackend(["Denied"], [Hit("d", "Denied")]);
        Assert.Empty(await Search(backend, ["missing"]));
        Assert.Empty(await Search(backend, ["missing"], "MISSING"));
        Assert.Empty(backend.Searches);
    }

    [Fact]
    public async Task NullAndEmptyAllowlists_PreserveUnrestrictedSearch()
    {
        foreach (var allowed in new List<string>?[] { null, [] })
        {
            var backend = new ScopedBackend(["A", "B"], [Hit("a", "A"), Hit("b", "B")]);
            Assert.Equal(2, (await Search(backend, allowed)).Length);
            Assert.Null(Assert.Single(backend.Searches).Store);
            Assert.Equal(0, backend.DiscoveryCalls);
        }
    }

    [Fact]
    public async Task Composite_ExplicitStore_OnlyCallsOwningBackend_WithCanonicalName()
    {
        var files = new ScopedBackend(["Files"], [Hit("f", "Files")]);
        var memory = new ScopedBackend(["memory:Team"], [Hit("m", "memory:Team")]);
        var composite = new CompositeQmdBackend([files, memory]);
        var results = await composite.SearchAsync("query", "MEMORY:TEAM", QmdSearchMode.Keyword, 5);
        Assert.Equal("m", Assert.Single(results).Id);
        Assert.Empty(files.Searches);
        Assert.Equal("memory:Team", Assert.Single(memory.Searches).Store);
    }

    [Fact]
    public async Task Composite_UnknownStore_DoesNotCallAnyBackend()
    {
        var files = new ScopedBackend(["Files"], []);
        Assert.Empty(await new CompositeQmdBackend([files]).SearchAsync("query", "missing", QmdSearchMode.Keyword, 5));
        Assert.Empty(files.Searches);
    }

    [Fact]
    public async Task Composite_WithRealMemoryBackend_IntersectsAllowlistAndRegistryPolicy()
    {
        var (memory, registry, team, excluded, secret) = CreateMemory();
        var files = new ScopedBackend(["Files"], [Hit("f", "Files", 100)]);
        var results = await Search(new CompositeQmdBackend([files, memory]), ["MEMORY:TEAM", "memory:Secret"], limit: 1);
        Assert.Equal("memory:Team/e", Assert.Single(results).Id);
        Assert.Empty(files.Searches);
        registry.Verify(r => r.CanRead("agent", "Team"), Times.Once);
        team.Verify(s => s.SearchAsync("query", 1, It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Once);
        excluded.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
        secret.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RealMemoryBackend_ExplicitAllowedButRegistryDenied_DoesNotReadStore()
    {
        var (memory, registry, _, _, secret) = CreateMemory();
        Assert.Empty(await Search(memory, ["memory:Secret"], "memory:Secret"));
        registry.Verify(r => r.GetStore("Secret"), Times.Never);
        secret.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Composite_ExplicitFiles_DoesNotReadUnrelatedMemoryStats()
    {
        var (memory, registry, team, excluded, _) = CreateMemory(throwStats: true);
        var files = new ScopedBackend(["Files"], [Hit("f", "Files")]);

        var results = await new CompositeQmdBackend([files, memory])
            .SearchAsync("query", "FILES", QmdSearchMode.Keyword, 5);

        results.ShouldHaveSingleItem().Id.ShouldBe("f");
        files.Searches.ShouldHaveSingleItem().Store.ShouldBe("Files");
        registry.Verify(r => r.GetReadableStores("agent"), Times.Once);
        registry.Verify(r => r.GetStore(It.IsAny<string>()), Times.Never);
        team.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
        excluded.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Composite_ExplicitAllowedFiles_DoesNotReadMemoryStatsDuringToolDiscovery()
    {
        var (memory, registry, team, excluded, _) = CreateMemory(throwStats: true);
        var files = new ScopedBackend(["Files"], [Hit("f", "Files")]);

        var results = await Search(new CompositeQmdBackend([files, memory]), ["files"], "FILES");

        results.ShouldHaveSingleItem().Id.ShouldBe("f");
        files.Searches.ShouldHaveSingleItem().Store.ShouldBe("Files");
        registry.Verify(r => r.GetStore(It.IsAny<string>()), Times.Never);
        team.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
        excluded.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OmittedAllowedMemory_WithFailingStats_StillIntersectsRegistryPolicy()
    {
        foreach (var composite in new[] { false, true })
        {
            var (memory, registry, team, excluded, secret) = CreateMemory(throwStats: true);
            var files = new ScopedBackend(["Files"], [Hit("f", "Files", 100)]);
            IQmdBackend backend = composite ? new CompositeQmdBackend([files, memory]) : memory;

            var results = await Search(backend, ["MEMORY:TEAM", "memory:Secret"], limit: 1);

            results.ShouldHaveSingleItem().Id.ShouldBe("memory:Team/e");
            files.Searches.ShouldBeEmpty();
            registry.Verify(r => r.CanRead("agent", "Team"), Times.Once);
            registry.Verify(r => r.GetStore("Excluded"), Times.Never);
            registry.Verify(r => r.GetStore("Secret"), Times.Never);
            team.Verify(s => s.SearchAsync("query", 1, It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Once);
            excluded.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
            secret.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
            team.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
            excluded.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
            secret.Verify(s => s.GetStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task UnrestrictedComposite_BlankStore_SearchesAllStores()
    {
        foreach (var allowed in new List<string>?[] { null, [] })
        foreach (var store in new[] { "", "   " })
        {
            var backend = new ScopedBackend(["Files"], [Hit("f", "Files")]);
            var results = await Search(new CompositeQmdBackend([backend]), allowed, store);
            results.ShouldHaveSingleItem().Id.ShouldBe("f");
            backend.Searches.ShouldHaveSingleItem().Store.ShouldBeNull();
            backend.DiscoveryCalls.ShouldBe(0);
        }
    }

    private static (MemoryQmdBackend Backend, Mock<ISharedMemoryStoreRegistry> Registry, Mock<IMemoryStore> Team, Mock<IMemoryStore> Excluded, Mock<IMemoryStore> Secret) CreateMemory(bool throwStats = false)
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        var stores = new[] { new Mock<IMemoryStore>(), new Mock<IMemoryStore>(), new Mock<IMemoryStore>() };
        var names = new[] { "Team", "Excluded", "Secret" };
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            registry.Setup(r => r.GetStore(name)).Returns(stores[i].Object);
            registry.Setup(r => r.CanRead("agent", name)).Returns(name != "Secret");
            if (throwStats)
                stores[i].Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Stats must not be read during search"));
            else
                stores[i].Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStoreStats(1, 0, null));
            stores[i].Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MemoryEntry { Id = "e", AgentId = "agent", SourceType = "manual", Content = "query", CreatedAt = DateTimeOffset.UtcNow }]);
        }
        registry.Setup(r => r.GetReadableStores("agent")).Returns(["Excluded", "Team"]);
        registry.Setup(r => r.GetAllConfigs()).Returns([]);
        return (new MemoryQmdBackend(registry.Object, AgentId.From("agent")), registry, stores[0], stores[1], stores[2]);
    }

    private static async Task<QmdSearchResult[]> Search(IQmdBackend backend, List<string>? allowed, string? store = null, int limit = 10)
    {
        var tool = new KnowledgeSearchTool(backend, new QmdConfig { AllowedStores = allowed });
        var result = await tool.ExecuteAsync("test", new Dictionary<string, object?> { ["query"] = "query", ["store"] = store, ["limit"] = limit });
        var parsed = JsonSerializer.Deserialize<QmdSearchResult[]>(result.Content[0].Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        return parsed;
    }

    private static QmdSearchResult Hit(string id, string store, double score = 1) => new(id, store, $"/{id}", id, score, "query");

    private sealed class ScopedBackend(string[] names, QmdSearchResult[] hits) : IQmdBackend
    {
        public List<(string? Store, int Limit)> Searches { get; } = [];
        public int DiscoveryCalls { get; private set; }
        public bool LeakResults { get; init; }
        public Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Searches.Add((store, limit));
            if (store is not null && !names.Contains(store, StringComparer.Ordinal))
                throw new QmdCliException(1, "Unknown collection", [store]);
            return Task.FromResult(hits.Where(h => LeakResults || store is null || h.Store.Equals(store, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(h => h.Score).Take(limit).ToArray());
        }
        public Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DiscoveryCalls++;
            return Task.FromResult(names.Select(n => new QmdStoreInfo(n, "/synthetic", null, 0, null, true)).ToArray());
        }
        public Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct = default) => Task.FromResult<QmdDocument?>(null);
        public Task UpdateIndexAsync(string? store, CancellationToken ct = default) => Task.CompletedTask;
        public Task EmbedAsync(string? store, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
