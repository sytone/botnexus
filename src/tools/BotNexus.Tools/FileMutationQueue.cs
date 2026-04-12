using System.Collections.Concurrent;

namespace BotNexus.Tools;

/// <summary>
/// Represents file mutation queue.
/// </summary>
public sealed class FileMutationQueue
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the shared.
    /// </summary>
    public static FileMutationQueue Shared { get; } = new();

    public async Task<T> WithFileLockAsync<T>(string path, Func<Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(operation);

        var key = Path.GetFullPath(path);
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
