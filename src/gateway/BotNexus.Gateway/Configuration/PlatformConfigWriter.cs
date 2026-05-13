using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thread-safe writer for platform config JSON files.
/// Performs atomic read-modify-write with file locking.
/// </summary>
public sealed class PlatformConfigWriter
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly JsonSerializerOptions PlatformReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions PlatformWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem;
    private readonly ConfigBackupService? _backup;

    public PlatformConfigWriter(string configPath, IFileSystem fileSystem, ConfigBackupService? backup = null)
    {
        _configPath = configPath;
        _fileSystem = fileSystem;
        _backup = backup;
    }

    /// <summary>
    /// Reads the full config as a JSON object.
    /// </summary>
    public async Task<JsonObject> ReadAsync(CancellationToken ct = default)
    {
        return await ReadRootAsync(ct);
    }

    /// <summary>
    /// Reads the current platform configuration as a strongly-typed object.
    /// </summary>
    public async Task<PlatformConfig> ReadPlatformConfigAsync(CancellationToken ct = default)
    {
        var root = await ReadRootAsync(ct);
        var json = root.ToJsonString();
        return JsonSerializer.Deserialize<PlatformConfig>(json, PlatformReadOptions) ?? new PlatformConfig();
    }

    /// <summary>
    /// Atomically updates a section of the config.
    /// </summary>
    public async Task UpdateSectionAsync(string sectionName, JsonNode value, CancellationToken ct = default)
        => await MutateAsync(
            root => root[sectionName] = value,
            $"before-{sectionName}-update",
            ct);

    /// <summary>
    /// Replaces the entire platform configuration document.
    /// </summary>
    public async Task UpdatePlatformConfigAsync(PlatformConfig config, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await MutateAsync(root =>
        {
            var serialized = JsonSerializer.Serialize(config, PlatformWriteOptions);
            var next = JsonNode.Parse(serialized)?.AsObject() ?? new JsonObject();
            root.Clear();
            foreach (var kvp in next)
                root[kvp.Key] = kvp.Value?.DeepClone();
        }, reason, ct);
    }

    /// <summary>
    /// Updates a keyed entry within a section (e.g., providers.github-copilot).
    /// </summary>
    public async Task UpdateSectionEntryAsync(string sectionName, string key, JsonNode value, CancellationToken ct = default)
        => await MutateAsync(root =>
        {
            if (root[sectionName] is not JsonObject section)
            {
                section = new JsonObject();
                root[sectionName] = section;
            }

            section[key] = value;
        }, $"before-{sectionName}-update", ct);

    /// <summary>
    /// Atomically mutates the config document and persists the result.
    /// </summary>
    public async Task MutateAsync(Action<JsonObject> mutation, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await MutateAsync(root =>
        {
            mutation(root);
            return Task.CompletedTask;
        }, reason, ct);
    }

    /// <summary>
    /// Atomically mutates the config document and persists the result.
    /// </summary>
    public async Task MutateAsync(Func<JsonObject, Task> mutation, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadRootAsync(ct);
            await mutation(root);
            await WriteRootAsync(root, reason, ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Removes a keyed entry from a section.
    /// </summary>
    public async Task RemoveSectionEntryAsync(string sectionName, string key, CancellationToken ct = default)
        => await MutateAsync(root =>
        {
            if (root[sectionName] is JsonObject section)
                section.Remove(key);
        }, $"before-{sectionName}-remove", ct);

    private async Task<JsonObject> ReadRootAsync(CancellationToken ct)
    {
        if (!_fileSystem.File.Exists(_configPath))
            return new JsonObject();

        var json = await _fileSystem.File.ReadAllTextAsync(_configPath, ct);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private async Task WriteRootAsync(JsonObject root, string reason, CancellationToken ct)
    {
        _backup?.Backup(_configPath, reason);

        var directory = _fileSystem.Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            _fileSystem.Directory.CreateDirectory(directory);

        var tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await _fileSystem.File.WriteAllTextAsync(tempPath, json, ct);
            _fileSystem.File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            if (_fileSystem.File.Exists(tempPath))
                _fileSystem.File.Delete(tempPath);
        }
    }
}
