using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Represents session jsonl.
/// </summary>
public static class SessionJsonl
{
    public static async Task WriteAllAsync<TEntry>(
        IFileSystem fileSystem,
        string path,
        IEnumerable<TEntry> entries,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.Directory.CreateDirectory(directory);

        await using var stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var entry in entries)
        {
            var json = JsonSerializer.Serialize(entry, options);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
    }

    public static async Task<IReadOnlyList<TEntry>> ReadAllAsync<TEntry>(
        IFileSystem fileSystem,
        string path,
        JsonSerializerOptions options,
        ILogger? logger = null,
        string? malformedEntryContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!fileSystem.File.Exists(path))
            return [];

        var lines = await fileSystem.File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = new List<TEntry>(lines.Length);
        foreach (var line in lines.Where(static line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<TEntry>(line, options);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "Skipping malformed {Context} JSONL entry", malformedEntryContext ?? typeof(TEntry).Name);
            }
        }

        return entries;
    }
}
