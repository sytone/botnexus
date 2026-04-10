using System.Collections.Concurrent;
using System.IO.Abstractions;

namespace BotNexus.Memory;

public sealed class MemoryStoreFactory(Func<string, string> dbPathResolver) : IMemoryStoreFactory, IAsyncDisposable
{
    private readonly Func<string, string> _dbPathResolver = dbPathResolver;
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public IMemoryStore Create(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        return _stores.GetOrAdd(agentId, id =>
        {
            var dbPath = _dbPathResolver(id);
            return new SqliteMemoryStore(dbPath, new FileSystem());
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _stores)
            await pair.Value.DisposeAsync();

        _stores.Clear();
    }
}
