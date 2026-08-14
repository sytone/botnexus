using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli;

/// <summary>
/// The outcome of resolving a named platform SQLite store for a read-only CLI reader.
/// </summary>
/// <param name="Path">
/// The store path the caller should open. When <see cref="Found"/> is true this is an existing
/// file; otherwise it is the canonical path a writer <em>would</em> create, so an error message
/// still names something meaningful.
/// </param>
/// <param name="Found">True when an existing store file was discovered.</param>
/// <param name="SearchedDirectories">
/// Every directory probed, in probe order. Surfaced in the not-found message so a
/// wrong-directory deployment (data dir != home) is diagnosable from the message alone
/// rather than from a single guessed path (issue #3126, AC4).
/// </param>
/// <param name="CandidateFileNames">Every file name probed within each directory, in probe order.</param>
internal sealed record StoreResolution(
    string Path,
    bool Found,
    IReadOnlyList<string> SearchedDirectories,
    IReadOnlyList<string> CandidateFileNames);

/// <summary>
/// Canonical, tolerant resolution of BotNexus SQLite stores for CLI readers.
/// <para>
/// This is the single definition of the writer/reader filename contract (issue #3126). BotNexus
/// mixes two SQLite extensions - <c>.db</c> and <c>.sqlite</c> - and writers place runtime stores
/// under the configured <em>data</em> directory (<c>BOTNEXUS_DATA_DIR</c> /
/// <see cref="BotNexusHome.ResolveDataPath(string?)"/>), which is deliberately allowed to differ
/// from the config home so the store still works when the config directory is mounted read-only.
/// A reader that hard-codes <c>Path.Combine(home, "sessions.db")</c> therefore gets the filename
/// <em>and</em> the directory wrong, and can never open a real store on any deployment.
/// </para>
/// <para>
/// The tolerant naming here is equivalent to <c>DebugDbCommand</c>'s normalisation: a request for
/// <c>sessions</c>, <c>sessions.sqlite</c> or <c>sessions.db</c> all resolve to whichever of the
/// two extensions actually exists on disk.
/// </para>
/// </summary>
internal static class CliStorePaths
{
    /// <summary>
    /// Extensions probed for every store, in preference order. <c>.sqlite</c> comes first because
    /// it is what every current writer creates; <c>.db</c> is retained so pre-existing stores from
    /// older deployments still resolve.
    /// </summary>
    private static readonly string[] Extensions = [".sqlite", ".db"];

    /// <summary>
    /// Resolves a named store (e.g. <c>"sessions"</c>, <c>"cron"</c>) tolerantly across both SQLite
    /// extensions and every directory a writer may have used.
    /// </summary>
    /// <param name="storeName">
    /// Bare store name or a name carrying either extension - <c>sessions</c>, <c>sessions.db</c>
    /// and <c>sessions.sqlite</c> are equivalent.
    /// </param>
    /// <param name="target">Optional explicit <c>--target</c> home override.</param>
    /// <param name="dataPathOverride">
    /// Optional explicit data directory. When null the ambient <c>BOTNEXUS_DATA_DIR</c> is used.
    /// </param>
    public static StoreResolution Resolve(string storeName, string? target, string? dataPathOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        var bareName = StripExtension(storeName.Trim());
        var home = CliPaths.ResolveTarget(target);
        var explicitTarget = !string.IsNullOrWhiteSpace(target);
        var directories = BuildSearchDirectories(home, dataPathOverride, explicitTarget);
        var candidateNames = Extensions.Select(ext => bareName + ext).ToArray();

        foreach (var directory in directories)
        {
            foreach (var fileName in candidateNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return new StoreResolution(candidate, Found: true, directories, candidateNames);
            }
        }

        // Nothing on disk. Fall back to the path a writer WOULD create so callers that create or
        // simply report the path stay sensible: data dir when configured, else home, and the
        // writer's own ".sqlite" extension - never the historical ".db" guess.
        var fallback = Path.Combine(directories[0], bareName + Extensions[0]);
        return new StoreResolution(fallback, Found: false, directories, candidateNames);
    }

    /// <summary>
    /// Convenience overload for callers that only need a path to probe with
    /// <see cref="File.Exists(string)"/> and have no message to render.
    /// </summary>
    public static string ResolvePath(string storeName, string? target)
        => Resolve(storeName, target).Path;

    /// <summary>
    /// Directories probed, in preference order.
    /// <para>
    /// When the operator passed an explicit <c>--target</c> that directory wins outright: they
    /// named a specific home and an ambient <c>BOTNEXUS_DATA_DIR</c> belonging to some other
    /// deployment must not silently outrank it.
    /// </para>
    /// <para>
    /// Otherwise the configured data directory comes first, because that is where every writer
    /// actually puts runtime stores, then the config home, then <c>home/data</c> which older
    /// layouts used. Duplicates are collapsed so the not-found message does not repeat a directory
    /// when data dir == home (the common single-machine case).
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> BuildSearchDirectories(
        string home,
        string? dataPathOverride,
        bool explicitTarget)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;
            if (seen.Add(Path.TrimEndingDirectorySeparator(directory)))
                ordered.Add(directory);
        }

        if (explicitTarget)
            Add(home);

        Add(BotNexusHome.ResolveDataPath(dataPathOverride));
        Add(home);
        Add(Path.Combine(home, "data"));

        return ordered;
    }

    /// <summary>
    /// Builds the not-found message for a store the caller already resolved to
    /// <paramref name="attemptedPath"/>. Names every candidate filename and every directory that
    /// was (or would be) searched - including the attempted path's own directory - so a
    /// wrong-directory deployment is diagnosable from the message alone (issue #3126, AC4).
    /// </summary>
    public static string BuildNotFoundMessage(string storeName, string attemptedPath, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        var bareName = StripExtension(storeName.Trim());
        var resolution = Resolve(bareName, target);

        var directories = new List<string>(resolution.SearchedDirectories);
        var attemptedDirectory = Path.GetDirectoryName(attemptedPath);
        if (!string.IsNullOrWhiteSpace(attemptedDirectory)
            && !directories.Contains(attemptedDirectory, StringComparer.OrdinalIgnoreCase))
        {
            directories.Add(attemptedDirectory);
        }

        var names = string.Join(", ", resolution.CandidateFileNames);
        var dirs = string.Join(", ", directories);
        return $"{bareName} store not found. Looked for [{names}] in: {dirs}";
    }

    private static string StripExtension(string name)
    {
        foreach (var ext in Extensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        }

        return name;
    }
}
