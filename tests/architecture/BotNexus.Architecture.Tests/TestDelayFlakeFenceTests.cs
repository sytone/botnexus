using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents tests from synchronising through finite wall-clock sleeps instead of observable signals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacing a sleep with <c>WaitAsync(TimeSpan)</c> does not satisfy this fence's intent.</b> It
/// is still a finite wall-clock deadline and still fails when CI is saturated - the ban simply moves
/// the flake somewhere this scanner cannot see it. That happened, repeatedly: a comment in
/// <c>InboundBoundaryObservabilityTests</c> read "Task.Delay is banned in tests by
/// TestDelayFlakeFenceTests; WaitAsync is the sanctioned form", and hand-written five-second
/// deadlines then took three unrelated PRs red (#75, #103, and <c>main</c> at 6c215e2c). A fence that
/// names what is forbidden without naming what is correct redirects the defect rather than removing
/// it.
/// </para>
/// <para>
/// What is actually sanctioned, in order of preference: await a signal the fixture raises through
/// <c>TestAwait.SignaledAsync</c>; poll for the observable condition through
/// <c>TestAwait.EventuallyAsync</c>; or drive the clock yourself with <c>ManualTimeProvider</c>. All
/// three end when the thing you are waiting for happens, not when a guess about the host's speed
/// expires. <see cref="TestObservationWindowTests"/> enforces the deadline half of that contract,
/// <c>WaitAsync</c> included.
/// </para>
/// </remarks>
public class TestDelayFlakeFenceTests : ArchitectureTest
{
    private static readonly Regex LocalPollerDeclaration = new(
        @"\b(?:private|protected|internal|public)\s+(?:static\s+)?(?:async\s+)?Task\s+" +
        @"(?:WaitUntilAsync|WaitForAsync|EventuallyAsync|PollUntilAsync)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string BaselineFileName = "TestDelayFlakeBaseline.baseline";
    // #107 ratchet: FileWatcherToolTests' rapid-change debounce test drove five writes to a real
    // file 40ms apart. The sleep was never what made the changes rapid - the debounce window is - so
    // raising the events through the tool's own watcher seam removed it and left only the clamp
    // watchdog behind.
    // #3625 ratchet: CrossWorldFederationControllerTests' single finite wait (a 25ms Task.Delay
    // poll loop) was replaced with an awaited signal, so its baseline entry was removed entirely.
    // Both sides ratcheted this independently: upstream's #3820 replaced DefaultSubAgentManager-
    // TimeoutTests' two finite waits with an awaited dispatch signal, and our fix to
    // InMemoryActivityBroadcaster.SubscribeAsync removed DefaultAgentRegistryTests' two 20ms sleeps
    // (they were waiting on a subscriber that had not been registered yet). Both entries are gone,
    // so the counts below are read off the merged baseline, not carried over from either branch.
    private const int ExpectedBaselineEntryCount = 107;
    private const int ExpectedBaselineViolationCount = 143;

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
            "Tests must use TestAwait.EventuallyAsync to observe a condition, TestAwait.SignaledAsync to " +
            "await a signal the fixture raises, use virtual time, or inject the delay under test " +
            "instead of sleeping for a finite wall-clock duration. Infinite delays that end through " +
            "cancellation are sentinels and remain valid. Rewriting the sleep as " +
            "WaitAsync(TimeSpan.FromSeconds(n)) does NOT satisfy this rule: it is the same wall-clock " +
            "deadline, it fails on the same loaded runner, and TestObservationWindowTests fences it. " +
            "Do not add entries to the baseline; replace " +
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