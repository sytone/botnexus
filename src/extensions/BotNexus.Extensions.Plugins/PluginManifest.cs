using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins;

/// <summary>
/// Attribution block shared by the plugin manifest (<c>author</c>) and the marketplace
/// catalog (<c>owner</c>). Callers need this to know who is accountable for content
/// before it is trusted; only <see cref="Name"/> is guaranteed present.
/// </summary>
public sealed record PluginParty
{
    /// <summary>Display name of the responsible person or organisation.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Contact address, or <c>null</c> when the author chose not to publish one.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>Profile or organisation URL, or <c>null</c> when not published.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>
/// Typed projection of <c>.botnexus-plugin/plugin.json</c>. Only <see cref="Name"/> is
/// required; every component collection is <c>null</c> when the manifest omits it, which
/// signals that the component should be discovered by convention at the plugin root rather
/// than that the plugin has none. Downstream slices depend on that distinction, so the
/// parser never substitutes an empty collection for an absent one.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Unique lowercase kebab-case plugin identifier.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Marketplace listing summary, or <c>null</c> when unspecified.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Semantic version of the release, or <c>null</c> for an unversioned local plugin.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Author attribution, or <c>null</c> when the manifest omits it.</summary>
    [JsonPropertyName("author")]
    public PluginParty? Author { get; init; }

    /// <summary>Project or documentation URL, or <c>null</c>.</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    /// <summary>Source repository URL, or <c>null</c>.</summary>
    [JsonPropertyName("repository")]
    public string? Repository { get; init; }

    /// <summary>SPDX licence identifier, or <c>null</c>.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>Discovery keywords, or <c>null</c> when none were declared.</summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>Explicit skill paths; <c>null</c> means discover by convention.</summary>
    [JsonPropertyName("skills")]
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>Explicit agent paths; <c>null</c> means discover by convention.</summary>
    [JsonPropertyName("agents")]
    public IReadOnlyList<string>? Agents { get; init; }

    /// <summary>Explicit command paths; <c>null</c> means discover by convention.</summary>
    [JsonPropertyName("commands")]
    public IReadOnlyList<string>? Commands { get; init; }

    /// <summary>Explicit hooks configuration path; <c>null</c> means discover by convention.</summary>
    [JsonPropertyName("hooks")]
    public string? Hooks { get; init; }

    /// <summary>Explicit MCP server configuration path; <c>null</c> means discover by convention.</summary>
    [JsonPropertyName("mcpServers")]
    public string? McpServers { get; init; }
}

/// <summary>One plugin offering inside a marketplace catalog.</summary>
public sealed record MarketplacePluginEntry
{
    /// <summary>Plugin identifier, matching the plugin's own manifest name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Where the plugin content comes from - a repository URL or catalog-relative path.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>Listing summary, or <c>null</c>.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Advertised version, or <c>null</c> when the catalog tracks the source's default branch.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Discovery keywords, or <c>null</c>.</summary>
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }
}

/// <summary>
/// Typed projection of a marketplace catalog document. Exists so a future install slice can
/// resolve a plugin name to a source without re-deriving the catalog shape.
/// </summary>
public sealed record MarketplaceCatalog
{
    /// <summary>Unique lowercase kebab-case marketplace identifier.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Publisher accountable for the catalog contents.</summary>
    [JsonPropertyName("owner")]
    public PluginParty Owner { get; init; } = new();

    /// <summary>Catalog summary, or <c>null</c>.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Plugin offerings; may be empty but is never <c>null</c> after a successful parse.</summary>
    [JsonPropertyName("plugins")]
    public IReadOnlyList<MarketplacePluginEntry> Plugins { get; init; } = [];
}
