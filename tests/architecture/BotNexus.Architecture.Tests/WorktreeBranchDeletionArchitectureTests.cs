using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2104: no tracked file may teach or
/// execute an <b>unconditionally chained</b> worktree removal followed by a
/// branch deletion.
/// </summary>
/// <remarks>
/// <para>
/// On Windows a <c>git worktree remove</c> routinely fails with
/// <c>Permission denied</c> because an editor, test runner, shell or antivirus
/// still holds a handle under the worktree. When the caller chains
/// <c>git worktree remove ... ; git branch -D ...</c>, the branch is deleted
/// even though the removal failed - the directory is orphaned and the commits
/// on that branch are stranded. This happened twice for real
/// (<c>fix/2248-view-readonly-source</c>, <c>fix/2293-portal-recursion</c>).
/// </para>
/// <para>
/// The hardened helper <c>scripts/repo/Remove-Worktree.ps1</c> already does the
/// right thing (bounded retry, structured <c>locked</c> outcome, branch deletion
/// only after a fully successful removal). The regression vector was
/// <i>documentation</i>: skill files and agent instructions taught the chained
/// form, and agents copied it verbatim. This fence stops that drift.
/// </para>
/// <para>Accepted forms:</para>
/// <list type="bullet">
///   <item><description><c>pwsh -File scripts/repo/Remove-Worktree.ps1 -WorktreePath ... -DeleteBranch</c></description></item>
///   <item><description>an explicit exit-code check between the two commands
///   (<c>if ($LASTEXITCODE -eq 0) { git branch -d ... }</c>, <c>|| exit 1</c>, <c>$?</c>, ...)</description></item>
/// </list>
/// <para>
/// The fence carries its own anti-vacuity assertions: it proves it scanned a
/// non-trivial number of tracked files, and it pins the detector regex with
/// positive and negative samples so it cannot pass by silently matching
/// nothing.
/// </para>
/// </remarks>
public sealed class WorktreeBranchDeletionArchitectureTests : ArchitectureTest
{
    // The fence file itself documents the forbidden pattern. Allowlist by
    // basename so it does not trip on its own documentation.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorktreeBranchDeletionArchitectureTests.cs",
    };

    // Minimum number of tracked text files the sweep must inspect. The repo has
    // thousands; if the sweep ever drops below this the enumeration broke and a
    // green result would be vacuous.
    private const int MinimumScannedFiles = 200;

    private static readonly Regex WorktreeRemove = new(
        @"worktree\s+remove", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BranchDelete = new(
        @"branch\s+-[dD]\b", RegexOptions.Compiled);

    // Tokens that prove the author checked the removal's exit code before
    // touching the branch.
    private static readonly Regex ExitCodeGuard = new(
        @"LASTEXITCODE|\$\?|exitCode|ExitCode|\|\||\bif\b|Remove-Worktree\.ps1",
        RegexOptions.Compiled);

    // How many following lines count as "adjacent" inside a fenced block.
    private const int AdjacencyWindow = 3;

    // Extensions whose comment syntax is '#' to end of line (shell/PowerShell/YAML).
    private static readonly HashSet<string> HashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".yml", ".yaml", ".toml",
    };

    // Extensions whose comment syntax is C-style ('//' and '/* */').
    private static readonly HashSet<string> SlashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".mjs", ".cjs", ".razor", ".css", ".scss", ".jsonc",
    };

    // '//' that starts a comment - not the '//' of a scheme like 'https://'.
    private static readonly Regex LineCommentSlash = new(
        @"(?<!:)//", RegexOptions.Compiled);

    [Fact]
    public void NoTrackedFile_ChainsBranchDeletionAfterWorktreeRemoval()
    {
        var scanned = 0;
        var offenders = ScanTrackedFiles(
            (path, content) =>
            {
                var hits = FindOffendingLines(content, path);
                return hits.Count == 0
                    ? null
                    : $"{path}: {string.Join("; ", hits)}";
            },
            ref scanned);

        scanned.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Anti-vacuity: the sweep only inspected {scanned} tracked text files. " +
            "A fence that scans nothing is trivially green - fix the enumeration.");

        offenders.ShouldBeEmpty(
            "Tracked files chain a branch deletion after a worktree removal without " +
            "checking the removal's exit code (issue #2104). On Windows the removal " +
            "frequently fails with 'Permission denied'; deleting the branch anyway " +
            "orphans the worktree directory and strands the commits.\n" +
            "Use scripts/repo/Remove-Worktree.ps1 (bounded retry + structured 'locked' " +
            "outcome + branch deletion only after a fully successful removal), or write " +
            "the two-step form with an explicit exit-code check.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    // ---------------------------------------------------------------------
    // Anti-vacuity pins on the detector itself. If the regexes stop matching,
    // these fail loudly instead of letting the sweep pass by matching nothing.
    // ---------------------------------------------------------------------

    [Theory]
    // same line, ';' separator
    [InlineData("git worktree remove ../wt-1 ; git branch -D fix/1-slug")]
    // same line, '&&' separator - still a hand-rolled chain, route via the helper
    [InlineData("git worktree remove ../wt-1 && git branch -d fix/1-slug")]
    // documentation bullet form, '+' separator
    [InlineData("- `git worktree remove {path}` + `git branch -d {branch}`")]
    // adjacent lines in a fenced block, no exit-code check
    [InlineData("git worktree remove ../wt-1\ngit branch -d fix/1-slug")]
    // adjacent lines with an intervening prune step
    [InlineData("git worktree remove ../wt-195\ngit worktree prune\ngit branch -d fix/195-x")]
    public void Detector_FlagsUnconditionalChain(string sample)
    {
        FindOffendingLines(sample).ShouldNotBeEmpty(
            "Positive pin failed - the detector no longer recognises the unsafe " +
            "chained form, so the repo-wide sweep would pass vacuously. Sample:\n" + sample);
    }

    [Theory]
    // the hardened helper does the ordering internally
    [InlineData("pwsh -NoProfile -File scripts/repo/Remove-Worktree.ps1 -WorktreePath ../wt-1 -DeleteBranch")]
    // explicit exit-code check
    [InlineData("git worktree remove ../wt-1\nif ($LASTEXITCODE -eq 0) { git branch -d fix/1-slug }")]
    // bash short-circuit guard
    [InlineData("git worktree remove ../wt-1 || exit 1\ngit branch -d fix/1-slug")]
    // removal alone is fine
    [InlineData("git worktree remove ../wt-1")]
    // branch deletion alone is fine
    [InlineData("git branch -d fix/1-slug")]
    // far apart - outside the adjacency window
    [InlineData("git worktree remove ../wt-1\na\nb\nc\nd\ngit branch -d fix/1-slug")]
    public void Detector_AllowsGuardedForms(string sample)
    {
        FindOffendingLines(sample).ShouldBeEmpty(
            "Negative pin failed - the detector flags a form that is actually safe, " +
            "which would make the fence unusable. Sample:\n" + sample);
    }

    // ---------------------------------------------------------------------
    // Issue #3817: a file may DESCRIBE the anti-pattern in a comment without
    // EXECUTING it. Both directions are pinned so the fix cannot gut the fence.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("scripts/Remove-DevWorktree.ps1", "# `git worktree remove ...` ; `git branch -D ...`, because the chained form runs the")]
    [InlineData("scripts/cleanup.sh", "# git worktree remove ../wt-1 ; git branch -D fix/1-slug")]
    [InlineData("src/Foo.cs", "// git worktree remove ../wt-1 ; git branch -D fix/1-slug")]
    [InlineData("src/Foo.cs", "/* git worktree remove ../wt-1\n   git branch -D fix/1-slug */")]
    [InlineData("scripts/Doc.ps1", "<#\ngit worktree remove ../wt-1\ngit branch -D fix/1-slug\n#>")]
    [InlineData("scripts/Trailing.ps1", "Write-Host 'x'  # git worktree remove ../wt-1 ; git branch -D fix/1")]
    public void Detector_IgnoresCommentedExample(string path, string sample)
    {
        FindOffendingLines(sample, path).ShouldBeEmpty(
            "Issue #3817: a comment that merely describes the chained form is not an " +
            "offence - only executable statements are. Sample:\n" + sample);
    }

    [Theory]
    // executable chain in the same file types whose comments are now stripped
    [InlineData("scripts/Remove-DevWorktree.ps1", "git worktree remove ../wt-1 ; git branch -D fix/1-slug")]
    [InlineData("scripts/cleanup.sh", "git worktree remove ../wt-1 && git branch -d fix/1-slug")]
    // code before the comment on the same line is still scanned
    [InlineData("scripts/Mixed.ps1", "git worktree remove ../wt-1 ; git branch -D fix/1  # cleanup")]
    // code after a closed block comment is still scanned
    [InlineData("scripts/Block.ps1", "<# doc #> git worktree remove ../wt-1 ; git branch -D fix/1")]
    // markdown prose is NOT stripped - teaching the chained form is the #2104 vector
    [InlineData("docs/guide.md", "Run `git worktree remove ../wt-1` then `git branch -d fix/1-slug`")]
    public void Detector_StillFlagsExecutableChain_WhenCommentsAreStripped(string path, string sample)
    {
        FindOffendingLines(sample, path).ShouldNotBeEmpty(
            "Issue #3817 regression guard: comment stripping must not blind the fence " +
            "to a real #2104 chained statement. Sample:\n" + sample);
    }

    /// <summary>
    /// Returns human-readable descriptions of every unconditionally chained
    /// "remove worktree then delete branch" occurrence in <paramref name="content"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="relativePath"/> selects the comment syntax to blank before
    /// matching (issue #3817): a file may <i>describe</i> the anti-pattern in a
    /// comment without <i>executing</i> it. Markdown is deliberately NOT stripped -
    /// prose that teaches the chained form is the original #2104 regression vector.
    /// Passing <c>null</c> disables stripping and matches raw text.
    /// </remarks>
    internal static List<string> FindOffendingLines(string content, string? relativePath = null)
    {
        var hits = new List<string>();
        var lines = StripComments(content.Replace("\r\n", "\n").Split('\n'), relativePath);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!WorktreeRemove.IsMatch(line))
            {
                continue;
            }

            var removeMatch = WorktreeRemove.Match(line);
            var tail = line[(removeMatch.Index + removeMatch.Length)..];

            // Case 1: both commands on the same line.
            if (BranchDelete.IsMatch(tail) && !ExitCodeGuard.IsMatch(line))
            {
                hits.Add($"line {i + 1}: chained on one line -> {Truncate(line.Trim())}");
                continue;
            }

            // Case 2: branch deletion on a nearby following line with no
            // exit-code check anywhere between the two commands.
            if (ExitCodeGuard.IsMatch(line))
            {
                continue;
            }

            for (var j = i + 1; j <= i + AdjacencyWindow && j < lines.Length; j++)
            {
                var next = lines[j];
                if (ExitCodeGuard.IsMatch(next))
                {
                    break;
                }
                if (BranchDelete.IsMatch(next))
                {
                    hits.Add(
                        $"line {i + 1}->{j + 1}: adjacent unguarded chain -> " +
                        $"{Truncate(line.Trim())} / {Truncate(next.Trim())}");
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Blanks comment text (preserving line count and numbering) according to the
    /// file's language, so a documented example of the anti-pattern is not an offence.
    /// </summary>
    internal static string[] StripComments(string[] lines, string? relativePath)
    {
        if (relativePath is null)
        {
            return lines;
        }

        var extension = Path.GetExtension(relativePath);
        var hash = HashCommentExtensions.Contains(extension);
        var slash = SlashCommentExtensions.Contains(extension);
        if (!hash && !slash)
        {
            return lines;
        }

        var result = new string[lines.Length];
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (slash)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0)
                    {
                        result[i] = string.Empty;
                        continue;
                    }
                    line = new string(' ', end + 2) + line[(end + 2)..];
                    inBlockComment = false;
                }

                var start = line.IndexOf("/*", StringComparison.Ordinal);
                if (start >= 0)
                {
                    var end = line.IndexOf("*/", start + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        inBlockComment = true;
                        line = line[..start];
                    }
                    else
                    {
                        line = line[..start] + line[(end + 2)..];
                    }
                }

                var slashMatch = LineCommentSlash.Match(line);
                if (slashMatch.Success)
                {
                    line = line[..slashMatch.Index];
                }
            }

            if (hash)
            {
                // PowerShell block comments <# ... #>.
                if (inBlockComment)
                {
                    var end = line.IndexOf("#>", StringComparison.Ordinal);
                    if (end < 0)
                    {
                        result[i] = string.Empty;
                        continue;
                    }
                    line = line[(end + 2)..];
                    inBlockComment = false;
                }

                var start = line.IndexOf("<#", StringComparison.Ordinal);
                if (start >= 0)
                {
                    var end = line.IndexOf("#>", start + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        inBlockComment = true;
                        line = line[..start];
                    }
                    else
                    {
                        line = line[..start] + line[(end + 2)..];
                    }
                }

                var hashIndex = line.IndexOf('#');
                if (hashIndex >= 0)
                {
                    line = line[..hashIndex];
                }
            }

            result[i] = line;
        }

        return result;
    }

    private List<string> ScanTrackedFiles(Func<string, string, string?> inspect, ref int scanned)
    {
        var repoRoot = Repository.Root;
        var offenders = new List<string>();
        var count = 0;

        foreach (var relative in EnumerateTrackedFiles(repoRoot))
        {
            if (AllowedFiles.Contains(Path.GetFileName(relative)))
            {
                continue;
            }
            if (!IsTextFile(relative))
            {
                continue;
            }

            var absolute = Path.Combine(repoRoot, relative);
            if (!File.Exists(absolute))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(absolute);
            }
            catch (IOException)
            {
                continue;
            }

            count++;
            var result = inspect(relative.Replace('\\', '/'), content);
            if (result is not null)
            {
                offenders.Add(result);
            }
        }

        scanned = count;
        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    private static IEnumerable<string> EnumerateTrackedFiles(string repoRoot)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, "git ls-files failed: " + process.StandardError.ReadToEnd());
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp",
        ".pdf", ".zip", ".gz", ".tar", ".7z", ".dll", ".exe", ".pdb",
        ".mitm", ".pptx", ".docx", ".xlsx", ".woff", ".woff2", ".ttf",
        ".eot", ".otf", ".mp3", ".mp4", ".wav", ".mov",
    };

    private static bool IsTextFile(string relativePath)
        => !BinaryExtensions.Contains(Path.GetExtension(relativePath));


    private static string Truncate(string value)
        => value.Length <= 100 ? value : value[..100] + "...";
}
