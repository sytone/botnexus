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
