using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// A substitutable source fetcher that writes a scripted file set into the staging directory.
/// Exists so the lifecycle can be pinned without a network or a git binary, and - critically -
/// so a fault can be injected PART WAY THROUGH materialisation, which is the only honest way to
/// test the all-or-nothing install guarantee.
/// </summary>
internal sealed class FakePluginSourceFetcher : IPluginSourceFetcher
{
    private readonly Queue<FakeFetch> _fetches = new();

    /// <summary>Every staging directory the fetcher was handed, in call order.</summary>
    public List<string> StagingDirectories { get; } = [];

    /// <summary>Arguments of each call, for asserting update re-resolves the recorded source.</summary>
    public List<(string Source, string? Reference)> Calls { get; } = [];

    public void Enqueue(string resolvedVersion, IReadOnlyDictionary<string, string> files) =>
        _fetches.Enqueue(new FakeFetch(resolvedVersion, files, FaultAfterFiles: null));

    /// <summary>
    /// Queues a fetch that writes <paramref name="faultAfterFiles"/> files and then throws,
    /// simulating a clone that dies mid-transfer.
    /// </summary>
    public void EnqueueFaulting(IReadOnlyDictionary<string, string> files, int faultAfterFiles) =>
        _fetches.Enqueue(new FakeFetch("never-resolved", files, faultAfterFiles));

    public Task<PluginFetchResult> FetchAsync(
        string source,
        string? reference,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((source, reference));
        StagingDirectories.Add(stagingDirectory);

        if (_fetches.Count == 0)
        {
            throw new InvalidOperationException("No fetch was queued for this call.");
        }

        var fetch = _fetches.Dequeue();
        var written = 0;
        foreach (var (relative, content) in fetch.Files)
        {
            if (fetch.FaultAfterFiles is { } limit && written == limit)
            {
                throw new IOException("Simulated transport failure part way through materialisation.");
            }

            var path = Path.Combine(stagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            written++;
        }

        if (fetch.FaultAfterFiles is not null)
        {
            throw new IOException("Simulated transport failure part way through materialisation.");
        }

        return Task.FromResult(new PluginFetchResult(fetch.ResolvedVersion));
    }

    private sealed record FakeFetch(
        string ResolvedVersion,
        IReadOnlyDictionary<string, string> Files,
        int? FaultAfterFiles);
}
