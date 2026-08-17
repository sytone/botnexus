using System.Collections.Concurrent;
using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.Logging;

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
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dbPathResolver">Maps an agent id to its store path.</param>
    /// <param name="embeddingService">Vector source, typically from <see cref="MemoryEmbeddingComposition"/>.</param>
    /// <param name="fileSystem">Filesystem abstraction.</param>
    /// <param name="loggerFactory">
    /// Supplies each store its logger so the #3244 vector-scan-ceiling warning actually reaches the
    /// gateway log. Optional: with none, stores log to <c>NullLogger</c> exactly as before.
    /// </param>
    public EmbeddingAwareMemoryStoreFactory(
        Func<string, string> dbPathResolver,
        IMemoryEmbeddingService embeddingService,
        IFileSystem? fileSystem = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dbPathResolver);
        ArgumentNullException.ThrowIfNull(embeddingService);

        _dbPathResolver = dbPathResolver;
        _embeddingService = embeddingService;
        _fileSystem = fileSystem ?? new FileSystem();
        _loggerFactory = loggerFactory;
        _existenceProbe = new MemoryStoreFactory(dbPathResolver, _fileSystem);
    }

    /// <inheritdoc />
    public IMemoryStore Create(AgentId agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId.Value);

        return _stores.GetOrAdd(agentId.Value, id =>
            new SqliteMemoryStore(
                _dbPathResolver(id),
                _fileSystem,
                null,
                _embeddingService,
                null,
                _loggerFactory?.CreateLogger<SqliteMemoryStore>()));
    }

    /// <inheritdoc />
    public bool StoreLocationExists(AgentId agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId.Value);

        // An already-created store is live regardless of what the filesystem looks like now; the
        // inner probe cannot know about stores this factory created, so answer that case here.
        return _stores.ContainsKey(agentId.Value) || _existenceProbe.StoreLocationExists(agentId);
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
