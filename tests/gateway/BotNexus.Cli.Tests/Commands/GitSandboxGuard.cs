using System.Diagnostics;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Sandbox guard shared by every repo-creating test harness in this assembly (#2632).
/// <para>
/// A harness interrupted mid-flight previously left an <c>add -A</c> / <c>commit</c> pointed at a
/// live worktree and produced a tree-deleting "initial" commit on the developer's branch. Two
/// mechanisms combined: the pre-commit hook exports <c>GIT_DIR</c> / <c>GIT_WORK_TREE</c> for the
/// caller's repository, and a harness that located its repo only via
/// <see cref="ProcessStartInfo.WorkingDirectory"/> was silently retargeted by them.
/// </para>
/// <para>
/// This lives in ONE place on purpose. The guard was briefly duplicated per-harness, and a mutation
/// that neutered one copy stayed green because only the other copy was covered - a duplicated
/// invariant is an invariant that rots in whichever copy nobody tests.
/// </para>
/// </summary>
internal static class GitSandboxGuard
{
    /// <summary>
    /// Environment variables git exports to hook subprocesses that can silently redirect a git
    /// invocation at the caller's real repository, or author a commit under a real identity.
    /// </summary>
    private static readonly string[] AmbientRepoRedirectVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_PREFIX",
        "GIT_AUTHOR_NAME",
        "GIT_AUTHOR_EMAIL",
        "GIT_COMMITTER_NAME",
        "GIT_COMMITTER_EMAIL"
    ];

    /// <summary>
    /// Sandbox identity, deliberately generic and NON-conflicting with any real identity. It must
    /// never resemble the #1602 pollution signature (<c>test@example.com</c> / <c>Test</c>), so a
    /// leaked write cannot be mistaken for - or graft onto - the host repo.
    /// </summary>
    internal const string SentinelName = "botnexus-test";

    /// <inheritdoc cref="SentinelName"/>
    internal const string SentinelEmail = "botnexus-test@invalid.local";

    /// <summary>
    /// Throws unless <paramref name="repoRoot"/> resolves under the temp sandbox root.
    /// Callers invoke this before every git call that can stage or author a commit.
    /// </summary>
    /// <returns>The normalised absolute sandbox path.</returns>
    internal static string AssertSandboxRepoPath(string repoRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException(
                $"Sandbox guard: refusing git write against '{full}' because it is not under the temp sandbox root '{root}'.");

        return full;
    }

    /// <summary>
    /// Builds a git <see cref="ProcessStartInfo"/> that can only ever act on the sandbox: the path
    /// is guarded, <c>-C</c> is the sole repo locator, and every ambient redirect variable the hook
    /// environment may have exported is stripped.
    /// </summary>
    internal static ProcessStartInfo CreateSandboxedGit(string repoRoot, string arguments)
    {
        AssertSandboxRepoPath(repoRoot);

        var psi = new ProcessStartInfo("git", $"-C \"{repoRoot}\" {arguments}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var leaked in AmbientRepoRedirectVariables)
            psi.Environment.Remove(leaked);

        return psi;
    }
}
