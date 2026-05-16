using System.Text;
using System.IO.Abstractions;
using BotNexus.CodingAgent;
using BotNexus.Tools.Utils;

namespace BotNexus.CodingAgent.Utils;

public static class ContextFileDiscovery
{
    private const int ContextBudgetBytes = 16 * 1024;
    private const string TruncatedMarker = "[truncated]";
    private const string DefaultConfigDirectoryName = ".botnexus-agent";

    public static async Task<IReadOnlyList<PromptContextFile>> DiscoverAsync(
        IFileSystem fileSystem,
        string workingDirectory,
        CancellationToken ct,
        string? configDirectoryName = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));
        }

        var cwd = Path.GetFullPath(workingDirectory);
        if (!fileSystem.Directory.Exists(cwd))
        {
            return [];
        }

        var discovered = new List<PromptContextFile>();
        var remainingBudget = ContextBudgetBytes;
        var seenFileKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedConfigDirectoryName = string.IsNullOrWhiteSpace(configDirectoryName)
            ? DefaultConfigDirectoryName
            : configDirectoryName!;

        foreach (var directory in EnumerateDiscoveryDirectories(fileSystem, cwd))
        {
            foreach (var (kind, filePath) in GetContextCandidates(directory, resolvedConfigDirectoryName))
            {
                ct.ThrowIfCancellationRequested();
                if (remainingBudget <= 0 || seenFileKinds.Contains(kind) || !fileSystem.File.Exists(filePath))
                {
                    continue;
                }

                var content = await fileSystem.File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var includedContent = FitContentToBudget(content, remainingBudget);
                if (string.IsNullOrEmpty(includedContent))
                {
                    return discovered;
                }

                discovered.Add(new PromptContextFile(PathUtils.GetRelativePath(filePath, cwd), includedContent));
                remainingBudget -= Encoding.UTF8.GetByteCount(includedContent);
                seenFileKinds.Add(kind);
            }
        }

        return discovered;
    }

    private static IEnumerable<string> EnumerateDiscoveryDirectories(IFileSystem fileSystem, string cwd)
    {
        var gitRoot = FindGitRoot(fileSystem, cwd);
        var current = cwd;

        while (true)
        {
            yield return current;

            if (string.Equals(current, gitRoot ?? cwd, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent;
        }
    }

    private static string? FindGitRoot(IFileSystem fileSystem, string cwd)
    {
        var current = cwd;
        while (true)
        {
            if (fileSystem.Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }
    }

    private static IEnumerable<(string Kind, string Path)> GetContextCandidates(
        string directory,
        string configDirectoryName)
    {
        yield return ("copilot-instructions", Path.Combine(directory, ".github", "copilot-instructions.md"));
        yield return ("instructions", Path.Combine(directory, "INSTRUCTIONS.md"));
        yield return ("agents", Path.Combine(directory, "AGENTS.md"));
        yield return ("config-agents", Path.Combine(directory, configDirectoryName, "AGENTS.md"));
    }

    private static string FitContentToBudget(string content, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var fullSize = Encoding.UTF8.GetByteCount(content);
        if (fullSize <= maxBytes)
        {
            return content;
        }

        var markerBytes = Encoding.UTF8.GetByteCount(TruncatedMarker);
        if (maxBytes <= markerBytes)
        {
            return TruncatedMarker[..Math.Min(TruncatedMarker.Length, maxBytes)];
        }

        var allowedBytes = maxBytes - markerBytes;
        var builder = new StringBuilder(content.Length);
        var usedBytes = 0;
        foreach (var ch in content)
        {
            var charBytes = Encoding.UTF8.GetByteCount(ch.ToString());
            if (usedBytes + charBytes > allowedBytes)
            {
                break;
            }

            builder.Append(ch);
            usedBytes += charBytes;
        }

        builder.Append(TruncatedMarker);
        return builder.ToString();
    }
}
