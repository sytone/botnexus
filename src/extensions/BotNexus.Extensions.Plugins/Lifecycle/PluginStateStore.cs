using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Persists the installed-plugin records to a single JSON file under the plugin root.
/// </summary>
/// <remarks>
/// A file rather than a database because the plugin root is already the unit of backup and
/// inspection, and a human debugging a bad install needs to be able to read the record next to
/// the content it describes. Writes go through a temp file and a replace so an interrupted write
/// cannot leave a truncated state file - losing the record would orphan every installed plugin,
/// since the record is the only thing that knows which files to remove.
/// </remarks>
public sealed class PluginStateStore
{
    /// <summary>File name of the state document inside the plugin root.</summary>
    public const string StateFileName = "installed-plugins.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _statePath;
    private readonly IFileSystem _fileSystem;

    /// <summary>Creates a store over the state file inside <paramref name="pluginRoot"/>.</summary>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="fileSystem">
    /// Filesystem abstraction, defaulting to the real filesystem. Injectable so a consumer that
    /// merely READS the record set - notably skill discovery - can be exercised against an
    /// in-memory filesystem without materialising plugin content on disk.
    /// </param>
    public PluginStateStore(string pluginRoot, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        _fileSystem = fileSystem ?? new FileSystem();
        PluginRoot = _fileSystem.Path.GetFullPath(pluginRoot);
        _statePath = _fileSystem.Path.Combine(PluginRoot, StateFileName);
    }

    /// <summary>Absolute path of the directory holding installed plugins.</summary>
    public string PluginRoot { get; }

    /// <summary>Absolute path of the state document.</summary>
    public string StatePath => _statePath;

    /// <summary>
    /// Reads every installed-plugin record. A missing state file yields an empty list, which is
    /// the correct reading of "nothing has been installed here yet".
    /// </summary>
    public IReadOnlyList<InstalledPlugin> Read()
    {
        if (!_fileSystem.File.Exists(_statePath))
        {
            return [];
        }

        var json = _fileSystem.File.ReadAllText(_statePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<InstalledPlugin>>(json, SerializerOptions) ?? [];
    }

    /// <summary>
    /// Resolves execution policy from this configured store, binding exact identity to a canonical
    /// direct-child directory. Unlike inspection reads, absent, empty, null or ambiguous state is an error.
    /// </summary>
    /// <param name="name">Requested plugin identity.</param>
    /// <param name="directory">Directory whose content would execute.</param>
    /// <returns>The single authoritative record, including its effective activation policy.</returns>
    public InstalledPlugin ResolveForExecution(string name, string directory)
    {
        if (!new PluginManifestParser().ParseManifest(JsonSerializer.Serialize(new { name })).IsValid)
            throw new InvalidDataException("Invalid installed plugin identity.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(_fileSystem.Path.GetFullPath(PluginRoot));
        var actual = Path.TrimEndingDirectorySeparator(_fileSystem.Path.GetFullPath(directory));
        var expected = Path.TrimEndingDirectorySeparator(_fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(root, name)));
        if (!string.Equals(actual, expected, comparison))
            throw new InvalidDataException("Installed plugin identity and directory do not agree.");

        // Reject aliases rather than trying to infer authority through a filesystem link.
        for (var current = actual; current is not null; current = _fileSystem.Path.GetDirectoryName(current))
        {
            if ((_fileSystem.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installed plugin path contains a filesystem link.");
        }
        if ((_fileSystem.File.GetAttributes(_statePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Installed plugin policy must not be a filesystem link.");

        // No File.Exists precheck: inaccessible/missing policy must throw, not become native admission.
        var json = _fileSystem.File.ReadAllText(_statePath);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Installed plugin policy must be an array.");
        RejectDuplicateFields(document.RootElement);
        var records = JsonSerializer.Deserialize<List<InstalledPlugin?>>(json, SerializerOptions)
            ?? throw new InvalidDataException("Installed plugin policy is unavailable.");
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.Name) || !names.Add(record.Name))
                throw new InvalidDataException("Installed plugin policy has ambiguous identities.");
        }
        return records.SingleOrDefault(record => record is not null && string.Equals(record.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidDataException("No authoritative installed policy matches this plugin.");
    }

    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Installed plugin policy has duplicate fields.");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateFields(item);
    }

    /// <summary>Returns the record for <paramref name="name"/>, or <c>null</c> when not installed.</summary>
    /// <param name="name">Plugin identifier.</param>
    public InstalledPlugin? Find(string name) =>
        Read().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>Replaces the whole record set atomically.</summary>
    /// <param name="plugins">Records to persist.</param>
    public void Write(IReadOnlyList<InstalledPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _fileSystem.Directory.CreateDirectory(PluginRoot);

        var ordered = plugins.OrderBy(static p => p.Name, StringComparer.Ordinal).ToList();
        var json = JsonSerializer.Serialize(ordered, SerializerOptions);

        var temp = _statePath + ".tmp";
        _fileSystem.File.WriteAllText(temp, json);
        _fileSystem.File.Move(temp, _statePath, overwrite: true);
    }

    /// <summary>Inserts or replaces one record, keyed by <see cref="InstalledPlugin.Name"/>.</summary>
    /// <param name="plugin">Record to persist.</param>
    public void Upsert(InstalledPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var all = Read().Where(p => !string.Equals(p.Name, plugin.Name, StringComparison.Ordinal)).ToList();
        all.Add(plugin);
        Write(all);
    }

    /// <summary>Removes one record by name. Returns <c>false</c> when no such record existed.</summary>
    /// <param name="name">Plugin identifier.</param>
    public bool Delete(string name)
    {
        var all = Read();
        var remaining = all.Where(p => !string.Equals(p.Name, name, StringComparison.Ordinal)).ToList();
        if (remaining.Count == all.Count)
        {
            return false;
        }

        Write(remaining);
        return true;
    }
}
