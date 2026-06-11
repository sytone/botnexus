namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Configuration model for the QMD knowledge base extension.
/// Bound from <c>ExtensionConfig["botnexus-qmd"]</c> on the agent descriptor.
/// </summary>
public sealed class QmdConfig
{
    /// <summary>Whether the QMD extension is enabled for this agent.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to the <c>qmd</c> binary. When null, the binary is resolved from PATH.
    /// </summary>
    public string? QmdPath { get; set; }

    /// <summary>
    /// Default search mode when not specified by the caller.
    /// Valid values: "keyword", "semantic", "hybrid".
    /// </summary>
    public string DefaultSearchMode { get; set; } = "hybrid";

    /// <summary>Maximum number of search results to return by default.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>Configured knowledge stores to index and search.</summary>
    public List<QmdStoreConfig> Stores { get; set; } = [];
}

/// <summary>
/// Configuration for a single QMD knowledge store (a folder of documents to index).
/// </summary>
public sealed class QmdStoreConfig
{
    /// <summary>Unique name for this store (used in search queries and tool output).</summary>
    public required string Name { get; set; }

    /// <summary>Filesystem path to the document folder to index.</summary>
    public required string Path { get; set; }

    /// <summary>Human-readable description of what this store contains.</summary>
    public string? Description { get; set; }

    /// <summary>Whether to automatically re-index this store on a schedule.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Interval in minutes between automatic re-indexing runs.</summary>
    public int UpdateIntervalMinutes { get; set; } = 60;
}
