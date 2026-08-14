namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The only filesystem surface this extension is allowed to touch (#3029).
/// </summary>
/// <remarks>
/// <para>
/// This interface exists instead of a NuGet filesystem-abstraction package because AC2 of #3029
/// is binding: this project declares no <c>PackageReference</c> beyond those already in
/// <c>BotNexus.Extensions.WebTools.csproj</c>, and that project declares none. A four-method
/// seam defined here costs nothing at runtime and keeps the extension's load context identical
/// to WebTools'.
/// </para>
/// <para>
/// It is deliberately tiny. Every member is one the binary resolver or the snapshot spill path
/// actually calls; a wider surface would be a wider thing for a test fake to get subtly wrong.
/// </para>
/// </remarks>
public interface IBrowserFileSystem
{
    /// <summary>Combines path segments using the platform's separator.</summary>
    string CombinePath(params string[] segments);

    /// <summary>Whether a regular file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Creates <paramref name="path"/> and any missing parents. No-op if it exists.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes <paramref name="path"/>. No-op when the file is already absent.</summary>
    void DeleteFile(string path);

    /// <summary>Writes UTF-8 text, replacing any existing file.</summary>
    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>Writes raw bytes, replacing any existing file.</summary>
    Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real <see cref="IBrowserFileSystem"/>, a direct pass-through to <see cref="System.IO"/>.
/// </summary>
/// <remarks>
/// Contains no logic of its own on purpose: anything conditional here would be behaviour that the
/// test fake never exercises, which is exactly the behaviour that later breaks in production.
/// </remarks>
public sealed class BrowserFileSystem : IBrowserFileSystem
{
    /// <inheritdoc />
    public string CombinePath(params string[] segments) => Path.Combine(segments);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc />
    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        => File.WriteAllTextAsync(path, contents, cancellationToken);

    /// <inheritdoc />
    public Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        => File.WriteAllBytesAsync(path, contents, cancellationToken);
}
