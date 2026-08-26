using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents tests from synchronising through finite wall-clock sleeps instead of observable signals.
/// </summary>
public class TestDelayFlakeFenceTests : ArchitectureTest
{
    private static readonly Regex LocalPollerDeclaration = new(
        @"\b(?:private|protected|internal|public)\s+(?:static\s+)?(?:async\s+)?Task\s+" +
        @"(?:WaitUntilAsync|WaitForAsync|EventuallyAsync|PollUntilAsync)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string BaselineFileName = "TestDelayFlakeBaseline.baseline";
    private const int ExpectedBaselineEntryCount = 110;
    private const int ExpectedBaselineViolationCount = 149;

    /// <summary>
    /// Pins the lexical boundary so cancellation sentinels remain valid while finite sleeps are caught.
    /// </summary>
    [Theory]
    [InlineData("await Task.Delay(20);", true)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);", true)]
    [InlineData("Thread.Sleep(100);", true)]
    [InlineData("await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);", false)]
    [InlineData("await Task.Delay(Timeout.Infinite, cancellationToken);", false)]
    [InlineData("await Task.Delay(\n    Timeout.InfiniteTimeSpan,\n    cancellationToken);", false)]
    [InlineData("// await Task.Delay(20);", false)]
    public void FiniteWaitClassifier_DistinguishesSleepsFromCancellationSentinels(
        string source,
        bool expectedViolation)
    {
        FiniteTestDelayScanner.FindViolations(source).Any().ShouldBe(expectedViolation);
    }

    /// <summary>
    /// Rejects finite waits beyond the frozen debt so new tests must coordinate deterministically.
    /// </summary>
    [Fact]
    public void Tests_IntroduceNoNewFiniteWallClockWaits()
    {
        var baseline = ReadBaseline();
        var actual = ScanTestSources();
        var offenders = new List<string>();

        foreach (var (path, violations) in actual.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var allowed = baseline.TryGetValue(path, out var count) ? count : 0;
            if (violations.Count <= allowed)
                continue;

            offenders.Add(
                $"{path}: {violations.Count} finite wait(s), baseline allows {allowed}. " +
                "Offending lines: " +
                string.Join("; ", violations.Skip(allowed).Select(site => $"L{site.Line} {site.Text}")));
        }

        offenders.ShouldBeEmpty(
            "Tests must use TestAwait.EventuallyAsync to observe a condition, synchronize on an explicit signal, " +
            "use virtual time, or inject the delay under test " +
            "instead of sleeping for a finite wall-clock duration. Infinite delays that end through " +
            "cancellation are sentinels and remain valid. Do not add entries to the baseline; replace " +
            "the wait with deterministic coordination." + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Prevents test projects from recreating polling loops with inconsistent timing and diagnostics.
    /// </summary>
    [Fact]
    public void Tests_DoNotDeclareProjectLocalGenericPollers()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateTestSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in LocalPollerDeclaration.Matches(text))
            {
                var line = text[..match.Index].Count(character => character == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(Repository.TestsRoot, file)}:{line}");
            }
        }

        violations.ShouldBeEmpty(
            "Generic condition polling belongs in TestAwait.EventuallyAsync so timeout, cancellation, " +
            "poll interval, and diagnostics stay consistent across test projects. Local pollers:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Proves the local-poller predicate matches declarations but not shared-helper calls.</summary>
    [Fact]
    public void LocalPollerClassifier_DistinguishesDeclarationsFromCalls()
    {
        LocalPollerDeclaration.IsMatch(
            "private static async Task WaitUntilAsync(Func<bool> condition) { await Task.Yield(); }")
            .ShouldBeTrue();
        LocalPollerDeclaration.IsMatch(
            "await TestAwait.EventuallyAsync(() => ready, \"the service to be ready\");")
            .ShouldBeFalse();
    }

    /// <summary>
    /// Forces the baseline to ratchet downward whenever existing finite waits are removed.
    /// </summary>
    [Fact]
    public void FiniteWaitBaseline_HasNoStaleEntries()
    {
        var baseline = ReadBaseline();
        var actual = ScanTestSources();
        var stale = new List<string>();

        baseline.Count.ShouldBe(
            ExpectedBaselineEntryCount,
            "The finite-wait baseline file count may only shrink; lower the expected count when removing an entry.");
        baseline.Values.Sum().ShouldBe(
            ExpectedBaselineViolationCount,
            "The finite-wait baseline violation count may only shrink; lower the expected count when removing a wait.");

        foreach (var (path, allowed) in baseline.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var count = actual.TryGetValue(path, out var violations) ? violations.Count : 0;
            if (count < allowed)
                stale.Add($"{path}: baseline allows {allowed} but only {count} remain.");
        }

        stale.ShouldBeEmpty(
            "The finite-wait baseline is shrink-only. Lower or remove an entry whenever a wait is " +
            "made deterministic." + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    private Dictionary<string, List<FiniteTestDelayScanner.Violation>> ScanTestSources()
    {
        var result = new Dictionary<string, List<FiniteTestDelayScanner.Violation>>(StringComparer.Ordinal);

        foreach (var file in EnumerateTestSources())
        {
            var violations = FiniteTestDelayScanner.FindViolations(File.ReadAllText(file));
            if (violations.Count == 0)
                continue;

            var relativePath = Path.GetRelativePath(Repository.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            result.Add(relativePath, violations);
        }

        return result;
    }

    private IEnumerable<string> EnumerateTestSources()
    {
        foreach (var file in Directory.EnumerateFiles(Repository.TestsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith($"{Path.DirectorySeparatorChar}BotNexus.Testing{Path.DirectorySeparatorChar}TestAwait.cs", StringComparison.Ordinal)
                || file.EndsWith(nameof(TestDelayFlakeFenceTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    private static Dictionary<string, int> ReadBaseline() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, BaselineFileName))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]), StringComparer.Ordinal);
}

internal static partial class FiniteTestDelayScanner
{
    private static readonly Regex FiniteWait = CreateFiniteWaitRegex();

    internal sealed record Violation(int Line, string Text);

    internal static List<Violation> FindViolations(string source)
    {
        var violations = new List<Violation>();
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var sourceOffset = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var code = lines[index].Split("//", 2, StringSplitOptions.None)[0];
            foreach (Match match in FiniteWait.Matches(code))
            {
                var invocationStart = sourceOffset + match.Index;
                var invocationEnd = normalized.IndexOf(';', invocationStart);
                var invocation = normalized[invocationStart..(invocationEnd < 0 ? normalized.Length : invocationEnd)];
                if (invocation.Contains("Timeout.Infinite", StringComparison.Ordinal))
                    continue;

                violations.Add(new Violation(index + 1, lines[index].Trim()));
            }

            sourceOffset += lines[index].Length + 1;
        }

        return violations;
    }

    [GeneratedRegex(@"\b(?:Task\.Delay|Thread\.Sleep)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CreateFiniteWaitRegex();
}