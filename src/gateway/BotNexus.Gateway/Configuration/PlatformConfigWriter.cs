using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thread-safe writer for platform config JSON files.
/// Performs atomic read-modify-write with file locking.
/// </summary>
public sealed class PlatformConfigWriter
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem;

    public PlatformConfigWriter(string configPath, IFileSystem fileSystem)
    {
        _configPath = configPath;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Reads the full config as a JSON object.
    /// </summary>
    public async Task<JsonObject> ReadAsync(CancellationToken ct = default)
    {
        if (!_fileSystem.File.Exists(_configPath))
            return new JsonObject();

        var json = await _fileSystem.File.ReadAllTextAsync(_configPath, ct);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    /// <summary>
    /// Atomically updates a section of the config.
    /// </summary>
    public async Task UpdateSectionAsync(string sectionName, JsonNode value, CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadAsync(ct);
            root[sectionName] = value;
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = root.ToJsonString(options);
            await _fileSystem.File.WriteAllTextAsync(_configPath, json, ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Updates a keyed entry within a section (e.g., providers.github-copilot).
    /// </summary>
    public async Task UpdateSectionEntryAsync(string sectionName, string key, JsonNode value, CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadAsync(ct);
            if (root[sectionName] is not JsonObject section)
            {
                section = new JsonObject();
                root[sectionName] = section;
            }

            section[key] = value;
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = root.ToJsonString(options);
            await _fileSystem.File.WriteAllTextAsync(_configPath, json, ct);
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
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadAsync(ct);
            if (root[sectionName] is JsonObject section)
            {
                section.Remove(key);
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = root.ToJsonString(options);
                await _fileSystem.File.WriteAllTextAsync(_configPath, json, ct);
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
