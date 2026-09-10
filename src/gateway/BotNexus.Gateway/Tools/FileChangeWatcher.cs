namespace BotNexus.Gateway.Tools;

/// <summary>The filesystem transitions <see cref="FileWatcherTool"/> can be asked to wait for.</summary>
public enum FileChangeKind
{
    /// <summary>The watched file's contents or last-write time changed.</summary>
    Modified,

    /// <summary>The watched file appeared.</summary>
    Created,

    /// <summary>The watched file was removed.</summary>
    Deleted,

    /// <summary>The watched file was renamed.</summary>
    Renamed
}

/// <summary>
/// Raises change notifications for a single watched file.
/// </summary>
/// <remarks>
/// This exists so <see cref="FileWatcherTool"/>'s own logic — debounce coalescing, the readiness
/// notice's ordering against arming, timeout clamping, result formatting — can be exercised by
/// driving the callback directly instead of writing to a real filesystem and hoping the operating
/// system reports it inside the test's budget. Waiting on a real <see cref="FileSystemWatcher"/>
/// made <c>FileWatcherToolTests</c> fail on unrelated PRs (#75, #103) because a saturated runner
/// delivers the event late, not because anything was wrong.
/// </remarks>
public interface IFileChangeWatcher : IDisposable
{
    /// <summary>Raised for each observed change, but only after <see cref="Arm"/> has returned.</summary>
    event Action<FileChangeKind>? Changed;

    /// <summary>
    /// Begins raising events. Changes that happen before this returns are not observable, so a
    /// caller must not announce readiness until it has been called.
    /// </summary>
    void Arm();
}

/// <summary>Creates the watcher <see cref="FileWatcherTool"/> arms for one execution.</summary>
public interface IFileChangeWatcherFactory
{
    /// <summary>
    /// Creates a watcher for <paramref name="fileName"/> inside <paramref name="directory"/> that
    /// reports only the requested <paramref name="kinds"/>.
    /// </summary>
    IFileChangeWatcher Create(string directory, string fileName, IReadOnlyCollection<FileChangeKind> kinds);
}

/// <summary>Produces watchers backed by the operating system's <see cref="FileSystemWatcher"/>.</summary>
public sealed class FileSystemChangeWatcherFactory : IFileChangeWatcherFactory
{
    /// <inheritdoc />
    public IFileChangeWatcher Create(string directory, string fileName, IReadOnlyCollection<FileChangeKind> kinds)
        => new FileSystemChangeWatcher(directory, fileName, kinds);
}

/// <summary>Adapts <see cref="FileSystemWatcher"/> to <see cref="IFileChangeWatcher"/>.</summary>
internal sealed class FileSystemChangeWatcher : IFileChangeWatcher
{
    private readonly FileSystemWatcher _watcher;

    internal FileSystemChangeWatcher(string directory, string fileName, IReadOnlyCollection<FileChangeKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        if (kinds.Contains(FileChangeKind.Modified))
            _watcher.Changed += (_, _) => Raise(FileChangeKind.Modified);

        if (kinds.Contains(FileChangeKind.Created))
            _watcher.Created += (_, _) => Raise(FileChangeKind.Created);

        if (kinds.Contains(FileChangeKind.Deleted))
            _watcher.Deleted += (_, _) => Raise(FileChangeKind.Deleted);

        if (kinds.Contains(FileChangeKind.Renamed))
            _watcher.Renamed += (_, _) => Raise(FileChangeKind.Renamed);
    }

    /// <inheritdoc />
    public event Action<FileChangeKind>? Changed;

    /// <inheritdoc />
    public void Arm() => _watcher.EnableRaisingEvents = true;

    /// <inheritdoc />
    public void Dispose() => _watcher.Dispose();

    private void Raise(FileChangeKind kind) => Changed?.Invoke(kind);
}
