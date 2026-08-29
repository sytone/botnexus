using System.Collections.Concurrent;
using System.IO.Abstractions;
using BotNexus.Domain.Primitives;

namespace BotNexus.Memory;

public sealed class MemoryStoreFactory(Func<string, string> dbPathResolver, IFileSystem? fileSystem = null) : IMemoryStoreFactory, IAsyncDisposable
{
    private readonly Func<string, string> _dbPathResolver = dbPathResolver;
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public IMemoryStore Create(AgentId agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId.Value);

        return _stores.GetOrAdd(agentId.Value, id =>
        {
            var dbPath = _dbPathResolver(id);
            return new SqliteMemoryStore(dbPath, _fileSystem);
        });
    }

    /// <inheritdoc />
    public bool StoreLocationExists(AgentId agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId.Value);

        // #3542: a cached store instance is NOT evidence the location still exists — a reaped
        // sub-agent is cached-then-swept by construction. The filesystem is the only authority.
        return MemoryStoreLocationProbe.Exists(_fileSystem, _dbPathResolver(agentId.Value));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _stores)
            await pair.Value.DisposeAsync();

        _stores.Clear();
    }
}
