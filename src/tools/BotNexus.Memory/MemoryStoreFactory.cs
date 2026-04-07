using System.Collections.Concurrent;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Memory;

public sealed class MemoryStoreFactory(BotNexusHome home) : IMemoryStoreFactory, IAsyncDisposable
{
    private readonly BotNexusHome _home = home;
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public IMemoryStore Create(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        return _stores.GetOrAdd(agentId, id =>
        {
            var agentDirectory = _home.GetAgentDirectory(id);
            var dbPath = Path.Combine(agentDirectory, "data", "memory.sqlite");
            return new SqliteMemoryStore(dbPath);
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _stores)
            await pair.Value.DisposeAsync();

        _stores.Clear();
    }
}
