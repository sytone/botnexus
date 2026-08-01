using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2651: no test source may point a
/// <b>mutating</b> git command at a path derived from the live repository.
/// </summary>
/// <remarks>
/// <para>
/// A test fixture that runs <c>git init</c>, <c>git add</c>, <c>git commit</c>,
/// <c>git config &lt;key&gt; &lt;value&gt;</c>, <c>git checkout</c>, <c>git reset</c> or
/// <c>git clean</c> against the developer's or agent's live worktree is silently
/// destructive: it can move <c>HEAD</c>, stage or discard real work, and overwrite the
/// repository's git identity. The damage is only visible after the fact, and a leaked
/// commit authored by a generic-looking fixture identity is indistinguishable from a
/// legitimate one.
/// </para>
/// <para>
/// The safe form is unambiguous and easy to state: a mutating fixture repository must be
/// created by the test itself under <see cref="Path.GetTempPath"/>. It must never derive
/// its working directory from a repo-root locator - <c>RepoLocator</c>,
/// <c>Directory.GetCurrentDirectory()</c>, <c>Environment.CurrentDirectory</c>,
/// <c>AppContext.BaseDirectory</c> walked up to <c>BotNexus.slnx</c>, <c>git rev-parse
/// --show-toplevel</c>, or a local <c>FindRepoRoot</c>-style helper.
/// </para>
/// <para>
/// <b>Read-only git is explicitly permitted</b> against the live repository. Architecture
/// fences in this very project enumerate tracked files with <c>git ls-files</c>, and the
/// #2651 isolation pin observes the ambient worktree with <c>git rev-parse HEAD</c> /
/// <c>git status --porcelain</c> / <c>git config --get</c>. Those cannot damage anything.
/// The fence therefore flags a file only when it combines a repo-root-derived path with a
/// mutating verb.
/// </para>
/// <para>
/// The fence carries its own anti-vacuity assertions: it proves it inspected a plausible
/// minimum number of test sources, that it actually found git-invoking test files at all,
/// and it pins the detector with positive and negative samples so it cannot pass by
/// silently matching nothing.
/// </para>
/// </remarks>
public sealed class TestGitInvocationScopeArchitectureTests
{
    // The fence file itself documents both the forbidden and the permitted forms, so it
    // would match its own detector. Allowlist by basename, exactly as the sibling fences do.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TestGitInvocationScopeArchitectureTests.cs",
    };

    // Minimum number of tracked test sources the sweep must inspect. The repo has many
    // hundreds; if the sweep ever drops below this the enumeration broke and a green
    // result would be vacuous.
    private const int MinimumScannedTestFiles = 100;

    // Minimum number of test sources that invoke git at all. If this hits zero the
    // git-detection regex stopped matching and the fence would pass by inspecting nothing.
    private const int MinimumGitInvokingTestFiles = 2;

    /// <summary>
    /// Any use of the <c>git</c> executable from a test source.
    /// </summary>
    private static readonly Regex GitInvocation = new(
        @"ProcessStartInfo\s*\(\s*""git""|StartInfo\s*=\s*new\s+ProcessStartInfo\s*\(\s*""git""|""git""\s*,",
        RegexOptions.Compiled);

    /// <summary>
    /// Git subcommands that can modify a repository. <c>config</c> is included only in its
    /// assigning form (<c>config key value</c>); <c>config --get key</c> is read-only.
    /// </summary>
    // The character classes deliberately exclude newlines. A ""[^""]*"" class spans line breaks and
    // will happily swallow an entire XML doc-comment block that merely MENTIONS a git command,
    // producing false positives on the sibling fences' own documentation.
    private static readonly Regex MutatingGitVerb = new(
        @"""(?:[^""\r\n]*\s)?(?:init|add|commit|checkout|reset|clean|push|merge|rebase|stash|branch\s+-[dD]|worktree\s+add)\b[^""\r\n]*""",
        RegexOptions.Compiled);

    private static readonly Regex AssigningGitConfig = new(
        @"""(?:[^""\r\n]*\s)?config\s+(?!--get\b|--list\b)[^""\r\n]*\s+[^""\r\n\s]+""",
        RegexOptions.Compiled);

    /// <summary>
    /// Tokens that derive a path from the LIVE repository rather than creating a fresh one.
    /// </summary>
    private static readonly Regex RepoDerivedPath = new(
        @"RepoLocator|Directory\.GetCurrentDirectory\s*\(|Environment\.CurrentDirectory|" +
        @"rev-parse\s+--show-toplevel|FindRepoRoot|GetRepoRoot|LocateRepoRoot|RepositoryRoot|SolutionRoot",
        RegexOptions.Compiled);

    [Fact]
    public void NoTestSource_PointsAMutatingGitCommandAtTheLiveRepository()
    {
        var scanned = 0;
        var gitInvoking = 0;
        var offenders = new List<string>();

        foreach (var (relative, content) in EnumerateTrackedTestSources())
        {
            scanned++;

            if (!GitInvocation.IsMatch(content))
            {
                continue;
            }

            gitInvoking++;

            var reason = Inspect(content);
            if (reason is not null)
            {
                offenders.Add($"{relative}: {reason}");
            }
        }

        scanned.ShouldBeGreaterThan(
            MinimumScannedTestFiles,
            $"Anti-vacuity: the sweep only inspected {scanned} tracked test sources. " +
            "A fence that scans nothing is trivially green - fix the enumeration.");

        gitInvoking.ShouldBeGreaterThanOrEqualTo(
            MinimumGitInvokingTestFiles,
            $"Anti-vacuity: the sweep found only {gitInvoking} test sources that invoke git. " +
            "The repo definitely has more than that, so the git-invocation detector has stopped " +
            "matching and the fence would pass without inspecting anything real.");

        offenders.Sort(StringComparer.Ordinal);
        offenders.ShouldBeEmpty(
            "Test sources run a MUTATING git command against a path derived from the live " +
            "repository (issue #2651). A fixture that runs git init/add/commit/config against " +
            "the real worktree silently moves HEAD, stages or discards real work, and can " +
            "overwrite the repository's git identity.\n" +
            "Create the fixture repository yourself under Path.GetTempPath() and delete it in a " +
            "finally block. Read-only git (rev-parse, status, ls-files, config --get) against the " +
            "live repository remains fine.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    // ---------------------------------------------------------------------
    // Anti-vacuity pins on the detector itself.
    // ---------------------------------------------------------------------

    [Theory]
    // repo-root locator handed straight to a mutating verb
    [InlineData("var root = RepoLocator.FindRoot();\nRunGit(root, \"init\");")]
    // current directory as the git working directory, then a commit
    [InlineData("var root = Directory.GetCurrentDirectory();\nRunGit(root, \"commit -m x\");")]
    // Environment.CurrentDirectory with an add
    [InlineData("var root = Environment.CurrentDirectory;\nRunGit(root, \"add -A\");")]
    // a local repo-root helper feeding an identity-assigning config
    [InlineData("var root = FindRepoRoot();\nRunGit(root, \"config user.email a@b.c\");")]
    // shelling out to git to find the toplevel, then resetting it
    [InlineData("var root = Run(\"rev-parse --show-toplevel\");\nRunGit(root, \"reset --hard\");")]
    public void Detector_FlagsMutatingGitAgainstRepoDerivedPath(string sample)
    {
        Inspect(sample).ShouldNotBeNull(
            "Positive pin failed - the detector no longer recognises a mutating git command " +
            "aimed at the live repository, so the sweep would pass vacuously. Sample:\n" + sample);
    }

    [Theory]
    // the correct form: self-created temp repository
    [InlineData("var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(\"N\"));\nRunGit(root, \"init\");")]
    // read-only observation of the live repository is fine
    [InlineData("var root = FindRepoRoot();\nReadGit(root, \"rev-parse HEAD\");")]
    [InlineData("var root = Directory.GetCurrentDirectory();\nReadGit(root, \"status --porcelain\");")]
    [InlineData("var root = FindRepoRoot();\nReadGit(root, \"config --get user.email\");")]
    // architecture fences enumerating tracked files
    [InlineData("var root = FindRepoRoot();\nRun(root, \"ls-files\");")]
    // mutating git with no repo-derived path at all
    [InlineData("RunGit(tempRoot, \"commit -m initial\");")]
    public void Detector_AllowsSafeForms(string sample)
    {
        Inspect(sample).ShouldBeNull(
            "Negative pin failed - the detector flags a form that is actually safe, which " +
            "would make the fence unusable. Sample:\n" + sample);
    }

    /// <summary>
    /// Returns a human-readable reason when <paramref name="content"/> combines a
    /// repo-root-derived path with a mutating git verb, or <c>null</c> when it is safe.
    /// </summary>
    internal static string? Inspect(string content)
    {
        content = StripNonExecutableLines(content);

        var pathMatch = RepoDerivedPath.Match(content);
        if (!pathMatch.Success)
        {
            return null;
        }

        var verbMatch = MutatingGitVerb.Match(content);
        if (!verbMatch.Success)
        {
            verbMatch = AssigningGitConfig.Match(content);
        }

        if (!verbMatch.Success)
        {
            return null;
        }

        return $"derives a git path from '{pathMatch.Value}' and runs the mutating command " +
               $"{Truncate(verbMatch.Value)}";
    }

    /// <summary>
    /// Removes lines that cannot possibly BE a git invocation: comments (including XML doc
    /// comments) and <c>[InlineData(...)]</c> attribute rows. Sibling architecture fences pin
    /// their own detectors with literal command strings such as
    /// <c>[InlineData("git worktree remove ... ; git branch -D ...")]</c>, and they locate the
    /// repository root only to run a strictly read-only <c>git ls-files</c> sweep. Scanning those
    /// rows as if they were call sites reports the fence's own test data as an offender. Only
    /// executable statements can actually launch a process, so only those are inspected.
    /// </summary>
    private static string StripNonExecutableLines(string content)
    {
        var kept = content
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("//", StringComparison.Ordinal)
                    && !trimmed.StartsWith("*", StringComparison.Ordinal)
                    && !trimmed.StartsWith("[InlineData", StringComparison.Ordinal);
            });

        return string.Join('\n', kept);
    }

    private static IEnumerable<(string Relative, string Content)> EnumerateTrackedTestSources()
    {
        var repoRoot = FindSweepRepoRoot();

        foreach (var relative in EnumerateTrackedFiles(repoRoot))
        {
            var normalised = relative.Replace('\\', '/');

            if (!normalised.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!normalised.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (AllowedFiles.Contains(Path.GetFileName(normalised)))
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

            yield return (normalised, content);
        }
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

    private static string FindSweepRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current.FullName;
    }

    private static string Truncate(string value)
        => value.Length <= 100 ? value : value[..100] + "...";
}
