using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins;

/// <summary>Projects one declared external catalogue contract into BotNexus marketplace types.</summary>
public interface IMarketplaceCatalogAdapter
{
    /// <summary>Projects a manifest and its already-fetched pages without performing network I/O.</summary>
    PluginParseResult<MarketplaceCatalogProjection> Project(
        string catalogOrigin,
        string manifestJson,
        IReadOnlyDictionary<string, string> pages);
}

/// <summary>Advertised component categories in an external marketplace listing.</summary>
public enum MarketplaceComponentKind
{
    /// <summary>A reusable skill.</summary>
    Skill,

    /// <summary>An agent definition.</summary>
    Agent,
}

/// <summary>Advertised components retained as discovery evidence.</summary>
public sealed record MarketplaceComponentInventory
{
    /// <summary>Advertised skill identities.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Advertised agent identities.</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>Distinct component categories derived from the advertised identities.</summary>
    public IReadOnlyList<MarketplaceComponentKind> Kinds { get; init; } = [];
}

/// <summary>A validated native catalogue plus immutable install-request resolution.</summary>
public sealed record MarketplaceCatalogProjection
{
    /// <summary>The strict native marketplace projection.</summary>
    public required MarketplaceCatalog Catalog { get; init; }

    /// <summary>Resolves one selected identity to an immutable install receipt.</summary>
    public PluginInstallRequest ResolveInstallRequest(string pluginIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginIdentity);
        var entry = Catalog.Plugins.SingleOrDefault(plugin =>
            string.Equals(plugin.Name, pluginIdentity, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new KeyNotFoundException($"Marketplace plugin '{pluginIdentity}' was not found.");
        }

        return new PluginInstallRequest
        {
            Name = entry.Name,
            Source = entry.Source,
            Reference = entry.Version,
            UpdatesEnabled = false,
        };
    }
}

/// <summary>
/// Adapter for the synthetic generated catalogue v1 contract. It is deliberately pure: fetching,
/// redirect policy, and credentials belong to a source service, while this boundary validates and
/// projects only the supplied bounded documents.
/// </summary>
public sealed class GeneratedMarketplaceCatalogAdapter : IMarketplaceCatalogAdapter
{
    /// <summary>Maximum declared pages accepted by one projection.</summary>
    public const int MaxPages = 64;

    /// <summary>Maximum entries accepted across all pages.</summary>
    public const int MaxItems = 10_000;

    /// <summary>Maximum UTF-8 bytes accepted for one manifest or page.</summary>
    public const int MaxDocumentBytes = 1_048_576;

    /// <summary>Maximum UTF-8 bytes accepted across the manifest and all pages.</summary>
    public const int MaxAggregateBytes = 16_777_216;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly PluginManifestParser _nativeParser = new();

    /// <inheritdoc />
    public PluginParseResult<MarketplaceCatalogProjection> Project(
        string catalogOrigin,
        string manifestJson,
        IReadOnlyDictionary<string, string> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogOrigin);
        ArgumentNullException.ThrowIfNull(pages);

        if (!Uri.TryCreate(catalogOrigin, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            return Failure("origin", "Catalogue origin must be an absolute HTTPS URL without query credentials or a fragment.");
        }

        int aggregateBytes = Utf8Size(manifestJson);
        if (aggregateBytes > MaxDocumentBytes)
        {
            return Failure("manifest", $"Catalogue manifest exceeds the {MaxDocumentBytes}-byte limit.");
        }

        if (!TryDeserialize(manifestJson, "manifest", out GeneratedManifest? manifest, out var manifestFailure))
        {
            return manifestFailure!;
        }

        if (manifest!.Version != 1)
        {
            return Failure("version", $"Unsupported generated catalogue version '{manifest.Version}'. Expected version 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Owner))
        {
            return Failure("manifest", "Catalogue manifest name and owner are required.");
        }

        if (manifest.TotalItems is < 0 or > MaxItems)
        {
            return Failure("totalItems", $"Catalogue totalItems must be between 0 and {MaxItems}.");
        }

        if (manifest.Pages.Count > MaxPages)
        {
            return Failure("pages", $"Catalogue declares more than {MaxPages} pages.");
        }

        var pageReferences = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<MarketplacePluginEntry>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < manifest.Pages.Count; index++)
        {
            string reference = manifest.Pages[index];
            if (!IsSafeRelativeReference(origin, reference) || !pageReferences.Add(reference))
            {
                return Failure($"pages[{index}]", $"Catalogue page reference '{reference}' is unsafe or duplicated.");
            }

            if (!pages.TryGetValue(reference, out string? pageJson))
            {
                return Failure($"pages[{index}]", $"Catalogue is incomplete: declared page '{reference}' was not supplied.");
            }

            int pageBytes = Utf8Size(pageJson);
            aggregateBytes = checked(aggregateBytes + pageBytes);
            if (pageBytes > MaxDocumentBytes || aggregateBytes > MaxAggregateBytes)
            {
                return Failure($"pages[{index}]", "Catalogue page or aggregate byte limit was exceeded.");
            }

            if (!TryDeserialize(pageJson, $"pages[{index}]", out GeneratedPage? page, out var pageFailure))
            {
                return pageFailure!;
            }

            if (page!.Version != 1 || page.Page != index + 1 || page.TotalPages != manifest.Pages.Count)
            {
                return Failure($"pages[{index}]", "Catalogue page version, sequence, or total conflicts with the manifest.");
            }

            foreach (var item in page.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id)
                    || string.IsNullOrWhiteSpace(item.Source)
                    || string.IsNullOrWhiteSpace(item.Reference))
                {
                    return Failure($"pages[{index}].items", "Catalogue item id, source, and immutable reference are required.");
                }

                if (!identities.Add(item.Id))
                {
                    return Failure($"pages[{index}].items", $"Duplicate plugin identity '{item.Id}' was declared.");
                }

                if (!TryResolveContainedReference(origin, item.Source, out Uri? resolvedSourceUri)
                    || resolvedSourceUri is null)
                {
                    return Failure($"pages[{index}].items", $"Plugin source '{item.Source}' is not a safe catalogue-relative reference.");
                }

                string resolvedSource = resolvedSourceUri.AbsoluteUri;
                if (!sources.Add(resolvedSource))
                {
                    return Failure($"pages[{index}].items", $"Duplicate source identity '{resolvedSource}' was declared.");
                }

                entries.Add(new MarketplacePluginEntry
                {
                    Name = item.Id,
                    Source = resolvedSource,
                    Description = item.Description,
                    Version = item.Reference,
                    Keywords = item.Keywords,
                    Components = CreateInventory(item),
                });
            }
        }

        if (entries.Count != manifest.TotalItems)
        {
            return Failure("totalItems", $"Catalogue is incomplete: manifest declares {manifest.TotalItems} items but {entries.Count} were supplied.");
        }

        var catalog = new MarketplaceCatalog
        {
            Name = manifest.Name,
            Owner = new PluginParty { Name = manifest.Owner },
            Description = manifest.Description,
            Plugins = entries,
        };

        var nativeValidation = _nativeParser.ParseMarketplace(JsonSerializer.Serialize(catalog, JsonOptions));
        return nativeValidation.IsValid
            ? PluginParseResult<MarketplaceCatalogProjection>.Success(new MarketplaceCatalogProjection { Catalog = catalog })
            : PluginParseResult<MarketplaceCatalogProjection>.Failure(nativeValidation.Errors);
    }

    private static MarketplaceComponentInventory CreateInventory(GeneratedItem item)
    {
        var kinds = new List<MarketplaceComponentKind>(2);
        if (item.Skills.Count > 0)
        {
            kinds.Add(MarketplaceComponentKind.Skill);
        }

        if (item.Agents.Count > 0)
        {
            kinds.Add(MarketplaceComponentKind.Agent);
        }

        return new MarketplaceComponentInventory
        {
            Skills = item.Skills,
            Agents = item.Agents,
            Kinds = kinds,
        };
    }

    private static bool IsSafeRelativeReference(Uri origin, string reference) =>
        TryResolveContainedReference(origin, reference, out _);

    private static bool TryResolveContainedReference(Uri origin, string reference, out Uri? resolvedReference)
    {
        resolvedReference = null;
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Contains('\\', StringComparison.Ordinal)
            || Uri.TryCreate(reference, UriKind.Absolute, out _))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, reference, out var resolved)
            || resolved.Scheme != origin.Scheme
            || resolved.Host != origin.Host
            || resolved.Port != origin.Port
            || !string.IsNullOrEmpty(resolved.Query)
            || !string.IsNullOrEmpty(resolved.Fragment))
        {
            return false;
        }

        string basePath = origin.AbsolutePath.EndsWith('/') ? origin.AbsolutePath : origin.AbsolutePath + "/";
        if (!resolved.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal))
        {
            return false;
        }

        resolvedReference = resolved;
        return true;
    }

    private static bool TryDeserialize<T>(
        string json,
        string field,
        out T? value,
        out PluginParseResult<MarketplaceCatalogProjection>? failure)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            failure = value is null ? Failure(field, $"Catalogue {field} deserialised to null.") : null;
            return value is not null;
        }
        catch (JsonException ex)
        {
            value = null;
            failure = Failure(field, $"Catalogue {field} is malformed: {ex.Message}");
            return false;
        }
    }

    private static int Utf8Size(string value) => System.Text.Encoding.UTF8.GetByteCount(value ?? string.Empty);

    private static PluginParseResult<MarketplaceCatalogProjection> Failure(string field, string message) =>
        PluginParseResult<MarketplaceCatalogProjection>.Failure(field, message);

    private sealed record GeneratedManifest
    {
        public int Version { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Owner { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int TotalItems { get; init; }
        public IReadOnlyList<string> Pages { get; init; } = [];
    }

    private sealed record GeneratedPage
    {
        public int Version { get; init; }
        public int Page { get; init; }
        public int TotalPages { get; init; }
        public IReadOnlyList<GeneratedItem> Items { get; init; } = [];
    }

    private sealed record GeneratedItem
    {
        public string Id { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Reference { get; init; } = string.Empty;
        public string? Description { get; init; }
        public IReadOnlyList<string>? Keywords { get; init; }
        public IReadOnlyList<string> Skills { get; init; } = [];
        public IReadOnlyList<string> Agents { get; init; } = [];
    }
}
