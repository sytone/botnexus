namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Rewrites host-absolute skill paths to sandbox-relative paths in skill content.
/// When an agent runs inside a Docker sandbox, skill script references in the system
/// prompt must point to sandbox-local paths rather than host paths that don't exist
/// inside the container.
/// </summary>
public static class SandboxSkillPathRewriter
{
    /// <summary>
    /// Default sandbox skills mount point (Linux path inside the container).
    /// </summary>
    public const string DefaultSandboxSkillsPath = "/workspace/skills";

    /// <summary>
    /// Rewrites all occurrences of the host skills directory path in the given content
    /// to the sandbox-relative path.
    /// </summary>
    /// <param name="content">Skill content (markdown) that may contain host paths.</param>
    /// <param name="hostSkillsDir">
    /// The host-absolute skills directory path (e.g., "%USERPROFILE%\.botnexus\skills").
    /// Both forward-slash and backslash variants are replaced.
    /// </param>
    /// <param name="sandboxSkillsPath">
    /// The sandbox-relative path to use (e.g., "/workspace/skills").
    /// </param>
    /// <returns>Content with host paths replaced by sandbox paths.</returns>
    public static string RewritePaths(
        string content,
        string hostSkillsDir,
        string sandboxSkillsPath = DefaultSandboxSkillsPath)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(hostSkillsDir))
            return content;

        // Normalize: replace both backslash and forward-slash variants
        var backslashPath = hostSkillsDir.Replace('/', '\\');
        var forwardSlashPath = hostSkillsDir.Replace('\\', '/');

        var result = content;

        // Replace backslash variant first (Windows paths)
        if (result.Contains(backslashPath, StringComparison.OrdinalIgnoreCase))
            result = result.Replace(backslashPath, sandboxSkillsPath, StringComparison.OrdinalIgnoreCase);

        // Replace forward-slash variant
        if (result.Contains(forwardSlashPath, StringComparison.OrdinalIgnoreCase))
            result = result.Replace(forwardSlashPath, sandboxSkillsPath, StringComparison.OrdinalIgnoreCase);

        // Normalize remaining backslashes to forward slashes in paths that follow
        // the sandbox prefix (Linux containers use forward slashes)
        result = NormalizePathSeparatorsAfterPrefix(result, sandboxSkillsPath);

        return result;
    }

    /// <summary>
    /// After path prefix replacement, normalizes any remaining backslashes that
    /// immediately follow the sandbox prefix into forward slashes. This handles
    /// cases where Windows sub-paths (\teams\scripts\Tool.ps1) remain after
    /// the prefix is rewritten to a Linux path.
    /// </summary>
    private static string NormalizePathSeparatorsAfterPrefix(string content, string prefix)
    {
        var sb = new System.Text.StringBuilder(content.Length);
        int searchFrom = 0;

        while (true)
        {
            var idx = content.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(content, searchFrom, content.Length - searchFrom);
                break;
            }

            // Append everything up to and including the prefix
            sb.Append(content, searchFrom, idx + prefix.Length - searchFrom);
            searchFrom = idx + prefix.Length;

            // Normalize backslashes in the path segment that follows the prefix
            while (searchFrom < content.Length)
            {
                var ch = content[searchFrom];
                if (ch == '\\')
                {
                    sb.Append('/');
                    searchFrom++;
                }
                else if (ch == '/' || char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_')
                {
                    sb.Append(ch);
                    searchFrom++;
                }
                else
                {
                    // Non-path character — stop normalizing
                    break;
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites skill paths in the content by trying multiple common host skill directory patterns.
    /// This handles the case where skills reference the global skills dir, agent skills dir,
    /// or workspace skills dir.
    /// </summary>
    /// <param name="content">Skill content that may contain host paths.</param>
    /// <param name="hostSkillsDirs">
    /// Collection of host skill directory paths to search for and replace.
    /// </param>
    /// <param name="sandboxSkillsPath">Sandbox-relative base path.</param>
    /// <returns>Content with all recognized host paths replaced.</returns>
    public static string RewriteMultiplePaths(
        string content,
        IEnumerable<string> hostSkillsDirs,
        string sandboxSkillsPath = DefaultSandboxSkillsPath)
    {
        var result = content;
        foreach (var hostDir in hostSkillsDirs)
        {
            if (!string.IsNullOrWhiteSpace(hostDir))
                result = RewritePaths(result, hostDir, sandboxSkillsPath);
        }
        return result;
    }
}
