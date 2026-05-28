using System.Text.Json;

namespace BotNexus.Agent.Providers.IntegrationMock;

/// <summary>
/// Resolves and parses mock response catalogs. Catalog source precedence:
/// <list type="number">
///   <item><description>Catalog passed to the constructor (in-memory override, for tests).</description></item>
///   <item><description>Per-request path from <c>model.BaseUrl</c> when it points at an existing file.</description></item>
///   <item><description><c>BOTNEXUS_MOCK_CATALOG</c> environment variable.</description></item>
///   <item><description>Built-in default catalog containing the <c>HELLO_WORLD</c> script.</description></item>
/// </list>
/// File catalogs are parsed once and cached by absolute path. The built-in default is always
/// consulted as a fallback so well-known scripts like <c>HELLO_WORLD</c> are available even
/// when a custom catalog omits them.
/// </summary>
public sealed class MockCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, MockCatalog> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly MockCatalog? _override;

    /// <summary>
    /// Create a loader. When <paramref name="explicitCatalog"/> is supplied it is used in
    /// preference to any file/environment source — primarily for unit testing.
    /// </summary>
    public MockCatalogLoader(MockCatalog? explicitCatalog = null)
    {
        _override = explicitCatalog;
    }

    /// <summary>
    /// Resolve the catalog for a request. A <paramref name="catalogPath"/> sourced from
    /// <c>model.BaseUrl</c> takes precedence over the environment variable when it points
    /// at an existing file.
    /// </summary>
    public MockCatalog Resolve(string? catalogPath)
    {
        if (_override is not null)
            return _override;

        if (!string.IsNullOrWhiteSpace(catalogPath) && File.Exists(catalogPath))
            return LoadCached(catalogPath);

        var fromEnv = Environment.GetEnvironmentVariable("BOTNEXUS_MOCK_CATALOG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return LoadCached(fromEnv);

        return DefaultCatalog.Catalog;
    }

    /// <summary>
    /// Looks up a script in <paramref name="catalog"/>, falling back to the built-in default
    /// catalog so well-known keys remain available when a custom catalog omits them.
    /// </summary>
    public IReadOnlyList<ScriptedResponseStep>? Lookup(MockCatalog catalog, string key)
    {
        if (catalog.Scripts is not null && catalog.Scripts.TryGetValue(key, out var script))
            return script;

        if (!ReferenceEquals(catalog, DefaultCatalog.Catalog)
            && DefaultCatalog.Catalog.Scripts.TryGetValue(key, out var fallback))
            return fallback;

        return null;
    }

    private MockCatalog LoadCached(string path)
    {
        var fullPath = Path.GetFullPath(path);

        lock (_gate)
        {
            if (_cache.TryGetValue(fullPath, out var cached))
                return cached;

            var json = File.ReadAllText(fullPath);
            var parsed = JsonSerializer.Deserialize<MockCatalog>(json, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Mock catalog at '{fullPath}' deserialized to null.");

            _cache[fullPath] = parsed;
            return parsed;
        }
    }
}
