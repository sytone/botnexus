using System.IO.Abstractions;

namespace BotNexus.Gateway.Api.Controllers;

internal static class WorkspacePathSecurity
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string NormalizePath(IFileSystem fileSystem, string path)
    {
        var fullPath = fileSystem.Path.GetFullPath(path);
        var root = fileSystem.Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, PathComparison))
            return fullPath;

        return fullPath.TrimEnd(fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar);
    }

    public static string NormalizeRelativePath(IFileSystem fileSystem, string path)
    {
        return path.Trim()
            .Replace('/', fileSystem.Path.DirectorySeparatorChar)
            .Replace('\\', fileSystem.Path.DirectorySeparatorChar);
    }

    public static string ResolveFinalTargetPath(IFileSystem fileSystem, string fullPath)
    {
        var currentPath = NormalizePath(fileSystem, fullPath);
        var root = fileSystem.Path.GetPathRoot(currentPath);
        if (string.IsNullOrWhiteSpace(root))
            return currentPath;

        var rootPath = NormalizePath(fileSystem, root);
        var segments = currentPath[rootPath.Length..]
            .Split(
                [fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        var path = rootPath;
        for (var index = 0; index < segments.Length; index++)
        {
            path = fileSystem.Path.Combine(path, segments[index]);
            if (fileSystem.Directory.Exists(path))
            {
                var directoryInfo = fileSystem.DirectoryInfo.New(path);
                if (directoryInfo.LinkTarget is null)
                    continue;

                var resolved = ResolveLinkTargetPath(fileSystem, path, directoryInfo.LinkTarget, () => directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName);
                return NormalizePath(fileSystem, AppendRemainingSegments(fileSystem, resolved, segments, index + 1));
            }

            if (fileSystem.File.Exists(path))
            {
                var fileInfo = fileSystem.FileInfo.New(path);
                if (fileInfo.LinkTarget is null)
                    continue;

                var resolved = ResolveLinkTargetPath(fileSystem, path, fileInfo.LinkTarget, () => fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName);
                return NormalizePath(fileSystem, AppendRemainingSegments(fileSystem, resolved, segments, index + 1));
            }

            break;
        }

        return currentPath;
    }

    public static bool IsUnderPath(IFileSystem fileSystem, string candidatePath, string configuredPath)
    {
        var candidate = NormalizePath(fileSystem, candidatePath);
        var configured = NormalizePath(fileSystem, configuredPath);
        if (string.Equals(candidate, configured, PathComparison))
            return true;

        return candidate.StartsWith(configured + fileSystem.Path.DirectorySeparatorChar, PathComparison)
               || candidate.StartsWith(configured + fileSystem.Path.AltDirectorySeparatorChar, PathComparison);
    }

    public static string ToWorkspaceRelativePath(IFileSystem fileSystem, string workspaceRoot, string absolutePath)
    {
        var relativePath = fileSystem.Path.GetRelativePath(workspaceRoot, absolutePath);
        return relativePath.Replace(fileSystem.Path.DirectorySeparatorChar, '/');
    }

    private static string AppendRemainingSegments(
        IFileSystem fileSystem,
        string basePath,
        IReadOnlyList<string> segments,
        int startIndex)
    {
        var path = basePath;
        for (var index = startIndex; index < segments.Count; index++)
            path = fileSystem.Path.Combine(path, segments[index]);

        return path;
    }

    private static string ResolveLinkTargetPath(
        IFileSystem fileSystem,
        string path,
        string? linkTarget,
        Func<string?> resolveFinalTarget)
    {
        string? resolved;
        try
        {
            resolved = resolveFinalTarget();
        }
        catch (IOException)
        {
            resolved = null;
        }
        catch (UnauthorizedAccessException)
        {
            resolved = null;
        }
        catch (NotSupportedException)
        {
            resolved = null;
        }

        resolved ??= linkTarget ?? path;
        if (!fileSystem.Path.IsPathRooted(resolved))
        {
            var parent = fileSystem.Path.GetDirectoryName(path);
            resolved = string.IsNullOrEmpty(parent)
                ? fileSystem.Path.GetFullPath(resolved)
                : fileSystem.Path.GetFullPath(fileSystem.Path.Combine(parent, resolved));
        }

        return resolved;
    }
}
