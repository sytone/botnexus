using BotNexus.Memory;
using BotNexus.Memory.Models;
using Moq;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class MemoryQmdBackendTests
{
    private const string AgentId = "test-agent";

    private static (MemoryQmdBackend backend, Mock<ISharedMemoryStoreRegistry> registry, Mock<IMemoryStore> store) CreateSut(
        string storeName = "platform-knowledge",
        IReadOnlyList<MemoryEntry>? entries = null)
    {
        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries ?? []);
        store.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStoreStats(entries?.Count ?? 0, 1024, DateTimeOffset.UtcNow));

        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.GetReadableStores(AgentId)).Returns([storeName]);
        registry.Setup(r => r.CanRead(AgentId, storeName)).Returns(true);
        registry.Setup(r => r.GetStore(storeName)).Returns(store.Object);
        registry.Setup(r => r.GetAllConfigs()).Returns([
            new SharedMemoryStoreConfig { Name = storeName, Description = "Test store" }
        ]);

        var backend = new MemoryQmdBackend(registry.Object, AgentId);
        return (backend, registry, store);
    }

    [Fact]
    public async Task SearchAsync_ReturnsResults_FromReadableStores()
    {
        var entries = new List<MemoryEntry>
        {
            new() { Id = "e1", AgentId = "other", SourceType = "conversation", Content = "How to deploy BotNexus", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = "e2", AgentId = "other", SourceType = "dreaming", Content = "Deployment best practices", CreatedAt = DateTimeOffset.UtcNow }
        };
        var (backend, _, store) = CreateSut(entries: entries);

        var results = await backend.SearchAsync("deploy", null, QmdSearchMode.Hybrid, 10);

        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.StartsWith("memory:platform-knowledge/", r.Id));
        Assert.All(results, r => Assert.Equal("memory:platform-knowledge", r.Store));
        store.Verify(s => s.SearchAsync("deploy", 10, It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_RespectsStoreFilter_WithPrefix()
    {
        var (backend, registry, _) = CreateSut();

        await backend.SearchAsync("test", "memory:platform-knowledge", QmdSearchMode.Keyword, 5);

        registry.Verify(r => r.CanRead(AgentId, "platform-knowledge"), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenStoreNotReadable()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.GetReadableStores(AgentId)).Returns([]);
        registry.Setup(r => r.CanRead(AgentId, "secret-store")).Returns(false);
        var backend = new MemoryQmdBackend(registry.Object, AgentId);

        var results = await backend.SearchAsync("test", "secret-store", QmdSearchMode.Hybrid, 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsEntry_ByCompoundId()
    {
        var entry = new MemoryEntry
        {
            Id = "abc123",
            AgentId = "other",
            SourceType = "manual",
            Content = "Important knowledge about X",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.GetByIdAsync("abc123", It.IsAny<CancellationToken>())).ReturnsAsync(entry);

        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.CanRead(AgentId, "my-store")).Returns(true);
        registry.Setup(r => r.GetStore("my-store")).Returns(store.Object);

        var backend = new MemoryQmdBackend(registry.Object, AgentId);

        var doc = await backend.GetDocumentAsync("memory:my-store/abc123");

        Assert.NotNull(doc);
        Assert.Equal("memory:my-store/abc123", doc.Id);
        Assert.Equal("memory:my-store", doc.Store);
        Assert.Equal("Important knowledge about X", doc.Content);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenNotReadable()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.CanRead(AgentId, "secret")).Returns(false);
        var backend = new MemoryQmdBackend(registry.Object, AgentId);

        var doc = await backend.GetDocumentAsync("memory:secret/abc123");

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_ForNonMemoryId()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        var backend = new MemoryQmdBackend(registry.Object, AgentId);

        var doc = await backend.GetDocumentAsync("some-file-doc-id");

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetStoresAsync_ReturnsReadableStores_WithMemoryPrefix()
    {
        var (backend, _, _) = CreateSut();

        var stores = await backend.GetStoresAsync();

        Assert.Single(stores);
        Assert.Equal("memory:platform-knowledge", stores[0].Name);
        Assert.Equal("memory://platform-knowledge", stores[0].Path);
        Assert.Equal("Test store", stores[0].Description);
        Assert.True(stores[0].Healthy);
    }

    [Fact]
    public async Task UpdateIndexAsync_IsNoOp()
    {
        var (backend, _, _) = CreateSut();

        // Should not throw
        await backend.UpdateIndexAsync("memory:platform-knowledge");
        await backend.UpdateIndexAsync(null);
    }

    [Fact]
    public async Task EmbedAsync_IsNoOp()
    {
        var (backend, _, _) = CreateSut();

        await backend.EmbedAsync("memory:platform-knowledge");
        await backend.EmbedAsync(null);
    }
}
