using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences the two concurrency-flake shapes that took <c>main</c> red on 2026-08-11 (#2979).
/// </summary>
/// <remarks>
/// <para>
/// Sibling to <see cref="TestObservationWindowTests"/> (#2825), which fences observation windows
/// that are too SHORT. This one fences races that no window length can fix, because the test is
/// synchronising on the wrong signal or leaving work running past the end of the test.
/// </para>
/// <para>
/// Both patterns are drawn from real failures, not speculation. On a single CI day they produced
/// three separate red PRs and a red <c>main</c>, and each was misread at least once as an
/// unrelated product defect:
/// </para>
/// <list type="number">
/// <item>
/// <b>Verify-after-poll (#2969).</b> A test polls until a status flips, then immediately
/// <c>Verify()</c>s a mock call that the production code performs LATER in the same background
/// continuation. The status flip is not a happens-before edge for the verified call, so the
/// assertion races the very work it is checking. The tell is a Moq message reading
/// "expected once, but was 0 times" that then prints the invocation as having occurred - Moq
/// snapshots the count at Verify and formats the message afterwards, so the call landed in
/// between. Fix: poll for the OBSERVABLE EFFECT (the mock invocation) before verifying it.
/// </item>
/// <item>
/// <b>Unbounded async-iterator helper (#2970).</b> A test helper that yields forever
/// (<c>while (true) { yield return ...; await Task.Delay(...); }</c>) is consumed by code that
/// abandons a pending <c>MoveNextAsync</c> (any timeout/cancellation wrapper). Disposing the
/// iterator while that step is in flight corrupts its <c>ManualResetValueTaskSourceCore</c>; the
/// late continuation then calls <c>GetStatus</c> with a stale version token and throws
/// <c>InvalidOperationException</c> on a ThreadPool worker where nothing can catch it. That does
/// not fail a test - it CRASHES THE TEST HOST and aborts the whole run, taking thousands of
/// unrelated passing tests with it. Fix: make the helper finite, or drive it from a signal the
/// test controls.
/// </item>
/// </list>
/// <para>
/// Neither rule can weaken an assertion: rule 1 adds a wait before an existing assertion, and
/// rule 2 constrains a test's own fixture rather than anything asserted about production code.
/// </para>
/// </remarks>
public class TestConcurrencyFlakeFenceTests : ArchitectureTest
{
    private static readonly string[] WaitHelpers =
    [
        "WaitUntilAsync", "WaitForAsync", "WaitForOutboundAsync", "WaitForConditionAsync",
        "EventuallyAsync", "WaitForStatusAsync", "PollUntilAsync"
    ];

    /// <summary>
    /// Characters of source following a poll-wait that are searched for a bare
    /// <c>Verify(... Times ...)</c>. Wide enough to span the assertion block that normally follows
    /// the wait, narrow enough not to reach into the next test method.
    /// </summary>
    private const int VerifyProximityWindow = 700;

    [Fact]
    public void PollWaits_AreNotFollowedByAnUnsynchronisedMockVerify()
    {
        var testsRoot = Repository.TestsRoot;
        var waitPattern = new Regex($@"\b({string.Join('|', WaitHelpers)})\s*\(", RegexOptions.Compiled);
        // A Verify asserting a call COUNT is the racy shape. Times.Never is excluded: proving a call
        // did not happen cannot be made to pass by waiting longer, so it is not a race of this kind.
        var verifyPattern = new Regex(@"\.Verify\((?<body>[^;]*?)\)\s*;", RegexOptions.Singleline | RegexOptions.Compiled);

        var violations = new List<string>();
        foreach (var file in EnumerateTestSources(testsRoot, nameof(TestConcurrencyFlakeFenceTests)))
        {
            var text = File.ReadAllText(file);

            foreach (Match wait in waitPattern.Matches(text))
            {
                var tail = text[wait.Index..Math.Min(text.Length, wait.Index + VerifyProximityWindow)];

                foreach (Match verify in verifyPattern.Matches(tail))
                {
                    var body = verify.Groups["body"].Value;
                    if (!body.Contains("Times.", StringComparison.Ordinal)
                        || body.Contains("Times.Never", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The wait already observes this mock's invocations, so the verified call is
                    // synchronised by construction. This is the established correct shape in
                    // SubAgentIntegrationTests (SpawnFailure_/SpawnTimeout_CleansWorkspace).
                    var between = tail[..verify.Index];
                    if (between.Contains(".Invocations", StringComparison.Ordinal))
                        continue;

                    var line = text[..(wait.Index + verify.Index)].Count(c => c == '\n') + 1;
                    violations.Add(
                        $"{Path.GetRelativePath(testsRoot, file)}:{line} verifies a call count after a " +
                        "status-only wait");
                }
            }
        }

        violations.ShouldBeEmpty(
            "A poll-wait on one signal does not order a mock call made later by the same background " +
            "continuation. Poll for the mock invocation itself (e.g. `mock.Invocations.Count > 0`) " +
            "before asserting its count, then keep the existing Verify unchanged." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void TestHelpers_DoNotYieldFromAnUnboundedAsyncIterator()
    {
        var testsRoot = Repository.TestsRoot;
        var iteratorPattern = new Regex(
            @"IAsyncEnumerable<[^>]+>\s+\w+\s*\([^)]*\)\s*(?<body>\{)",
            RegexOptions.Compiled);

        var violations = new List<string>();
        foreach (var file in EnumerateTestSources(testsRoot, nameof(TestConcurrencyFlakeFenceTests)))
        {
            var text = File.ReadAllText(file);

            foreach (Match iterator in iteratorPattern.Matches(text))
            {
                var body = ExtractBlock(text, iterator.Groups["body"].Index);
                if (body is null)
                    continue;

                var unbounded = Regex.IsMatch(body, @"while\s*\(\s*true\s*\)") || body.Contains("for (;;)", StringComparison.Ordinal);
                if (!unbounded || !body.Contains("yield return", StringComparison.Ordinal))
                    continue;

                // A loop that can be stopped by the test is bounded in practice.
                if (body.Contains("cancellationToken", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("ct.IsCancellationRequested", StringComparison.Ordinal)
                    || body.Contains("yield break", StringComparison.Ordinal))
                {
                    continue;
                }

                var line = text[..iterator.Index].Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(testsRoot, file)}:{line} yields forever with no exit");
            }
        }

        violations.ShouldBeEmpty(
            "An unbounded async-iterator test helper leaves a MoveNextAsync pending after the consumer " +
            "stops reading. Disposing the iterator in that state corrupts its value-task source and " +
            "throws InvalidOperationException on a ThreadPool thread, which crashes the test host and " +
            "aborts the entire run rather than failing one test. Yield a bounded number of items, honour " +
            "a CancellationToken, or provide a `yield break` the test can reach." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    /// Returns the source text of a brace-delimited block starting at <paramref name="openBraceIndex"/>,
    /// or <c>null</c> when the braces are unbalanced (a partial file or a parse we should not trust).
    /// </summary>
    private static string? ExtractBlock(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return text[openBraceIndex..(i + 1)];
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTestSources(string testsRoot, string selfFileStem)
    {
        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(file).Equals(selfFileStem, StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

}
