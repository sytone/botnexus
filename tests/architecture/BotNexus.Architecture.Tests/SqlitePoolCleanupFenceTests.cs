using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Repo-wide fence: no test anywhere under <c>tests/**</c> may call the process-global
/// <c>SqliteConnection.ClearAllPools()</c> (#3324, #3392).
/// </summary>
/// <remarks>
/// <para>
/// <c>ClearAllPools()</c> disposes the pooled native <c>SQLitePCL.sqlite3</c> handles for
/// <b>every</b> connection string in the test host. Test collections run in parallel, so one test's
/// teardown detonates a sibling that is mid-query, which then fails with
/// <c>ObjectDisposedException</c> from <c>sqlite3_prepare_v2</c> while naming a test that did
/// nothing wrong. Because it is a race, it does not reproduce in isolation - which is precisely why
/// #3324 fixed one project and the pattern quietly survived in nine others until #3392 caught it
/// reddening the CI of a pull request that touched no database code at all.
/// </para>
/// <para>
/// <see cref="BotNexus.Testing.SqlitePoolCleanup"/> is the supported replacement. It keeps the
/// file-lock-release guarantee - which is the only reason teardown wanted the global API - with no
/// cross-test blast radius.
/// </para>
/// <para>
/// The assertion is source-level by design: the defect is a <i>call</i>, and a runtime assertion
/// could only observe its damage by winning a race. This fence lives in the architecture test
/// project because the rule spans every test project; the per-project copy it replaces could only
/// ever police the assembly that hosted it.
/// </para>
/// </remarks>
public sealed class SqlitePoolCleanupFenceTests
{
    /// <summary>
    /// Files exempt from the ban, with the reason each is allowed to name the API.
    /// </summary>
    /// <remarks>
    /// Only this fence and the helper that documents the ban may contain the token in code. Neither
    /// exemption is a suppression of a real call site: this file matches on its own regex literal,
    /// and the helper names the API only in prose.
    /// </remarks>
    private static readonly string[] ExemptFileNames =
    [
        "SqlitePoolCleanupFenceTests.cs",
        "SqlitePoolCleanup.cs"
    ];

    /// <summary>
    /// A floor on how many test sources the scan must see before its result means anything.
    /// </summary>
    /// <remarks>
    /// Without this the fence passes trivially if the path resolution breaks, the repo layout moves,
    /// or the enumeration silently returns nothing - the classic vacuous green. The repo had well
    /// over three thousand test source files when this was written, so a floor of 500 cannot be
    /// tripped by ordinary churn but is guaranteed to catch an enumeration that found nothing or
    /// only a single project.
    /// </remarks>
    private const int MinimumFilesScanned = 500;

    [Fact]
    public void NoTestProject_CallsTheProcessGlobalClearAllPools()
    {
        var testsRoot = FindTestsRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrIntermediate(file))
                continue;

            scanned++;

            if (ExemptFileNames.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                continue;

            var code = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(code, @"ClearAllPools\s*\("))
                offenders.Add(Path.GetRelativePath(testsRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        // Non-vacuity: assert the scan actually inspected the corpus BEFORE asserting it found
        // nothing. "No offenders" is only meaningful evidence if there was something to offend.
        scanned.ShouldBeGreaterThan(
            MinimumFilesScanned,
            $"The fence scanned only {scanned} files under '{testsRoot}', which is too few to be a "
            + "real sweep of the test corpus. A green result here would be vacuous - fix the path "
            + "resolution rather than lowering this floor.");

        offenders.ShouldBeEmpty(
            "SqliteConnection.ClearAllPools() is process-global and disposes sibling tests' live "
            + "SQLite handles under parallel collections (#3324, #3392). Use "
            + "SqlitePoolCleanup.ClearPoolFor(dbPath), ClearPoolForConnectionString(cs), or "
            + "ClearPoolsUnder(directory) instead. Offending files: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Proves the detection regex actually matches the banned call shape.
    /// </summary>
    /// <remarks>
    /// The fence above is a search that expects to find nothing, so a regex that matched nothing at
    /// all would look identical to a clean repository. This pins the detector itself: if someone
    /// weakens the pattern to silence a red, this test goes red instead of the ban silently
    /// evaporating. It also pins the comment-stripping, which exists so the fence does not redden on
    /// the prose that explains why the API is banned.
    /// </remarks>
    [Theory]
    [InlineData("SqliteConnection.ClearAllPools();", true)]
    [InlineData("Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();", true)]
    [InlineData("SqliteConnection.ClearAllPools ();", true)]
    [InlineData("=> SqliteConnection.ClearAllPools();", true)]
    [InlineData("// NOT ClearAllPools(): it is process-global.", false)]
    [InlineData("/// <c>SqliteConnection.ClearAllPools()</c> is banned.", false)]
    [InlineData("SqlitePoolCleanup.ClearPoolFor(dbPath);", false)]
    [InlineData("SqliteConnection.ClearPool(connection);", false)]
    public void Detector_MatchesTheBannedCall_AndNothingElse(string line, bool expectedOffence)
    {
        var detected = Regex.IsMatch(StripComments(line), @"ClearAllPools\s*\(");

        detected.ShouldBe(
            expectedOffence,
            $"The fence's detector must {(expectedOffence ? "flag" : "ignore")}: {line}");
    }

    /// <summary>
    /// Removes block and line comments so the scan sees CODE only.
    /// </summary>
    /// <remarks>
    /// Without this the fence reddens on its own remediation: the doc comments explaining why
    /// <c>ClearAllPools</c> is banned, and the <c>// NOT ClearAllPools()</c> markers left at each
    /// converted call site, both contain the banned token. Deleting those comments to satisfy the
    /// fence would destroy the explanation that stops the next author reintroducing the bug, so the
    /// fence is narrowed instead. Verified empirically on run <c>20260818031500-433a5706</c>, which
    /// failed for exactly that reason.
    /// </remarks>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>Excludes build intermediates, which contain generated copies of real sources.</summary>
    private static bool IsGeneratedOrIntermediate(string file)
    {
        var sep = Path.DirectorySeparatorChar;
        return file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
            || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (identified by the solution file), then
    /// resolves the tests directory.
    /// </summary>
    /// <remarks>
    /// Anchoring on the solution file rather than a fixed number of <c>..</c> segments keeps this
    /// working under any output-path layout, including the remote container gate.
    /// </remarks>
    private static string FindTestsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("Could not locate the repository root (BotNexus.slnx) from the test binary.");

        var testsRoot = Path.Combine(directory!.FullName, "tests");
        Directory.Exists(testsRoot).ShouldBeTrue($"Expected the test sources at '{testsRoot}'.");
        return testsRoot;
    }
}
