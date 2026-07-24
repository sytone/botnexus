namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Configuration model for the QMD knowledge base extension.
/// Bound from <c>ExtensionConfig["botnexus-qmd"]</c> on the agent descriptor.
/// </summary>
public sealed class QmdConfig
{
    /// <summary>
    /// Whether the QMD extension is enabled for this agent. Defaults to <c>false</c>:
    /// QMD is opt-in and must be explicitly enabled per agent via
    /// <c>extensions.botnexus-qmd.enabled: true</c> (issue #2116). Installs that relied on
    /// omitting this key to mean "enabled" must now set it explicitly.
    /// </summary>
    public bool Enabled { get; set; }

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

    /// <summary>
    /// When true, shared memory stores are exposed as virtual QMD collections
    /// (prefixed with "memory:") alongside file-based stores.
    /// </summary>
    public bool IncludeMemoryStores { get; set; }

    /// <summary>
    /// When set, restricts this agent to only see the listed store names.
    /// Null or empty means the agent can see ALL configured stores.
    /// </summary>
    public List<string>? AllowedStores { get; set; }

    /// <summary>
    /// Returns true if the given store name is accessible to this agent.
    /// When <see cref="AllowedStores"/> is null or empty, all stores are accessible.
    /// </summary>
    public bool IsStoreAllowed(string storeName)
    {
        if (AllowedStores is null || AllowedStores.Count == 0)
            return true;
        return AllowedStores.Contains(storeName, StringComparer.OrdinalIgnoreCase);
    }
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
