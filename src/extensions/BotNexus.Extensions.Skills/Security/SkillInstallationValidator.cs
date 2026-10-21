using System.IO.Abstractions;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// Installations and their ancestors must be ordinary paths. Child links are permitted only
/// when their canonical targets remain contained. Checks are best-effort filesystem observations,
/// not an atomic read/validation transaction.
/// </summary>
internal static class SkillInstallationValidator
{
    private const int MaxEntries = 100_000;

    internal static IReadOnlyList<string> CollectFiles(string root, IFileSystem fileSystem)
    {
        // Never canonicalize a linked installation into an authorized root.
        EnsureOrdinaryPath(root, fileSystem);
        var allowedRoot = SkillPath.CreateRoot(root, fileSystem);
        var files = new List<string>();
        var pending = new Stack<(string Path, bool Exit)>();
        var ancestors = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push((root, false));
        var entries = 0;

        while (pending.TryPop(out var frame))
        {
            if (frame.Exit)
            {
                ancestors.Remove(frame.Path);
                continue;
            }

            var directory = ResolveContainedPath(frame.Path, allowedRoot, fileSystem);
            if (!ancestors.Add(directory))
                throw new InvalidDataException("Skill installation contains a directory link cycle.");
            pending.Push((directory, true));

            // Enumerate only a validated canonical directory, never an unchecked link target.
            foreach (var canonicalEntry in fileSystem.Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaxEntries)
                    throw new InvalidDataException($"Skill installation exceeds the {MaxEntries} entry validation limit.");

                var entry = fileSystem.Path.Combine(frame.Path, fileSystem.Path.GetFileName(canonicalEntry));
                var resolved = ResolveContainedPath(entry, allowedRoot, fileSystem);
                var attributes = fileSystem.File.GetAttributes(resolved);
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push((entry, false));
                else
                    // Preserve the public alias so discovery and view_file use the same path.
                    files.Add(entry);
            }
        }

        return files;
    }

    internal static string ResolveContainedPath(string path, string root, IFileSystem fileSystem)
    {
        EnsureOrdinaryPath(root, fileSystem);
        return ResolveContainedPath(path, SkillPath.CreateRoot(root, fileSystem), fileSystem);
    }

    private static string ResolveContainedPath(string path, SkillPath root, IFileSystem fileSystem)
    {
        if (!SkillPathValidator.TryValidate(path, root, fileSystem, out var resolved, out var error))
            throw new InvalidDataException(error);
        // Unknown reparse types and unresolved links must fail closed too.
        EnsureOrdinaryPath(resolved.Value, fileSystem);
        return resolved.Value;
    }

    internal static void EnsureOrdinaryPath(string path, IFileSystem fileSystem)
    {
        var full = fileSystem.Path.GetFullPath(path);
        var root = fileSystem.Path.GetPathRoot(full)
            ?? throw new InvalidDataException($"Skill path '{path}' has no filesystem root.");
        var current = root;
        EnsureOrdinaryEntry(current, fileSystem);
        foreach (var segment in full[root.Length..].Split(
                     [fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = fileSystem.Path.Combine(current, segment);
            EnsureOrdinaryEntry(current, fileSystem);
        }
    }

    private static FileAttributes EnsureOrdinaryEntry(string path, IFileSystem fileSystem)
    {
        var attributes = fileSystem.File.GetAttributes(path);
        var linkTarget = (attributes & FileAttributes.Directory) != 0
            ? fileSystem.DirectoryInfo.New(path).LinkTarget
            : fileSystem.FileInfo.New(path).LinkTarget;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || linkTarget is not null)
            throw new InvalidDataException(
                $"Skill installation contains a link or reparse point at '{path}'. Install the skill as an ordinary directory with ordinary files, not a linked installation.");
        return attributes;
    }
}
