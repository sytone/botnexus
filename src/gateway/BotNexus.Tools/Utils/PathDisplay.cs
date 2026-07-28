using System.IO.Abstractions;

namespace BotNexus.Tools.Utils;

/// <summary>
/// Re-anchors tool <em>display</em> paths onto the prefix the caller actually named.
/// </summary>
/// <remarks>
/// <para>
/// Path validation deliberately resolves symlinks and reparse points before checking containment — that
/// resolution is the security check and must never be weakened. But the resolved path is the wrong thing to
/// hand back to an agent: workspaces and worktrees use links routinely, so a caller who asked about
/// <c>link/</c> would receive results under <c>real/</c> and then try to read, edit, or diff a path outside
/// the tree it reasoned about.
/// </para>
/// <para>
/// This helper separates the two concerns: validate against the resolved real path, display relative to the
/// requested path. It is the single re-anchoring seam for every path-returning tool — introduced by
/// issue #2384 / PR #2402 for grep and generalised for issue #2404. Do not copy this logic into individual
/// tools.
/// </para>
/// </remarks>
public static class PathDisplay
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Computes the absolute form of the caller's requested path <em>without</em> following symlinks, so
    /// result paths can be reported under the prefix the caller named.
    /// </summary>
    /// <param name="rawPath">The path exactly as supplied by the caller.</param>
    /// <param name="baseDirectory">Optional base used to root a relative <paramref name="rawPath"/>.</param>
    /// <param name="fileSystem">Optional file system abstraction for testability.</param>
    /// <returns>
    /// The absolute, unresolved requested path, or <c>null</c> when the raw path uses a form this
    /// re-anchoring cannot faithfully mirror (home-relative <c>~</c> paths, a relative path with no base
    /// directory, or a path that does not exist as written). Callers report the resolved path unchanged in
    /// that case.
    /// </returns>
    public static string? ResolveRequestedRoot(string? rawPath, string? baseDirectory, IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.TrimStart().StartsWith('~'))
        {
            return null;
        }

        var rooted = Path.IsPathRooted(rawPath);
        if (!rooted && string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(rooted ? rawPath : Path.Combine(baseDirectory!, rawPath));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        var fs = fileSystem ?? new FileSystem();
        return fs.Directory.Exists(candidate) || fs.File.Exists(candidate)
            ? candidate
            : null;
    }

    /// <summary>
    /// Maps a path discovered under the symlink-resolved <paramref name="resolvedRoot"/> back onto the prefix
    /// the caller asked about. Only the reported path changes — reading, access validation, and ignore checks
    /// all continue to use the resolved path.
    /// </summary>
    /// <param name="path">The absolute path produced from the resolved root.</param>
    /// <param name="resolvedRoot">The symlink-resolved form of the caller's requested path.</param>
    /// <param name="requestedRoot">The unresolved requested path from <see cref="ResolveRequestedRoot"/>.</param>
    /// <returns>
    /// The path anchored under <paramref name="requestedRoot"/>, or <paramref name="path"/> unchanged when
    /// resolution did not alter the prefix, when no requested root was captured, or when the path sits
    /// outside the resolved root.
    /// </returns>
    public static string Reanchor(string path, string resolvedRoot, string? requestedRoot)
    {
        if (requestedRoot is null || string.Equals(resolvedRoot, requestedRoot, PathComparison))
        {
            return path;
        }

        var relative = Path.GetRelativePath(resolvedRoot, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return path;
        }

        // A single-file request resolves the root to the file itself; anchor onto the requested file path.
        return relative == "."
            ? requestedRoot
            : Path.Combine(requestedRoot, relative);
    }
}
