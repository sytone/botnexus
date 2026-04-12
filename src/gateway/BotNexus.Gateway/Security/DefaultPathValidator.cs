using System.IO.Enumeration;
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

        return allowed ? resolvedPath : null;
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
        return ExpandUserHome(rawPath.Trim())
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
        var expanded = ExpandUserHome(rawPath.Trim())
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? NormalizePath(expanded)
            : NormalizePath(Path.Combine(_workspacePath, expanded));
    }

    private static string ExpandUserHome(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1)
        {
            return home;
        }

        var first = path[1];
        if (first == Path.DirectorySeparatorChar || first == Path.AltDirectorySeparatorChar)
        {
            return Path.Combine(home, path[2..]);
        }

        return path;
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
