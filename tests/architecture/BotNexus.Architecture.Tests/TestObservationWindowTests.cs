using System.Globalization;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences test observation windows against wall-clock assumptions (#2825).
/// </summary>
/// <remarks>
/// <para>
/// A test that only passes on an idle machine is a broken test. Production runs under CPU
/// contention too, so a suite that fails when the host is busy cannot distinguish a code bug
/// from a test bug - which is the whole point of having it.
/// </para>
/// <para>
/// Measured on 2026-08-06: eight identical container runs of one commit produced a consistent
/// 6/8 pass rate, and every failure was a different test asserting a short fixed duration.
/// Failures occurred only in the slow (~14 min) lanes, never the fast (~11 min) ones.
/// </para>
/// <para>
/// This fences OBSERVATION windows only - "wait until X becomes true". Widening those cannot
/// weaken an assertion, because the condition must still be met. A duration that is the
/// SUBJECT under test (a product timeout the test asserts fires) is deliberately not covered:
/// there a short value is the point.
/// </para>
/// <para>
/// <b>The rule, in one sentence.</b> If the deadline is reached on the FAILING path it must be
/// generous, because reaching it means something is already wrong and the only question is how
/// legibly that gets reported. If it is reached on the PASSING path it is the assertion, must
/// stay short, and must say so at the call site with a <c>deadline-is-the-assertion:</c> comment.
/// </para>
/// <para>
/// <b>Why <c>WaitAsync</c> is fenced too (#107).</b> <see cref="TestDelayFlakeFenceTests"/> moved
/// authors off <c>Task.Delay</c> and onto <c>task.WaitAsync(TimeSpan)</c>, and this fence's helper
/// list did not cover it - so a hand-written five-second deadline satisfied every fence in the repo
/// and still failed on a loaded runner. It did exactly that three times: <c>TelegramMultiBotTests</c>
/// on PR #75, <c>InboundBoundaryObservabilityTests</c> on <c>main</c> at 6c215e2c, and
/// <c>FileWatcherToolTests</c> on PR #103 - each on a diff that could not reach the code involved.
/// Two of the three fences pointing somewhere and the third not following is how a flake fence ends
/// up steering people into the flake it exists to prevent.
/// </para>
/// <para>
/// The 163 deadlines that predated the rule were frozen in a shrink-only baseline and retired over
/// #112, #113 and #115; the last of them went in #116. With the debt at zero the baseline was
/// deleted rather than left at 0/0, which makes the rule STRONGER than it was: a short deadline now
/// fails outright instead of being measured against an allowance. Do not reintroduce a baseline to
/// admit one - the fix is <c>TestAwait.SignaledAsync</c>, or the justification marker if the expiry
/// really is the assertion.
/// </para>
/// <para>
/// <b>Why tool-argument budgets are NOT regex-fenced.</b> <c>["timeout"] = 5</c> passed to a tool
/// under test is the same wall-clock deadline spelled as data, and it is what took
/// <c>FileWatcherToolTests</c> red. It is nevertheless left to review rather than to a scanner,
/// because a scanner cannot tell the two meanings apart: of the 62 such literals in this suite the
/// large majority - <c>ConfigHydrationServiceTests</c>, <c>AgentConverseToolTests</c>' coercion
/// cases, <c>ShellToolTimeoutCeilingTests</c> - are numbers the test PARSES or CLAMPS and never
/// waits out, and fencing those would be almost entirely false positives. The distinction is
/// semantic, so the guidance is documentary: a tool argument the test spends is subject to the same
/// rule as a deadline, and should be named (see <c>FileWatcherToolTests.WatchBudgetSeconds</c>)
/// rather than written as a bare literal.
/// </para>
/// </remarks>
public class TestObservationWindowTests : ArchitectureTest
{
    /// <summary>
    /// Helpers and forms whose <c>TimeSpan</c> argument is an observation budget. None of these
    /// carries a baseline: a new short window fails outright.
    /// </summary>
    private static readonly string[] WaitHelpers =
    [
        "WaitUntilAsync", "WaitForAsync", "WaitForOutboundAsync", "WaitForConditionAsync",
        "EventuallyAsync", "WaitForStatusAsync", "PollUntilAsync", "SignaledAsync", "WaitAsync"
    ];

    private const int MinimumObservationSeconds = 15;

    /// <summary>
    /// Rejects every short observation window. There is no allowance and no baseline: with the
    /// pre-existing debt retired, one of these is a defect rather than history.
    /// </summary>
    [Fact]
    public void ObservationWindows_AreGenerousEnoughForALoadedHost()
    {
        var violations = Scan(WaitHelpers)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.Select(site => $"{pair.Key}:{site.Line} waits only {site.Seconds:0.##}s"))
            .ToList();

        violations.ShouldBeEmpty(
            $"Observation windows must tolerate a loaded host (>= {MinimumObservationSeconds}s). " +
            "The signal is the synchronisation; the deadline only decides how a hang gets reported, " +
            "so it is never reached on the passing path and there is nothing to buy by keeping it " +
            "tight. Await the fixture's own signal through TestAwait.SignaledAsync, or poll for the " +
            "condition through TestAwait.EventuallyAsync - widening either cannot weaken an assertion, " +
            "because the condition must still be met. If the deadline's EXPIRY is what you are " +
            $"asserting, say so with a '{ShortDeadlineScanner.JustificationMarker} <reason>' comment " +
            $"on the line or just above it.{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    /// <summary>Pins the boundary between a deadline, an exempt one, and prose that merely shows one.</summary>
    [Theory]
    [InlineData("await ready.WaitAsync(TimeSpan.FromSeconds(5));", true)]
    [InlineData("await ready.WaitAsync(TimeSpan.FromMilliseconds(200));", true)]
    [InlineData("await ready.WaitAsync(TimeSpan.FromSeconds(30));", false)]
    [InlineData("await ready.WaitAsync(TimeSpan.FromSeconds(15));", false)]
    [InlineData("await ready.WaitAsync(cancellationToken);", false)]
    // A deadline shown in prose is documentation, not a deadline.
    [InlineData("// prefer this over ready.WaitAsync(TimeSpan.FromSeconds(5));", false)]
    // Expiry as the assertion, claimed either by the existing heuristic or by the marker.
    [InlineData("await Should.ThrowAsync<TimeoutException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(2)));", false)]
    [InlineData("await run.WaitAsync(TimeSpan.FromSeconds(2)); // deadline-is-the-assertion: expiry is the pass", false)]
    // A product timeout nested inside a wrapped call is the SUBJECT under test, not the window.
    [InlineData("await TestAwait.SignaledAsync(Read(c, idleTimeout: TimeSpan.FromMilliseconds(200)), \"x\");", false)]
    // The helper's own budget argument still counts, however deep the call is formatted.
    [InlineData("await TestAwait.EventuallyAsync(() => ok, \"x\", timeout: TimeSpan.FromSeconds(5));", true)]
    public void DeadlineClassifier_DistinguishesBudgetsFromAssertionsAndProse(string source, bool expectedViolation)
    {
        ShortDeadlineScanner
            .FindViolations(source, WaitHelpers, MinimumObservationSeconds)
            .Any()
            .ShouldBe(expectedViolation);
    }

    /// <summary>Proves the justification marker must carry a reason rather than stand alone.</summary>
    [Fact]
    public void DeadlineClassifier_RequiresAReasonAfterTheMarker()
    {
        ShortDeadlineScanner
            .FindViolations(
                "await run.WaitAsync(TimeSpan.FromSeconds(2)); // deadline-is-the-assertion:",
                WaitHelpers,
                MinimumObservationSeconds)
            .ShouldNotBeEmpty("a bare marker is a claim without a reason, so it does not exempt the deadline");
    }

    private Dictionary<string, List<ShortDeadlineScanner.Violation>> Scan(IReadOnlyCollection<string> helpers)
    {
        var result = new Dictionary<string, List<ShortDeadlineScanner.Violation>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Repository.TestsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(nameof(TestObservationWindowTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            var violations = ShortDeadlineScanner.FindViolations(
                File.ReadAllText(file), helpers, MinimumObservationSeconds);
            if (violations.Count == 0)
                continue;

            var relativePath = Path.GetRelativePath(Repository.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            result.Add(relativePath, violations);
        }

        return result;
    }

}

/// <summary>
/// Finds wall-clock deadlines shorter than the minimum a loaded CI host can be relied on to meet.
/// </summary>
internal static class ShortDeadlineScanner
{
    /// <summary>
    /// Marks a deadline whose EXPIRY is the assertion, so a short value is correct. Must appear on
    /// the deadline's own line or within <see cref="JustificationLookbackLines"/> lines above it,
    /// followed by the reason.
    /// </summary>
    internal const string JustificationMarker = "deadline-is-the-assertion:";

    private const int JustificationLookbackLines = 10;

    private static readonly Dictionary<string, Regex> Patterns = [];

    /// <summary>A deadline that is too short, and the budget it actually allows.</summary>
    internal sealed record Violation(int Line, double Seconds);

    internal static List<Violation> FindViolations(
        string source,
        IReadOnlyCollection<string> helpers,
        int minimumSeconds)
    {
        var violations = new List<Violation>();
        var lines = source.Split('\n');
        var masked = MaskLineComments(lines);
        var pattern = PatternFor(helpers);

        foreach (Match match in pattern.Matches(masked))
        {
            var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            var seconds = match.Groups["unit"].Value == "Seconds" ? value : value / 1000d;
            if (seconds >= minimumSeconds)
                continue;

            // A test that asserts a timeout is THROWN needs a short window by design.
            var context = masked[
                Math.Max(0, match.Index - 200)..
                Math.Min(masked.Length, match.Index + match.Length + 300)];
            if (context.Contains("ThrowAsync<TimeoutException>", StringComparison.Ordinal))
                continue;

            // A poll INTERVAL is not an observation budget - a tight interval makes the wait more
            // responsive, not less tolerant, so it is correct as written. The helper's own
            // DECLARATION matches this pattern too (its default interval is a parameter default,
            // not a budget), so skip method definitions as well.
            var argument = masked[match.Index..(match.Index + match.Length)];
            if (argument.Contains("pollInterval", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("interval:", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("Func<", StringComparison.Ordinal)
                || argument.Contains("TimeSpan timeout", StringComparison.Ordinal))
            {
                continue;
            }

            // The matched TimeSpan must be the HELPER's own argument, not one nested inside an
            // argument of it. Wrapping a call that takes a product timeout - e.g.
            // SignaledAsync(ReadWithLimitAsync(content, idleTimeout: TimeSpan.FromMilliseconds(200)),
            // "...") - otherwise reads the SUBJECT under test as the observation window and reports a
            // 0.2s violation against a deadline that is not there.
            if (!IsHelpersOwnArgument(masked, match))
                continue;

            var line = masked[..match.Index].Count(character => character == '\n') + 1;
            if (IsJustified(lines, line))
                continue;

            violations.Add(new Violation(line, seconds));
        }

        return violations;
    }

    /// <summary>
    /// Reports whether the matched <c>TimeSpan</c> sits directly in the helper's own argument list,
    /// rather than nested inside one of its arguments.
    /// </summary>
    private static bool IsHelpersOwnArgument(string masked, Match match)
    {
        // Walk from the helper's opening parenthesis to the TimeSpan, tracking nesting. Depth 1 is
        // the helper's own argument list; anything deeper belongs to a call it wraps.
        var open = masked.IndexOf('(', match.Index);
        if (open < 0)
            return false;

        var timeSpan = masked.IndexOf("TimeSpan.From", match.Index, StringComparison.Ordinal);
        if (timeSpan < 0)
            return false;

        var depth = 0;
        for (var index = open; index < timeSpan; index++)
        {
            if (masked[index] == '(')
                depth++;
            else if (masked[index] == ')')
                depth--;
        }

        return depth == 1;
    }

    /// <summary>
    /// Compiles one regex per helper set. <see cref="FindViolations"/> runs over every test source
    /// for each fenced form, so rebuilding the pattern per file costs thousands of compilations.
    /// </summary>
    private static Regex PatternFor(IReadOnlyCollection<string> helpers)
    {
        var key = string.Join('|', helpers);
        lock (Patterns)
        {
            if (Patterns.TryGetValue(key, out var cached))
                return cached;

            var pattern = new Regex(
                $@"(?:{key})\s*\([^;]*?TimeSpan\.From(?<unit>Seconds|Milliseconds)\(\s*(?<value>\d+(?:\.\d+)?)\s*\)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            Patterns[key] = pattern;
            return pattern;
        }
    }

    /// <summary>
    /// Reports whether the deadline claims, with a reason, that its expiry is the assertion. The
    /// claim is read from the ORIGINAL lines rather than the masked ones, because it lives in a
    /// comment by construction.
    /// </summary>
    private static bool IsJustified(string[] lines, int line)
    {
        var first = Math.Max(0, line - 1 - JustificationLookbackLines);
        for (var index = first; index < line && index < lines.Length; index++)
        {
            var marker = lines[index].IndexOf(JustificationMarker, StringComparison.Ordinal);
            if (marker < 0)
                continue;

            var reason = lines[index][(marker + JustificationMarker.Length)..];
            if (!string.IsNullOrWhiteSpace(reason))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Blanks out line comments while preserving every offset, so a deadline WRITTEN ABOUT in prose
    /// is not read as one while line numbers and context windows stay exact.
    /// </summary>
    private static string MaskLineComments(string[] lines)
    {
        var masked = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var comment = lines[index].IndexOf("//", StringComparison.Ordinal);
            masked[index] = comment < 0
                ? lines[index]
                : lines[index][..comment] + new string(' ', lines[index].Length - comment);
        }

        return string.Join('\n', masked);
    }
}
