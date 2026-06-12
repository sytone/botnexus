namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Composite QMD backend that delegates operations to multiple underlying backends
/// and merges results. Used to combine CLI-based file stores with memory-backed stores.
/// </summary>
public sealed class CompositeQmdBackend : IQmdBackend
{
    private readonly IReadOnlyList<IQmdBackend> _backends;

    /// <summary>
    /// Creates a composite backend from the given backends.
    /// Results are merged in order; earlier backends take priority on ID conflicts.
    /// </summary>
    public CompositeQmdBackend(IReadOnlyList<IQmdBackend> backends)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
    }

    /// <inheritdoc />
    public async Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var allResults = new List<QmdSearchResult>();

        foreach (var backend in _backends)
        {
            var results = await backend.SearchAsync(query, store, mode, limit, ct).ConfigureAwait(false);
            allResults.AddRange(results);
        }

        // Sort by score descending, take limit
        return allResults
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var backend in _backends)
        {
            var doc = await backend.GetDocumentAsync(id, ct).ConfigureAwait(false);
            if (doc is not null) return doc;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var allStores = new List<QmdStoreInfo>();

        foreach (var backend in _backends)
        {
            var stores = await backend.GetStoresAsync(ct).ConfigureAwait(false);
            allStores.AddRange(stores);
        }

        return allStores.ToArray();
    }

    /// <inheritdoc />
    public async Task UpdateIndexAsync(string? store, CancellationToken ct = default)
    {
        foreach (var backend in _backends)
        {
            await backend.UpdateIndexAsync(store, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EmbedAsync(string? store, CancellationToken ct = default)
    {
        foreach (var backend in _backends)
        {
            await backend.EmbedAsync(store, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var backend in _backends)
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }
}
