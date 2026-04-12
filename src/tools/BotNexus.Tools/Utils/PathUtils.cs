using System.Diagnostics;
using System.IO.Abstractions;

namespace BotNexus.Tools.Utils;

/// <summary>
/// Provides path normalization, containment validation, and repository ignore checks for coding-agent tools.
/// </summary>
/// <remarks>
/// <para>
/// All public helpers enforce a single invariant: file system operations must stay inside the configured
/// working directory root. This prevents accidental or malicious path traversal beyond the repository boundary.
/// </para>
/// <para>
/// These methods throw <see cref="InvalidOperationException"/> when a caller provides unsafe input.
/// Tool implementations intentionally surface those exceptions to the agent loop as structured tool errors.
/// </para>
/// </remarks>
public static class PathUtils
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Resolves a user-provided path against a working directory while enforcing root containment.
    /// </summary>
    /// <param name="relative">The user path, absolute or relative.</param>
    /// <param name="workingDirectory">The repository root used as the containment boundary.</param>
    /// <returns>A normalized absolute path guaranteed to remain under <paramref name="workingDirectory"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when inputs are empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when path traversal escapes the root boundary.</exception>
    public static string ResolvePath(string relative, string workingDirectory, IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(relative));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));
        }

        var root = Path.GetFullPath(workingDirectory);
        var sanitizedInput = SanitizePath(relative);

        var resolved = Path.IsPathRooted(sanitizedInput)
            ? Path.GetFullPath(sanitizedInput)
            : Path.GetFullPath(Path.Combine(root, sanitizedInput));

        if (!IsUnderRoot(resolved, root))
        {
            throw new InvalidOperationException(
                $"Path '{relative}' resolves outside working directory '{root}'.");
        }

        var resolvedFinal = ResolveFinalTargetPath(resolved, fileSystem);
        if (!IsUnderRoot(resolvedFinal, root))
        {
            throw new UnauthorizedAccessException(
                $"Symlink target escapes working directory: {relative}");
        }

        return resolved;
    }

    /// <summary>
    /// Normalizes separators and validates that parent-directory traversal does not escape the current root.
    /// </summary>
    /// <param name="path">The raw path value from tool input.</param>
    /// <returns>A sanitized path with canonical separators and collapsed <c>.</c>/<c>..</c> segments.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when traversal attempts to escape root scope.</exception>
    public static string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        var normalizedSeparators = path.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedSeparators))
        {
            var root = Path.GetPathRoot(normalizedSeparators)
                       ?? throw new InvalidOperationException("Unable to resolve path root.");

            var suffix = normalizedSeparators[root.Length..];
            var normalizedSuffix = NormalizeSegments(suffix);
            var rooted = Path.Combine(root, normalizedSuffix);
            return Path.GetFullPath(rooted);
        }

        return NormalizeSegments(normalizedSeparators);
    }

    /// <summary>
    /// Returns a display-friendly relative path from <paramref name="basePath"/> to <paramref name="fullPath"/>.
    /// </summary>
    /// <param name="fullPath">The full path to convert.</param>
    /// <param name="basePath">The base path to compute relativity from.</param>
    /// <returns>A relative path suitable for user-facing output.</returns>
    public static string GetRelativePath(string fullPath, string basePath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Full path cannot be empty.", nameof(fullPath));
        }

        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new ArgumentException("Base path cannot be empty.", nameof(basePath));
        }

        var normalizedFullPath = Path.GetFullPath(fullPath);
        var normalizedBasePath = Path.GetFullPath(basePath);
        return Path.GetRelativePath(normalizedBasePath, normalizedFullPath);
    }

    /// <summary>
    /// Executes get git ignored paths.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <returns>The get git ignored paths result.</returns>
    public static HashSet<string> GetGitIgnoredPaths(IEnumerable<string> paths, string workingDirectory, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var resolved = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolvePath(path, workingDirectory, fs))
            .Distinct(PathComparer)
            .ToList();

        var ignored = new HashSet<string>(PathComparer);
        if (resolved.Count == 0)
        {
            return ignored;
        }

        var relativeToAbsolute = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in resolved)
        {
            var relative = GetRelativePath(path, workingDirectory)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!relativeToAbsolute.ContainsKey(relative))
            {
                relativeToAbsolute[relative] = path;
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" check-ignore --stdin",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return ignored;
        }

        try
        {
            foreach (var relative in relativeToAbsolute.Keys)
            {
                process.StandardInput.WriteLine(relative);
            }

            process.StandardInput.Close();
        }
        catch
        {
            return ignored;
        }

        string output;
        try
        {
            output = process.StandardOutput.ReadToEnd();
        }
        catch
        {
            output = string.Empty;
        }

        if (!process.WaitForExit(5000) || process.ExitCode > 1)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return ignored;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var relative = line.Trim().Replace('\\', '/');
            if (relativeToAbsolute.TryGetValue(relative, out var absolute))
            {
                ignored.Add(absolute);
            }
            else
            {
                var resolvedIgnored = ResolvePath(relative, workingDirectory, fs);
                ignored.Add(resolvedIgnored);
            }
        }

        return ignored;
    }

    private static string NormalizeSegments(string path)
    {
        var stack = new Stack<string>();
        var segments = path.Split(
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
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException($"Path traversal is not allowed: '{path}'.");
                }

                stack.Pop();
                continue;
            }

            stack.Push(segment);
        }

        return string.Join(Path.DirectorySeparatorChar, stack.Reverse());
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);

        return normalizedPath.StartsWith(normalizedRoot, PathComparison)
               || PathComparer.Equals(
                   normalizedPath.TrimEnd(Path.DirectorySeparatorChar),
                   normalizedRoot.TrimEnd(Path.DirectorySeparatorChar));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string ResolveFinalTargetPath(string fullPath, IFileSystem? fileSystem)
    {
        var fs = fileSystem ?? new FileSystem();
        var current = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(current);
        if (string.IsNullOrWhiteSpace(root))
        {
            return current;
        }

        var rootPath = Path.GetFullPath(root);
        var segments = current[rootPath.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var currentPath = rootPath;
        for (var i = 0; i < segments.Length; i++)
        {
            currentPath = Path.Combine(currentPath, segments[i]);

            if (fs.Directory.Exists(currentPath))
            {
                var directoryInfo = fs.DirectoryInfo.New(currentPath);
                if (directoryInfo.LinkTarget is not null)
                {
                    var resolvedTarget = directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? currentPath;
                    currentPath = AppendRemainingSegments(resolvedTarget, segments, i + 1);
                    return Path.GetFullPath(currentPath);
                }
            }
            else if (fs.File.Exists(currentPath))
            {
                var fileInfo = fs.FileInfo.New(currentPath);
                if (fileInfo.LinkTarget is not null)
                {
                    var resolvedTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? currentPath;
                    currentPath = AppendRemainingSegments(resolvedTarget, segments, i + 1);
                    return Path.GetFullPath(currentPath);
                }
            }
            else
            {
                break;
            }
        }

        return current;
    }

    private static string AppendRemainingSegments(string basePath, IReadOnlyList<string> segments, int startIndex)
    {
        var path = basePath;
        for (var i = startIndex; i < segments.Count; i++)
        {
            path = Path.Combine(path, segments[i]);
        }

        return path;
    }
}
