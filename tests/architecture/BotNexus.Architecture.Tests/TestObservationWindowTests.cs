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
/// </remarks>
public class TestObservationWindowTests
{
    private static readonly string[] WaitHelpers =
    [
        "WaitUntilAsync", "WaitForAsync", "WaitForOutboundAsync", "WaitForConditionAsync",
        "EventuallyAsync", "WaitForStatusAsync", "PollUntilAsync"
    ];

    private const int MinimumObservationSeconds = 15;

    [Fact]
    public void ObservationWindows_AreGenerousEnoughForALoadedHost()
    {
        var testsRoot = FindTestsRoot();
        var pattern = new Regex(
            $@"({string.Join('|', WaitHelpers)})\s*\([^;]*?TimeSpan\.From(?<unit>Seconds|Milliseconds)\(\s*(?<value>\d+(?:\.\d+)?)\s*\)",
            RegexOptions.Singleline);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith("TestObservationWindowTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in pattern.Matches(text))
            {
                var value = double.Parse(match.Groups["value"].Value);
                var seconds = match.Groups["unit"].Value == "Seconds" ? value : value / 1000d;

                // A test that asserts a timeout is THROWN needs a short window by design.
                var context = text[Math.Max(0, match.Index - 200)..match.Index];
                if (context.Contains("ThrowAsync<TimeoutException>", StringComparison.Ordinal))
                    continue;

                // A poll INTERVAL is not an observation budget - a tight interval makes the
                // wait more responsive, not less tolerant, so it is correct as written. The
                // helper's own DECLARATION matches this pattern too (its default interval is a
                // parameter default, not a budget), so skip method definitions as well.
                var argument = text[match.Index..Math.Min(text.Length, match.Index + match.Length)];
                if (argument.Contains("pollInterval", StringComparison.OrdinalIgnoreCase)
                    || argument.Contains("interval:", StringComparison.OrdinalIgnoreCase)
                    || argument.Contains("Func<", StringComparison.Ordinal)
                    || argument.Contains("TimeSpan timeout", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seconds < MinimumObservationSeconds)
                {
                    var line = text[..match.Index].Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetRelativePath(testsRoot, file)}:{line} waits only {seconds:0.##}s");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"Observation windows must tolerate a loaded host (>= {MinimumObservationSeconds}s). " +
            "Poll for the condition instead of assuming the machine is fast; widening an " +
            "observation window cannot weaken an assertion because the condition must still " +
            $"be met.{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static string FindTestsRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return Path.Combine(current.FullName, "tests");

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
