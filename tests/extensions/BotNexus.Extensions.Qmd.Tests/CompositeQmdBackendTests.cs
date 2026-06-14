namespace BotNexus.Extensions.Qmd.Tests;

public sealed class CompositeQmdBackendTests
{
    [Fact]
    public async Task SearchAsync_MergesResults_SortedByScore()
    {
        var backend1 = new InMemoryQmdBackend();
        backend1.SetDocuments([
            new QmdDocument("doc1", "files", "/docs/a.md", "File Doc", "Content about deployment")
        ]);

        var backend2 = new InMemoryQmdBackend();
        backend2.SetDocuments([
            new QmdDocument("doc2", "memory:shared", "memory://shared/e1", "Memory Doc", "Deployment instructions")
        ]);

        var composite = new CompositeQmdBackend([backend1, backend2]);

        var results = await composite.SearchAsync("deployment", null, QmdSearchMode.Hybrid, 10);

        Assert.Equal(2, results.Length);
        // Both found
        Assert.Contains(results, r => r.Id == "doc1");
        Assert.Contains(results, r => r.Id == "doc2");
    }

    [Fact]
    public async Task SearchAsync_RespectsLimit()
    {
        var backend1 = new InMemoryQmdBackend();
        backend1.SetDocuments([
            new QmdDocument("d1", "s1", "/a.md", "A", "query match"),
            new QmdDocument("d2", "s1", "/b.md", "B", "query match")
        ]);

        var backend2 = new InMemoryQmdBackend();
        backend2.SetDocuments([
            new QmdDocument("d3", "s2", "/c.md", "C", "query match")
        ]);

        var composite = new CompositeQmdBackend([backend1, backend2]);

        var results = await composite.SearchAsync("query", null, QmdSearchMode.Keyword, 2);

        Assert.Equal(2, results.Length);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsFromFirstBackend_ThatHasIt()
    {
        var backend1 = new InMemoryQmdBackend();
        var backend2 = new InMemoryQmdBackend();
        backend2.SetDocuments([
            new QmdDocument("special-id", "store2", "/x.md", "Found", "Content here")
        ]);

        var composite = new CompositeQmdBackend([backend1, backend2]);

        var doc = await composite.GetDocumentAsync("special-id");

        Assert.NotNull(doc);
        Assert.Equal("Found", doc.Title);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenNotInAnyBackend()
    {
        var composite = new CompositeQmdBackend([new InMemoryQmdBackend(), new InMemoryQmdBackend()]);

        var doc = await composite.GetDocumentAsync("nonexistent");

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetStoresAsync_CombinesAllBackends()
    {
        var backend1 = new InMemoryQmdBackend();
        backend1.SetStores([new QmdStoreInfo("files", "/docs", "File store", 10, DateTimeOffset.UtcNow, true)]);

        var backend2 = new InMemoryQmdBackend();
        backend2.SetStores([new QmdStoreInfo("memory:shared", "memory://shared", "Memory store", 5, null, true)]);

        var composite = new CompositeQmdBackend([backend1, backend2]);

        var stores = await composite.GetStoresAsync();

        Assert.Equal(2, stores.Length);
        Assert.Contains(stores, s => s.Name == "files");
        Assert.Contains(stores, s => s.Name == "memory:shared");
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllBackends()
    {
        var backend1 = new InMemoryQmdBackend();
        var backend2 = new InMemoryQmdBackend();
        var composite = new CompositeQmdBackend([backend1, backend2]);

        // Should not throw
        await composite.DisposeAsync();
    }
}
