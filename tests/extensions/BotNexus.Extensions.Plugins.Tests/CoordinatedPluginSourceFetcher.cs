using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

internal sealed class CoordinatedPluginSourceFetcher : IPluginSourceFetcher
{
    private readonly Queue<Fetch> _fetches = new();
    private readonly TaskCompletionSource _firstFetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstFetchEntered => _firstFetchEntered.Task;

    public int CallCount { get; private set; }

    public void Enqueue(string resolvedVersion, IReadOnlyDictionary<string, string> files) =>
        _fetches.Enqueue(new Fetch(resolvedVersion, files, BlockUntilReleased: false, BlockUntilCancelled: false));

    public void EnqueueBlocked(string resolvedVersion, IReadOnlyDictionary<string, string> files) =>
        _fetches.Enqueue(new Fetch(resolvedVersion, files, BlockUntilReleased: true, BlockUntilCancelled: false));

    public void EnqueueCancellationBlocked() =>
        _fetches.Enqueue(new Fetch(string.Empty, new Dictionary<string, string>(), BlockUntilReleased: false, BlockUntilCancelled: true));

    public void ReleaseFirstFetch() => _releaseFirstFetch.TrySetResult();

    public async Task<PluginFetchResult> FetchAsync(
        string source,
        string? reference,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        var fetch = _fetches.Dequeue();
        CallCount++;
        if (CallCount == 1)
        {
            _firstFetchEntered.TrySetResult();
        }

        if (fetch.BlockUntilReleased)
        {
            await _releaseFirstFetch.Task.WaitAsync(cancellationToken);
        }
        else if (fetch.BlockUntilCancelled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        foreach (var (relativePath, content) in fetch.Files)
        {
            var path = Path.Combine(stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return new PluginFetchResult(fetch.ResolvedVersion);
    }

    private sealed record Fetch(
        string ResolvedVersion,
        IReadOnlyDictionary<string, string> Files,
        bool BlockUntilReleased,
        bool BlockUntilCancelled);
}
