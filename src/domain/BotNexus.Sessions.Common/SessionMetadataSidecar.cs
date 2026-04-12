using System.IO.Abstractions;
using System.Text.Json;

namespace BotNexus.Sessions.Common;

public static class SessionMetadataSidecar
{
    public static async Task WriteAsync<TMetadata>(
        IFileSystem fileSystem,
        string path,
        TMetadata metadata,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(metadata, options);
        await fileSystem.File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TMetadata?> ReadAsync<TMetadata>(
        IFileSystem fileSystem,
        string path,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!fileSystem.File.Exists(path))
            return default;

        var json = await fileSystem.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TMetadata>(json, options);
    }
}
