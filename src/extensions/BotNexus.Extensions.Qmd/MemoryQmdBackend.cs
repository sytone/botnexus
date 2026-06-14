using BotNexus.Memory;
using BotNexus.Memory.Models;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Adapts shared memory stores to the <see cref="IQmdBackend"/> interface,
/// enabling <c>knowledge_search</c> to discover and search agent memory entries.
/// Each shared memory store appears as a virtual QMD store prefixed with "memory:".
/// </summary>
public sealed class MemoryQmdBackend : IQmdBackend
{
    private readonly ISharedMemoryStoreRegistry _registry;
    private readonly string _agentId;

    internal const string StorePrefix = "memory:";

    /// <summary>
    /// Creates a new memory-backed QMD backend for the specified agent.
    /// </summary>
    /// <param name="registry">The shared memory store registry.</param>
    /// <param name="agentId">Agent ID used to filter readable stores.</param>
    public MemoryQmdBackend(ISharedMemoryStoreRegistry registry, string agentId)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _agentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
    }

    /// <inheritdoc />
    public async Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var storeNames = GetTargetStores(store);
        var results = new List<QmdSearchResult>();

        foreach (var storeName in storeNames)
        {
            var memStore = _registry.GetStore(storeName);
            if (memStore is null) continue;

            var entries = await memStore.SearchAsync(query, limit, ct: ct).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                results.Add(new QmdSearchResult(
                    Id: $"{StorePrefix}{storeName}/{entry.Id}",
                    Store: $"{StorePrefix}{storeName}",
                    Path: $"memory://{storeName}/{entry.Id}",
                    Title: ExtractTitle(entry),
                    Score: 0.8, // Memory search doesn't expose scores; use a reasonable default
                    Snippet: Truncate(entry.Content, 200)));
            }

            if (results.Count >= limit) break;
        }

        return results.Take(limit).ToArray();
    }

    /// <inheritdoc />
    public async Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!id.StartsWith(StorePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = id[StorePrefix.Length..];
        var slashIndex = remainder.IndexOf('/');
        if (slashIndex < 0) return null;

        var storeName = remainder[..slashIndex];
        var entryId = remainder[(slashIndex + 1)..];

        if (!_registry.CanRead(_agentId, storeName)) return null;

        var memStore = _registry.GetStore(storeName);
        if (memStore is null) return null;

        var entry = await memStore.GetByIdAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null) return null;

        return new QmdDocument(
            Id: id,
            Store: $"{StorePrefix}{storeName}",
            Path: $"memory://{storeName}/{entry.Id}",
            Title: ExtractTitle(entry),
            Content: entry.Content);
    }

    /// <inheritdoc />
    public async Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var readableStores = _registry.GetReadableStores(_agentId);
        var results = new List<QmdStoreInfo>();

        foreach (var storeName in readableStores)
        {
            var memStore = _registry.GetStore(storeName);
            if (memStore is null) continue;

            var stats = await memStore.GetStatsAsync(ct).ConfigureAwait(false);
            var config = _registry.GetAllConfigs()
                .FirstOrDefault(c => c.Name.Equals(storeName, StringComparison.OrdinalIgnoreCase));

            results.Add(new QmdStoreInfo(
                Name: $"{StorePrefix}{storeName}",
                Path: $"memory://{storeName}",
                Description: config?.Description ?? $"Shared memory store: {storeName}",
                DocumentCount: stats.EntryCount,
                LastUpdated: stats.LastIndexedAt,
                Healthy: true));
        }

        return results.ToArray();
    }

    /// <inheritdoc />
    public Task UpdateIndexAsync(string? store, CancellationToken ct = default)
    {
        // Memory stores are self-indexing; no-op.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EmbedAsync(string? store, CancellationToken ct = default)
    {
        // Embedding for memory stores is handled by the memory pipeline; no-op.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IReadOnlyList<string> GetTargetStores(string? store)
    {
        if (store is not null)
        {
            var name = store.StartsWith(StorePrefix, StringComparison.OrdinalIgnoreCase)
                ? store[StorePrefix.Length..]
                : store;

            return _registry.CanRead(_agentId, name) ? [name] : [];
        }

        return _registry.GetReadableStores(_agentId);
    }

    private static string ExtractTitle(MemoryEntry entry)
    {
        // Use first line or first 60 chars as title
        var firstLine = entry.Content.Split('\n', 2)[0].TrimStart('#', ' ');
        return Truncate(firstLine, 80);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }
}
