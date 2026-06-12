using System.Text.Json.Serialization;

namespace BotNexus.Extensions.DataStore;

/// <summary>
/// Configuration for the per-agent structured data store tool.
/// Injected from <c>botnexus-data-store</c> extension config block in agent descriptor.
/// </summary>
public sealed class DataStoreConfig
{
    /// <summary>Whether the data store tool is enabled for this agent.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Maximum size in bytes for the per-agent SQLite data store.
    /// Default: 50 MB.
    /// </summary>
    [JsonPropertyName("maxSizeBytes")]
    public long MaxSizeBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>
    /// Maximum number of rows returned by a single query action.
    /// Queries that would return more rows are truncated with a warning.
    /// Default: 1000.
    /// </summary>
    [JsonPropertyName("maxQueryRows")]
    public int MaxQueryRows { get; init; } = 1000;
}
