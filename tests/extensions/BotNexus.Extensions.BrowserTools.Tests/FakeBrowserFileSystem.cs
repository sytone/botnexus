using System.Text;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// In-memory <see cref="IBrowserFileSystem"/> for tests (#3029).
/// </summary>
/// <remarks>
/// Replaces the NuGet mock filesystem, which can no longer be used here: #3029 AC2 forbids the
/// production project from taking that package, so the production seam is now
/// <see cref="IBrowserFileSystem"/> and the fake has to speak the same interface. Keeping the
/// fake in-memory also keeps the "no real file writes" rule structural rather than aspirational.
/// </remarks>
public sealed class FakeBrowserFileSystem : IBrowserFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    /// <summary>Absolute paths of every file currently present.</summary>
    public IReadOnlyCollection<string> AllFiles => _files.Keys;

    /// <summary>Directories created through <see cref="CreateDirectory"/>.</summary>
    public IReadOnlyCollection<string> CreatedDirectories => _directories;

    /// <summary>Paths deleted through <see cref="DeleteFile"/>, in order.</summary>
    public List<string> DeletedFiles { get; } = [];

    /// <summary>Seeds a file so a resolution branch can find it.</summary>
    public FakeBrowserFileSystem AddFile(string path, string contents = "")
    {
        _files[path] = Encoding.UTF8.GetBytes(contents);
        return this;
    }

    /// <summary>Reads a seeded or written file back as UTF-8 text.</summary>
    public string GetFileText(string path) => Encoding.UTF8.GetString(_files[path]);

    /// <summary>Reads a seeded or written file back as raw bytes.</summary>
    public byte[] GetFileBytes(string path) => _files[path];

    /// <inheritdoc />
    public string CombinePath(params string[] segments) => Path.Combine(segments);

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _directories.Add(path);

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        DeletedFiles.Add(path);
        _files.Remove(path);
    }

    /// <inheritdoc />
    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        _files[path] = Encoding.UTF8.GetBytes(contents);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
    {
        _files[path] = contents;
        return Task.CompletedTask;
    }
}
