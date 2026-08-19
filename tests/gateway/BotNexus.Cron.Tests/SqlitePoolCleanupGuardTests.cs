using System.Text.RegularExpressions;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Guard against the #3324 flake family reappearing: no test in this assembly may call the
/// process-global <c>SqliteConnection.ClearAllPools()</c>.
/// </summary>
/// <remarks>
/// <para>
/// This assembly runs with <c>parallelizeTestCollections: true</c>. <c>ClearAllPools()</c> disposes
/// the pooled native <c>SQLitePCL.sqlite3</c> handles for <b>every</b> connection string in the
/// process, so one test's teardown detonates a sibling test that is mid-query, which then fails with
/// <c>ObjectDisposedException</c> from <c>sqlite3_prepare_v2</c> naming a test that did nothing
/// wrong. That is not reproducible in isolation, which is why the defect survived so long.
/// </para>
/// <para>
/// The narrowly-scoped
/// <see cref="TestInfrastructure.SqlitePoolCleanup.ClearPoolFor(string)"/> is the supported
/// replacement: it keeps the file-lock-release guarantee with no cross-test blast radius. A
/// source-level assertion is used deliberately - the defect is a call that a runtime assertion
/// could only observe by losing a race.
/// </para>
/// </remarks>
public sealed class SqlitePoolCleanupGuardTests
{
    [Fact]
    public void NoTestInThisAssembly_CallsTheProcessGlobalClearAllPools()
    {
        var testRoot = FindThisTestProjectRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip this guard's own source, which necessarily names the banned API.
            if (Path.GetFileName(file) == "SqlitePoolCleanupGuardTests.cs")
                continue;

            var code = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(code, @"ClearAllPools\s*\("))
                offenders.Add(Path.GetRelativePath(testRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        offenders.ShouldBeEmpty(
            "SqliteConnection.ClearAllPools() is process-global and disposes sibling tests' live SQLite "
            + "handles under parallel collections (#3324). Use SqlitePoolCleanup.ClearPoolFor(dbPath) instead. "
            + "Offending files: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Removes block and line comments so the scan sees CODE only.
    /// </summary>
    /// <remarks>
    /// Without this, the guard reddens on its own remediation: the doc comments explaining WHY
    /// <c>ClearAllPools</c> is banned, and the <c>// NOT ClearAllPools()</c> markers left at each
    /// converted call site, both contain the banned token. Verified empirically - run
    /// <c>20260818031500-433a5706</c> failed for exactly that reason. Deleting those comments to
    /// satisfy the guard would have destroyed the explanation that stops the next author
    /// reintroducing the bug, so the guard is narrowed instead.
    /// </remarks>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (identified by the solution file), then
    /// resolves this project's source directory. Anchoring on the solution file rather than a fixed
    /// number of <c>..</c> segments keeps the test working under any output-path layout.
    /// </summary>
    private static string FindThisTestProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("Could not locate the repository root (BotNexus.slnx) from the test binary.");

        var projectRoot = Path.Combine(directory!.FullName, "tests", "gateway", "BotNexus.Cron.Tests");
        Directory.Exists(projectRoot).ShouldBeTrue($"Expected the test project sources at '{projectRoot}'.");
        return projectRoot;
    }
}
