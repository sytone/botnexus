using System.Collections.Concurrent;
using System.IO.Abstractions;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// <see cref="IMemoryStoreFactory"/> that constructs each agent's SQLite store with the composed
/// <see cref="IMemoryEmbeddingService"/> (#2855).
/// </summary>
/// <remarks>
/// <para>
/// <c>MemoryStoreFactory</c> in <c>BotNexus.Memory</c> creates stores with no embedding service,
/// and acceptance criterion 2 requires this feature to land with zero diff under that project. So
/// the embedding-aware factory lives here, in composition, where the provider stack and the memory
/// store are both already visible.
/// </para>
/// <para>
/// Existence probing is DELEGATED to an inner <c>MemoryStoreFactory</c> rather than reimplemented.
/// <c>StoreLocationExists</c> encodes hard-won knowledge about reaped sub-agent workspaces (#2237,
/// #2608); a second copy of that logic here would drift from the original the first time either
/// changed. The inner factory's own <c>Create</c> is never called, so it holds no stores and there
/// is exactly one store instance per agent.
/// </para>
/// </remarks>
public sealed class EmbeddingAwareMemoryStoreFactory : IMemoryStoreFactory, IAsyncDisposable
{
    private readonly Func<string, string> _dbPathResolver;
    private readonly IFileSystem _fileSystem;
    private readonly IMemoryEmbeddingService _embeddingService;
    private readonly MemoryStoreFactory _existenceProbe;
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dbPathResolver">Maps an agent id to its store path.</param>
    /// <param name="embeddingService">Vector source, typically from <see cref="MemoryEmbeddingComposition"/>.</param>
    /// <param name="fileSystem">Filesystem abstraction.</param>
    public EmbeddingAwareMemoryStoreFactory(
        Func<string, string> dbPathResolver,
        IMemoryEmbeddingService embeddingService,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(dbPathResolver);
        ArgumentNullException.ThrowIfNull(embeddingService);

        _dbPathResolver = dbPathResolver;
        _embeddingService = embeddingService;
        _fileSystem = fileSystem ?? new FileSystem();
        _existenceProbe = new MemoryStoreFactory(dbPathResolver, _fileSystem);
    }

    /// <inheritdoc />
    public IMemoryStore Create(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        return _stores.GetOrAdd(agentId, id =>
            new SqliteMemoryStore(_dbPathResolver(id), _fileSystem, null, _embeddingService));
    }

    /// <inheritdoc />
    public bool StoreLocationExists(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // An already-created store is live regardless of what the filesystem looks like now; the
        // inner probe cannot know about stores this factory created, so answer that case here.
        return _stores.ContainsKey(agentId) || _existenceProbe.StoreLocationExists(agentId);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _stores)
            await pair.Value.DisposeAsync().ConfigureAwait(false);

        _stores.Clear();
        await _existenceProbe.DisposeAsync().ConfigureAwait(false);
    }
}
