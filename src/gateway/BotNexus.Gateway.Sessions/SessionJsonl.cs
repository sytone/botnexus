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

    public static async Task AppendAsync<TEntry>(
        IFileSystem fileSystem,
        string path,
        IEnumerable<TEntry> entries,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.Directory.CreateDirectory(directory);

        await using var stream = fileSystem.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.Position = stream.Length;
        await AppendToStreamAsync(stream, entries, options, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task AppendToStreamAsync<TEntry>(
        Stream stream,
        IEnumerable<TEntry> entries,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        var originalLength = stream.Length;
        stream.Position = originalLength;

        try
        {
            foreach (var entry in entries)
            {
                var json = JsonSerializer.Serialize(entry, options);
                var line = Encoding.UTF8.GetBytes(json + "\n");
                await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception appendException)
        {
            try
            {
                stream.SetLength(originalLength);
                stream.Position = originalLength;
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new IOException(
                    "Session JSONL append failed and its partial write could not be rolled back.",
                    new AggregateException(appendException, rollbackException));
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(appendException).Throw();
            throw;
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
