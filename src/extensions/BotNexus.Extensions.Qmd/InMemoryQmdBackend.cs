using System.Collections.Concurrent;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// In-memory implementation of <see cref="IQmdBackend"/> for unit testing.
/// Pre-populate <see cref="Documents"/> and <see cref="Stores"/> to control test responses.
/// </summary>
public sealed class InMemoryQmdBackend : IQmdBackend
{
    /// <summary>Documents available for search and retrieval.</summary>
    public ConcurrentBag<QmdDocument> Documents { get; } = [];

    /// <summary>Store metadata returned by <see cref="GetStoresAsync"/>.</summary>
    public ConcurrentBag<QmdStoreInfo> Stores { get; } = [];

    /// <summary>Records calls to <see cref="UpdateIndexAsync"/> for test assertions.</summary>
    public ConcurrentBag<string?> UpdateCalls { get; } = [];

    /// <summary>Records calls to <see cref="EmbedAsync"/> for test assertions.</summary>
    public ConcurrentBag<string?> EmbedCalls { get; } = [];

    /// <summary>Replaces all stores with the given set.</summary>
    public void SetStores(IEnumerable<QmdStoreInfo> stores)
    {
        Stores.Clear();
        foreach (var store in stores) Stores.Add(store);
    }

    /// <summary>Replaces all documents with the given set.</summary>
    public void SetDocuments(IEnumerable<QmdDocument> documents)
    {
        Documents.Clear();
        foreach (var doc in documents) Documents.Add(doc);
    }

    /// <inheritdoc />
    public Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = Documents
            .Where(d => store == null || d.Store.Equals(store, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        d.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select((d, i) => new QmdSearchResult(d.Id, d.Store, d.Path, d.Title, 1.0 - (i * 0.1), GetSnippet(d.Content, query)))
            .ToArray();

        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var doc = Documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(doc);
    }

    /// <inheritdoc />
    public Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Stores.ToArray());
    }

    /// <inheritdoc />
    public Task UpdateIndexAsync(string? store, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        UpdateCalls.Add(store);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EmbedAsync(string? store, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EmbedCalls.Add(store);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string GetSnippet(string content, string query)
    {
        var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return content.Length > 100 ? content[..100] + "..." : content;

        var start = Math.Max(0, index - 30);
        var end = Math.Min(content.Length, index + query.Length + 70);
        return content[start..end];
    }
}
