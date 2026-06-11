namespace BotNexus.Memory;

/// <summary>
/// Configuration for a named shared memory store that multiple agents can read/write.
/// Parsed from Gateway.Memory.SharedStores[] in config.json.
/// </summary>
public sealed record SharedMemoryStoreConfig
{
    /// <summary>Unique store name (e.g. "platform-knowledge").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of what this store is for.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Agent IDs allowed to write to this store. Use "*" for all agents.
    /// </summary>
    public IReadOnlyList<string> Writers { get; init; } = [];

    /// <summary>
    /// Agent IDs allowed to read from this store. Use "*" for all agents.
    /// </summary>
    public IReadOnlyList<string> Readers { get; init; } = [];

    /// <summary>
    /// Number of days to retain entries. Null means no retention limit.
    /// </summary>
    public int? RetentionDays { get; init; }
}
