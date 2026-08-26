using System.IO.Enumeration;
using BotNexus.Domain.Paths;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Security;

public sealed class DefaultPathValidator : IPathValidator
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string _workspacePath;
    private readonly bool _workspaceOnly;
    private readonly IReadOnlyList<string> _allowedReadPaths;
    private readonly IReadOnlyList<string> _allowedWritePaths;
    private readonly IReadOnlyList<string> _deniedPaths;

    public DefaultPathValidator(FileAccessPolicy? policy, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("Workspace path cannot be empty.", nameof(workspacePath));
        }

        _workspacePath = NormalizePath(workspacePath);
        _workspaceOnly = policy is null || IsPolicyEmpty(policy);
        _allowedReadPaths = ResolvePolicyPaths(policy?.AllowedReadPaths);
        _allowedWritePaths = ResolvePolicyPaths(policy?.AllowedWritePaths);
        _deniedPaths = ResolvePolicyPaths(policy?.DeniedPaths);
    }

    public bool CanRead(string absolutePath)
    {
        var path = NormalizePath(absolutePath);
        return CanAccess(path, _allowedReadPaths);
    }

    public bool CanWrite(string absolutePath)
    {
        var path = NormalizePath(absolutePath);
        return CanAccess(path, _allowedWritePaths);
    }

    public string? ValidateAndResolve(string rawPath, FileAccessMode mode)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var resolvedPath = ResolvePath(rawPath);
        var allowed = mode == FileAccessMode.Read
            ? CanRead(resolvedPath)
            : CanWrite(resolvedPath);

        if (!allowed)
        {
            return null;
        }

        // Phase two: `Path.GetFullPath` collapses `..` lexically without any link
        // resolution, so `<symlink>/../<target>` can appear to stay inside the root
        // while the OS resolves the link first and lands outside it. Re-walk the raw
        // (non-collapsed) path segment by segment, following links exactly as the OS
        // would, and re-check containment on the real final target.
        var linkResolvedPath = ResolvePathFollowingLinks(rawPath);
        if (linkResolvedPath is null)
        {
            return null;
        }

        if (!string.Equals(linkResolvedPath, resolvedPath, PathComparison))
        {
            var finalAllowed = mode == FileAccessMode.Read
                ? CanRead(linkResolvedPath)
                : CanWrite(linkResolvedPath);

            if (!finalAllowed)
            {
                return null;
            }
        }

        return resolvedPath;
    }

    /// <summary>
    /// Re-anchors workspace-relative entries in <paramref name="paths"/> onto
    /// <paramref name="workspacePath"/> so a policy list keeps its meaning when it is copied onto
    /// another agent's policy. Rooted entries and glob patterns come back verbatim: neither binds to
    /// a workspace during resolution, so the destination validator resolves them to the same place
    /// the source one did. Only a relative entry would silently re-bind to the new owner's workspace
    /// and stop protecting the directory the operator named.
    /// </summary>
    /// <param name="paths">The policy list to re-anchor.</param>
    /// <param name="workspacePath">The workspace of the agent the list came from.</param>
    /// <returns>The re-anchored list, or the input unchanged when there is no workspace to anchor to.</returns>
    internal static IReadOnlyList<string> RebaseRelativePaths(
        IReadOnlyList<string>? paths,
        string workspacePath)
    {
        if (paths is not { Count: > 0 })
        {
            return [];
        }

        // Empty is the only workspace with nowhere to anchor to - the constructor rejects it outright.
        // Everything else goes through the constructor's own NormalizePath, so a relative workspace
        // lands where the source validator put it instead of somewhere this helper invented.
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return paths;
        }

        var root = NormalizePath(workspacePath);
        var rebased = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || IsGlobPattern(path))
            {
                rebased.Add(path);
                continue;
            }

            var expanded = HomePathExpander.Expand(path.Trim())
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            rebased.Add(Path.IsPathRooted(expanded)
                ? path
                : NormalizePath(Path.Combine(root, expanded)));
        }

        return rebased;
    }

    /// <summary>
    /// Resolves <paramref name="rawPath"/> using operating-system semantics: each
    /// segment is appended in order and, when it is a symbolic link or junction, the
    /// link is followed before the next segment (including <c>..</c>) is applied.
    /// Returns <see langword="null"/> when the path cannot be resolved.
    /// </summary>
    private string? ResolvePathFollowingLinks(string rawPath)
    {
        try
        {
            var expanded = HomePathExpander.Expand(rawPath.Trim())
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            string current;
            string remainder;
            if (Path.IsPathRooted(expanded))
            {
                var root = Path.GetPathRoot(expanded);
                if (string.IsNullOrEmpty(root))
                {
                    return NormalizePath(expanded);
                }

                current = Path.GetFullPath(root);
                remainder = expanded[root.Length..];
            }
            else
            {
                current = _workspacePath;
                remainder = expanded;
            }

            var segments = remainder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    // `..` is applied to the already link-resolved parent, matching the OS.
                    current = Path.GetDirectoryName(current) ?? current;
                    continue;
                }

                current = Path.Combine(current, segment);
                current = ResolveLinkTarget(current);
            }

            return NormalizePath(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            // Fail closed: if the real target cannot be determined, do not approve.
            return null;
        }
    }

    private static string ResolveLinkTarget(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var directoryInfo = new DirectoryInfo(path);
                if (directoryInfo.LinkTarget is not null)
                {
                    return directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
                }
            }
            else if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.LinkTarget is not null)
                {
                    return fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
                }
            }
        }
        catch (IOException)
        {
            // Broken or cyclic link: keep the lexical path; containment checks still apply.
        }

        return path;
    }

    private bool CanAccess(string absolutePath, IReadOnlyList<string> allowedPaths)
    {
        if (IsDenied(absolutePath))
        {
            return false;
        }

        if (_workspaceOnly)
        {
            return IsUnderPath(absolutePath, _workspacePath);
        }

        if (allowedPaths.Any(pattern => PathMatchesPattern(absolutePath, pattern)))
        {
            return true;
        }

        return IsUnderPath(absolutePath, _workspacePath);
    }

    private bool IsDenied(string absolutePath)
        => _deniedPaths.Any(pattern => PathMatchesPattern(absolutePath, pattern));

    private static bool PathMatchesPattern(string absolutePath, string pattern)
    {
        if (IsGlobPattern(pattern))
        {
            // Normalize to forward slashes so backslash isn't treated as escape
            var normalizedPath = absolutePath.Replace('\\', '/');
            var normalizedPattern = pattern.Replace('\\', '/');
            return FileSystemName.MatchesSimpleExpression(
                normalizedPattern, normalizedPath, ignoreCase: OperatingSystem.IsWindows());
        }

        return IsUnderPath(absolutePath, pattern);
    }

    private static bool IsGlobPattern(string path)
        => path.Contains('*') || path.Contains('?');

    private static string ResolveGlobPath(string rawPath)
    {
        return HomePathExpander.Expand(rawPath.Trim())
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private IReadOnlyList<string> ResolvePolicyPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var resolved = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            resolved.Add(IsGlobPattern(path) ? ResolveGlobPath(path) : ResolvePath(path));
        }

        return resolved;
    }

    private string ResolvePath(string rawPath)
    {
        var expanded = HomePathExpander.Expand(rawPath.Trim())
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? NormalizePath(expanded)
            : NormalizePath(Path.Combine(_workspacePath, expanded));
    }


    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, PathComparison))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPolicyEmpty(FileAccessPolicy policy)
        => policy.AllowedReadPaths.Count == 0
           && policy.AllowedWritePaths.Count == 0
           && policy.DeniedPaths.Count == 0;

    private static bool IsUnderPath(string candidatePath, string configuredPath)
    {
        if (string.Equals(candidatePath, configuredPath, PathComparison))
        {
            return true;
        }

        return candidatePath.StartsWith(configuredPath + Path.DirectorySeparatorChar, PathComparison)
               || candidatePath.StartsWith(configuredPath + Path.AltDirectorySeparatorChar, PathComparison);
    }
}
