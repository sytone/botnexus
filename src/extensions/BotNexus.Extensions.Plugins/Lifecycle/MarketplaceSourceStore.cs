using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Persists the operator's marketplace sources to a JSON file under the plugin root.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="PluginStateStore"/>, in the same directory, written
/// the same way: a temp file then a replace, so an interrupted write cannot truncate the document.
/// Losing this file is milder than losing the installed-plugin record - no installed content is
/// orphaned, only the list of places to look - but it is still the operator's configuration and it
/// is cheap to protect.
/// </remarks>
public sealed partial class MarketplaceSourceStore
{
    /// <summary>File name of the sources document inside the plugin root.</summary>
    public const string StateFileName = "marketplace-sources.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _statePath;
    private readonly IFileSystem _fileSystem;

    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="fileSystem">Filesystem abstraction, defaulting to the real one.</param>
    public MarketplaceSourceStore(string pluginRoot, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        _fileSystem = fileSystem ?? new FileSystem();
        PluginRoot = _fileSystem.Path.GetFullPath(pluginRoot);
        _statePath = _fileSystem.Path.Combine(PluginRoot, StateFileName);
    }

    /// <summary>Absolute path of the directory holding installed plugins.</summary>
    public string PluginRoot { get; }

    /// <summary>Absolute path of the sources document.</summary>
    public string StatePath => _statePath;

    /// <summary>
    /// Reads every source. A missing file yields an empty list - the correct reading of "no
    /// sources have been added here yet".
    /// </summary>
    public IReadOnlyList<MarketplaceSource> Read()
    {
        if (!_fileSystem.File.Exists(_statePath))
            return [];

        var json = _fileSystem.File.ReadAllText(_statePath);

        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<MarketplaceSource>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses must not take the plugins page down with
            // it. Sources are re-addable; the installed plugins they led to are unaffected.
            return [];
        }
    }

    /// <summary>Returns the source named <paramref name="name"/>, or null.</summary>
    /// <param name="name">Source identifier.</param>
    public MarketplaceSource? Find(string name) =>
        Read().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Replaces the whole set atomically.</summary>
    /// <param name="sources">Records to persist.</param>
    public void Write(IReadOnlyList<MarketplaceSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _fileSystem.Directory.CreateDirectory(PluginRoot);

        var ordered = sources.OrderBy(static s => s.Name, StringComparer.Ordinal).ToList();
        var json = JsonSerializer.Serialize(ordered, SerializerOptions);

        var temp = _statePath + ".tmp";
        _fileSystem.File.WriteAllText(temp, json);
        _fileSystem.File.Move(temp, _statePath, overwrite: true);
    }

    /// <summary>Inserts or replaces one source, keyed by name.</summary>
    /// <param name="source">Record to persist.</param>
    public void Upsert(MarketplaceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var all = Read().Where(s => !string.Equals(s.Name, source.Name, StringComparison.Ordinal)).ToList();
        all.Add(source);
        Write(all);
    }

    /// <summary>Removes one source by name. Returns false when there was no such source.</summary>
    /// <param name="name">Source identifier.</param>
    public bool Delete(string name)
    {
        var all = Read();
        var remaining = all.Where(s => !string.Equals(s.Name, name, StringComparison.Ordinal)).ToList();

        if (remaining.Count == all.Count)
            return false;

        Write(remaining);
        return true;
    }

    /// <summary>
    /// Derives a stable kebab-case name from a git URL, so the operator can paste a link and not
    /// be asked to invent an identifier.
    /// </summary>
    /// <remarks>
    /// Owner and repository together, because the repository name alone collides readily - two
    /// people's "botnexus-plugins" are not the same source and must not overwrite one another.
    /// </remarks>
    /// <param name="url">Git URL.</param>
    public static string DeriveName(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var trimmed = url.Trim().TrimEnd('/');

        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        // Take the last two path segments: owner and repo. scp-style git@host:owner/repo is
        // handled by treating ':' as a separator too.
        var parts = trimmed
            .Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Contains('@') && !p.Contains('.') || !p.Contains('/'))
            .ToArray();

        var tail = parts.Length >= 2 ? $"{parts[^2]}-{parts[^1]}" : parts.LastOrDefault() ?? "source";
        var slug = NonSlugCharacters().Replace(tail.ToLowerInvariant(), "-").Trim('-');
        slug = CollapseDashes().Replace(slug, "-");

        return string.IsNullOrEmpty(slug) ? "source" : slug[..Math.Min(slug.Length, 64)].Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    [GeneratedRegex("-{2,}")]
    private static partial Regex CollapseDashes();
}
