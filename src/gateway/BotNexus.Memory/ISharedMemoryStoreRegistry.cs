namespace BotNexus.Memory;

/// <summary>
/// Registry for shared memory stores with access control enforcement.
/// Resolves stores by name and validates reader/writer permissions per agent.
/// </summary>
public interface ISharedMemoryStoreRegistry : IAsyncDisposable
{
    /// <summary>Gets a shared store by name. Returns null if not configured.</summary>
    IMemoryStore? GetStore(string storeName);

    /// <summary>Gets all store names readable by the specified agent.</summary>
    IReadOnlyList<string> GetReadableStores(string agentId);

    /// <summary>Gets all store names writable by the specified agent.</summary>
    IReadOnlyList<string> GetWritableStores(string agentId);

    /// <summary>Returns true if the agent can read from the named store.</summary>
    bool CanRead(string agentId, string storeName);

    /// <summary>Returns true if the agent can write to the named store.</summary>
    bool CanWrite(string agentId, string storeName);

    /// <summary>Gets all configured shared store configs.</summary>
    IReadOnlyList<SharedMemoryStoreConfig> GetAllConfigs();
}
