namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Outcome of fetching a marketplace source into a staging directory.
/// </summary>
/// <param name="ResolvedVersion">
/// The exact revision the source resolved to - a commit SHA for a git source. This is what the
/// installed record stores, never the requested reference: a branch name records nothing about
/// what content actually landed, so "did the source move?" would be unanswerable.
/// </param>
public sealed record PluginFetchResult(string ResolvedVersion);

/// <summary>
/// Fetches plugin content from a marketplace source into a caller-owned staging directory.
/// </summary>
/// <remarks>
/// This is the seam that keeps the lifecycle testable without a network or a git binary. The
/// production implementation shells out to <c>git clone</c> (the transport decision settled in
/// #2623); tests substitute a fetcher that writes known content, or that faults part-way through
/// writing it, which is the only way to pin the all-or-nothing install guarantee deterministically.
/// </remarks>
public interface IPluginSourceFetcher
{
    /// <summary>
    /// Materialises the source into <paramref name="stagingDirectory"/>, which the caller has
    /// already created and owns. Implementations must not touch anything outside it.
    /// </summary>
    /// <param name="source">Marketplace source - a git URL for the git transport.</param>
    /// <param name="reference">
    /// Branch, tag or commit to check out, or <c>null</c> to take the source's default branch.
    /// </param>
    /// <param name="stagingDirectory">Existing, empty directory to fetch into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revision the source resolved to.</returns>
    Task<PluginFetchResult> FetchAsync(
        string source,
        string? reference,
        string stagingDirectory,
        CancellationToken cancellationToken = default);
}
