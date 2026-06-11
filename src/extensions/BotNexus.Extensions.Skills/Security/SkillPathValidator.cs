using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// Validates that resolved file paths remain within the expected skill directory boundary
/// after symlink resolution. Prevents symlink-based path traversal attacks where a symlink
/// inside the skill directory points to a location outside the trusted root.
/// </summary>
internal static class SkillPathValidator
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Validates that the given target path resolves to a location within the allowed root
    /// after following any symlinks in the path. Returns the resolved path on success.
    /// </summary>
    /// <param name="targetPath">The absolute path to validate.</param>
    /// <param name="allowedRoot">The root directory that must contain the resolved path.</param>
    /// <param name="fileSystem">File system abstraction for testability.</param>
    /// <param name="resolvedPath">The fully resolved path (symlinks followed).</param>
    /// <param name="error">Error message if validation fails; null on success.</param>
    /// <returns>True if the path is safe; false if it escapes the allowed root.</returns>
    public static bool TryValidate(
        string targetPath,
        string allowedRoot,
        IFileSystem fileSystem,
        out string resolvedPath,
        out string? error)
    {
        resolvedPath = targetPath;
        error = null;

        // Normalize the allowed root (ensure trailing separator for prefix comparison)
        var normalizedRoot = NormalizePath(allowedRoot, fileSystem);

        // Resolve symlinks by walking each path component
        var resolved = ResolvePath(targetPath, fileSystem);
        var normalizedResolved = NormalizePath(resolved, fileSystem);

        // Check containment: resolved path must be under (or equal to) the allowed root
        if (!IsUnderOrEqual(normalizedResolved, normalizedRoot, fileSystem))
        {
            error = $"Path escapes skill directory boundary after symlink resolution. " +
                    $"Resolved '{resolved}' is not within '{allowedRoot}'.";
            return false;
        }

        resolvedPath = resolved;
        return true;
    }

    /// <summary>
    /// Checks each component of the path for symlinks (reparse points) by walking
    /// from root to leaf. If any component is a symlink, resolves it and continues
    /// from the resolved location.
    /// </summary>
    private static string ResolvePath(string fullPath, IFileSystem fileSystem)
    {
        var normalized = fileSystem.Path.GetFullPath(fullPath);
        var root = fileSystem.Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
            return normalized;

        var segments = normalized[root.Length..]
            .Split(
                [fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        var current = root.TrimEnd(fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar);

        foreach (var segment in segments)
        {
            current = fileSystem.Path.Combine(current, segment);

            if (fileSystem.Directory.Exists(current))
            {
                var dirInfo = fileSystem.DirectoryInfo.New(current);
                if (dirInfo.LinkTarget is not null)
                {
                    var target = ResolveLink(current, dirInfo.LinkTarget, fileSystem);
                    current = target;
                }
            }
            else if (fileSystem.File.Exists(current))
            {
                var fileInfo = fileSystem.FileInfo.New(current);
                if (fileInfo.LinkTarget is not null)
                {
                    var target = ResolveLink(current, fileInfo.LinkTarget, fileSystem);
                    current = target;
                }
            }
            // If path component doesn't exist yet (write to new file), stop resolution
            // and just use the normalized form from here forward
        }

        return fileSystem.Path.GetFullPath(current);
    }

    private static string ResolveLink(string linkPath, string linkTarget, IFileSystem fileSystem)
    {
        if (fileSystem.Path.IsPathRooted(linkTarget))
            return fileSystem.Path.GetFullPath(linkTarget);

        var parent = fileSystem.Path.GetDirectoryName(linkPath);
        return string.IsNullOrEmpty(parent)
            ? fileSystem.Path.GetFullPath(linkTarget)
            : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(parent, linkTarget));
    }

    private static string NormalizePath(string path, IFileSystem fileSystem)
    {
        var full = fileSystem.Path.GetFullPath(path);
        return full.TrimEnd(fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnderOrEqual(string candidate, string root, IFileSystem fileSystem)
    {
        if (string.Equals(candidate, root, PathComparison))
            return true;

        var sep = fileSystem.Path.DirectorySeparatorChar.ToString();
        return candidate.StartsWith(root + sep, PathComparison);
    }
}
