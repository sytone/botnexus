using System.Text.Json;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class KnowledgeStoresToolTests
{
    [Fact]
    public async Task Execute_ReturnsStoreListWithDescriptions()
    {
        var backend = new InMemoryQmdBackend();
        backend.Stores.Add(new QmdStoreInfo("vault", "/docs/vault", null, 42, DateTimeOffset.UtcNow, true));
        backend.Stores.Add(new QmdStoreInfo("work", "/docs/work", null, 10, null, true));

        var config = new QmdConfig
        {
            Stores =
            [
                new QmdStoreConfig { Name = "vault", Path = "/docs/vault", Description = "Personal knowledge vault" },
                new QmdStoreConfig { Name = "work", Path = "/docs/work", Description = "Work documents" }
            ]
        };

        var tool = new KnowledgeStoresTool(backend, config);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>());

        var text = result.Content[0].Value;
        text.ShouldContain("vault");
        text.ShouldContain("Personal knowledge vault");
        text.ShouldContain("42");
        text.ShouldContain("work");
        text.ShouldContain("Work documents");
        text.ShouldContain("knowledge_search");
    }

    [Fact]
    public async Task Execute_EmptyStores_ReturnsMessage()
    {
        var backend = new InMemoryQmdBackend();
        var tool = new KnowledgeStoresTool(backend, new QmdConfig());

        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>());

        result.Content[0].Value.ShouldContain("No knowledge stores");
    }

    [Fact]
    public async Task Execute_BackendFailure_ReturnsErrorMessage()
    {
        var backend = new FailingQmdBackend();
        var tool = new KnowledgeStoresTool(backend, new QmdConfig());

        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>());

        result.Content[0].Value.ShouldContain("Failed to retrieve");
    }

    [Fact]
    public async Task Execute_ConfigDescriptionOverridesBackend()
    {
        var backend = new InMemoryQmdBackend();
        backend.Stores.Add(new QmdStoreInfo("notes", "/notes", "Backend description", 5, null, true));

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "notes", Path = "/notes", Description = "Config description" }]
        };

        var tool = new KnowledgeStoresTool(backend, config);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>());

        result.Content[0].Value.ShouldContain("Config description");
    }
}

public sealed class KnowledgeGetToolTests
{
    [Fact]
    public async Task Execute_ReturnsDocument()
    {
        var backend = new InMemoryQmdBackend();
        backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/test.md", "Test Doc", "# Hello\nContent here"));

        var tool = new KnowledgeGetTool(backend, new QmdConfig());
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?> { ["id"] = "doc1" });

        var text = result.Content[0].Value;
        text.ShouldContain("Test Doc");
        text.ShouldContain("# Hello");
        text.ShouldContain("Content here");
    }

    [Fact]
    public async Task Execute_NotFound_ReturnsMessage()
    {
        var backend = new InMemoryQmdBackend();
        var tool = new KnowledgeGetTool(backend, new QmdConfig());
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?> { ["id"] = "nonexistent" });

        result.Content[0].Value.ShouldContain("Document not found");
    }

    [Fact]
    public async Task Execute_LargeContent_Truncates()
    {
        var largeContent = new string('x', 60_000);
        var backend = new InMemoryQmdBackend();
        backend.Documents.Add(new QmdDocument("big", "vault", "/vault/big.md", "Big", largeContent));

        var tool = new KnowledgeGetTool(backend, new QmdConfig());
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?> { ["id"] = "big" });

        var text = result.Content[0].Value;
        text.ShouldContain("[truncated");
        text.Length.ShouldBeLessThan(55_000); // 50K content + JSON overhead + marker
    }

    [Fact]
    public async Task Execute_BackendFailure_ReturnsErrorMessage()
    {
        var backend = new FailingQmdBackend();
        var tool = new KnowledgeGetTool(backend, new QmdConfig());
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?> { ["id"] = "doc1" });

        result.Content[0].Value.ShouldContain("Failed to retrieve");
    }

    [Fact]
    public async Task PrepareArguments_MissingId_Throws()
    {
        var backend = new InMemoryQmdBackend();
        var tool = new KnowledgeGetTool(backend, new QmdConfig());

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["id"] = "   " });
        await act.ShouldThrowAsync<ArgumentException>();
    }
}

/// <summary>Test helper that always throws on backend calls.</summary>
internal sealed class FailingQmdBackend : IQmdBackend
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct) => throw new InvalidOperationException("Backend unavailable");
    public Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct) => throw new InvalidOperationException("Backend unavailable");
    public Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct) => throw new InvalidOperationException("Backend unavailable");
    public Task UpdateIndexAsync(string? store, CancellationToken ct) => throw new InvalidOperationException("Backend unavailable");
    public Task EmbedAsync(string? store, CancellationToken ct) => throw new InvalidOperationException("Backend unavailable");
}
