using System.Collections.Concurrent;
using System.IO.Abstractions;

namespace BotNexus.Memory;

/// <summary>
/// In-memory registry of shared memory stores backed by SQLite.
/// Stores are located at {basePath}/shared/{store-name}.db.
/// </summary>
public sealed class SharedMemoryStoreRegistry : ISharedMemoryStoreRegistry
{
    private readonly IReadOnlyList<SharedMemoryStoreConfig> _configs;
    private readonly string _basePath;
    private readonly IFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public SharedMemoryStoreRegistry(
        IReadOnlyList<SharedMemoryStoreConfig> configs,
        string basePath,
        IFileSystem? fileSystem = null)
    {
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public IMemoryStore? GetStore(string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        var config = FindConfig(storeName);
        if (config is null)
            return null;

        return _stores.GetOrAdd(storeName, name =>
        {
            var dbPath = Path.Combine(_basePath, "shared", $"{name}.db");
            return new SqliteMemoryStore(dbPath, _fileSystem);
        });
    }

    public IReadOnlyList<string> GetReadableStores(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _configs
            .Where(c => HasAccess(c.Readers, agentId))
            .Select(c => c.Name)
            .ToList();
    }

    public IReadOnlyList<string> GetWritableStores(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _configs
            .Where(c => HasAccess(c.Writers, agentId))
            .Select(c => c.Name)
            .ToList();
    }

    public bool CanRead(string agentId, string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        var config = FindConfig(storeName);
        return config is not null && HasAccess(config.Readers, agentId);
    }

    public bool CanWrite(string agentId, string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        var config = FindConfig(storeName);
        return config is not null && HasAccess(config.Writers, agentId);
    }

    public IReadOnlyList<SharedMemoryStoreConfig> GetAllConfigs() => _configs;

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _stores)
            await pair.Value.DisposeAsync();

        _stores.Clear();
    }

    private SharedMemoryStoreConfig? FindConfig(string storeName)
        => _configs.FirstOrDefault(c => string.Equals(c.Name, storeName, StringComparison.OrdinalIgnoreCase));

    private static bool HasAccess(IReadOnlyList<string> accessList, string agentId)
    {
        if (accessList.Count == 0)
            return false;

        return accessList.Any(entry =>
            string.Equals(entry, "*", StringComparison.Ordinal) ||
            string.Equals(entry, agentId, StringComparison.OrdinalIgnoreCase));
    }
}
