using BotNexus.Extensions.Qmd;

namespace BotNexus.Extensions.Qmd.Tests;

public class InMemoryQmdBackendTests
{
    private readonly InMemoryQmdBackend _backend = new();

    [Fact]
    public async Task SearchAsync_returns_matching_documents_by_content()
    {
        _backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/note1.md", "Note One", "This is about gardening tips"));
        _backend.Documents.Add(new QmdDocument("doc2", "vault", "/vault/note2.md", "Note Two", "This is about cooking recipes"));

        var results = await _backend.SearchAsync("gardening", null, QmdSearchMode.Hybrid, 10);

        Assert.Single(results);
        Assert.Equal("doc1", results[0].Id);
        Assert.Equal("vault", results[0].Store);
        Assert.Equal("Note One", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_returns_matching_documents_by_title()
    {
        _backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/gardening.md", "Gardening Guide", "Content here"));

        var results = await _backend.SearchAsync("Gardening", null, QmdSearchMode.Keyword, 10);

        Assert.Single(results);
        Assert.Equal("doc1", results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_filters_by_store()
    {
        _backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/note.md", "Note", "shared content"));
        _backend.Documents.Add(new QmdDocument("doc2", "work", "/work/doc.md", "Work Doc", "shared content"));

        var results = await _backend.SearchAsync("shared", "vault", QmdSearchMode.Hybrid, 10);

        Assert.Single(results);
        Assert.Equal("vault", results[0].Store);
    }

    [Fact]
    public async Task SearchAsync_respects_limit()
    {
        for (int i = 0; i < 20; i++)
            _backend.Documents.Add(new QmdDocument($"doc{i}", "vault", $"/vault/note{i}.md", $"Note {i}", "common search term"));

        var results = await _backend.SearchAsync("common", null, QmdSearchMode.Hybrid, 5);

        Assert.Equal(5, results.Length);
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_no_match()
    {
        _backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/note.md", "Note", "unrelated content"));

        var results = await _backend.SearchAsync("nonexistent", null, QmdSearchMode.Hybrid, 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_assigns_descending_scores()
    {
        _backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/a.md", "A", "keyword match"));
        _backend.Documents.Add(new QmdDocument("doc2", "vault", "/vault/b.md", "B", "keyword match"));

        var results = await _backend.SearchAsync("keyword", null, QmdSearchMode.Hybrid, 10);

        Assert.Equal(2, results.Length);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task GetDocumentAsync_returns_document_by_id()
    {
        _backend.Documents.Add(new QmdDocument("doc1", "vault", "/vault/note.md", "Note", "Full content"));

        var doc = await _backend.GetDocumentAsync("doc1");

        Assert.NotNull(doc);
        Assert.Equal("doc1", doc.Id);
        Assert.Equal("Full content", doc.Content);
    }

    [Fact]
    public async Task GetDocumentAsync_returns_null_for_unknown_id()
    {
        var doc = await _backend.GetDocumentAsync("nonexistent");

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetStoresAsync_returns_configured_stores()
    {
        _backend.Stores.Add(new QmdStoreInfo("vault", "/vault", "Personal vault", 42, DateTimeOffset.UtcNow, true));
        _backend.Stores.Add(new QmdStoreInfo("work", "/work", null, 10, null, false));

        var stores = await _backend.GetStoresAsync();

        Assert.Equal(2, stores.Length);
    }

    [Fact]
    public async Task UpdateIndexAsync_records_call()
    {
        await _backend.UpdateIndexAsync("vault");
        await _backend.UpdateIndexAsync(null);

        Assert.Equal(2, _backend.UpdateCalls.Count);
        Assert.Contains("vault", _backend.UpdateCalls);
        Assert.Contains(null, _backend.UpdateCalls);
    }

    [Fact]
    public async Task EmbedAsync_records_call()
    {
        await _backend.EmbedAsync("vault");

        Assert.Single(_backend.EmbedCalls);
        Assert.Contains("vault", _backend.EmbedCalls);
    }

    [Fact]
    public async Task SearchAsync_throws_on_cancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _backend.SearchAsync("query", null, QmdSearchMode.Hybrid, 10, cts.Token));
    }

    [Fact]
    public async Task GetDocumentAsync_throws_on_cancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _backend.GetDocumentAsync("doc1", cts.Token));
    }

    [Fact]
    public async Task DisposeAsync_completes_without_error()
    {
        await _backend.DisposeAsync();
        // Should not throw
    }
}
