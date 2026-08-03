using System.Collections.Concurrent;
using System.IO.Abstractions;

namespace BotNexus.Memory;

public sealed class MemoryStoreFactory(Func<string, string> dbPathResolver, IFileSystem? fileSystem = null) : IMemoryStoreFactory, IAsyncDisposable
{
    private readonly Func<string, string> _dbPathResolver = dbPathResolver;
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public IMemoryStore Create(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        return _stores.GetOrAdd(agentId, id =>
        {
            var dbPath = _dbPathResolver(id);
            return new SqliteMemoryStore(dbPath, _fileSystem);
        });
    }

    /// <inheritdoc />
    public bool StoreLocationExists(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // An already-created store is live regardless of what the filesystem looks like now.
        if (_stores.ContainsKey(agentId))
            return true;

        var dbPath = _dbPathResolver(agentId);
        if (_fileSystem.File.Exists(dbPath))
            return true;

        // The store creates its own immediate directory (typically "data") on initialize,
        // so the agent's own directory one level above is the meaningful existence signal:
        // when the sweeper reaps a sub-agent it removes that whole directory (#2608).
        var storeDirectory = _fileSystem.Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(storeDirectory))
            return true;

        if (_fileSystem.Directory.Exists(storeDirectory))
            return true;

        var agentDirectory = _fileSystem.Path.GetDirectoryName(storeDirectory);
        return !string.IsNullOrEmpty(agentDirectory) && _fileSystem.Directory.Exists(agentDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _stores)
            await pair.Value.DisposeAsync();

        _stores.Clear();
    }
}
