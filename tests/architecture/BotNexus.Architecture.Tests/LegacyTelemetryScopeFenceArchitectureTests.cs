using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for clause 4 of #3227: no test in the solution may assert a
/// delta across two reads of <c>LegacyConversationTelemetry.Snapshot()</c> - the process-wide
/// static accumulator. Tests assert against
/// <c>LegacyConversationTelemetry.BeginScope()</c> instead.
///
/// <para><b>Why a fence and not just the fix.</b> The defect in #3227 was invisible in review:
/// <c>var before = Snapshot(); ...; var after = Snapshot(); (after.X - before.X).ShouldBe(1)</c>
/// reads as a careful, well-isolated test. It is not. The delta silently also asserts "no other
/// code in this process incremented X between these two lines", and xUnit runs collections in
/// parallel, so that hidden clause is false at random. It reddened a gate on a branch containing
/// no .NET source at all, which is the worst failure mode available: it trains readers to
/// dismiss a red gate. Converting today's eight call sites removes today's instances and does
/// nothing about the ninth, whose author copies the shape from the file next door precisely
/// because it looks correct. This is the migration-straggler pattern (#3171, #3187, #3208,
/// #3215): a helper without a fence is a suggestion.</para>
///
/// <para><b>The legitimate remedy is always the same:</b> wrap the exercised code in
/// <c>using var telemetry = LegacyConversationTelemetry.BeginScope();</c> and assert on
/// <c>telemetry.Snapshot()</c> directly. The scope flows on <c>AsyncLocal</c>, starts empty and
/// only accumulates the test's own activity, so there is no before/after window for a sibling to
/// interfere with - and no assertion has to be weakened to get there. Every exact-delta
/// expectation in <c>LegacyConversationTelemetryTests</c> survived the conversion verbatim.</para>
///
/// <para><b>What is still permitted.</b> Reading the statics is fine; asserting a
/// <i>subtraction between two reads</i> is not. A test may legitimately assert
/// <c>LegacyConversationTelemetry.Snapshot().HasActivity.ShouldBeTrue()</c>, or assert a
/// lower bound such as <c>ShouldBeGreaterThanOrEqualTo</c> - both are monotone properties that
/// concurrent increments cannot falsify. Only the exact-delta shape is unsound, so only the
/// exact-delta shape is fenced. Files needing a genuine exemption go in
/// <see cref="AllowedStaticDeltaSites"/> WITH A REASON, because an entry expires loudly via
/// <see cref="EveryAllowListEntry_StillExists_AndStillTakesAStaticDelta"/> while a loosened
/// pattern expires silently.</para>
///
/// <para>Source-text based, like <see cref="CliSafeDisplayFenceArchitectureTests"/>: "did this
/// assertion subtract one static snapshot from another" is a property of the source that the
/// compiled assembly retains no usable trace of.</para>
/// </summary>
public sealed class LegacyTelemetryScopeFenceArchitectureTests
{
    /// <summary>Root of the test tree this fence governs.</summary>
    private const string TestsRoot = "tests";

    /// <summary>The scoped accumulator that is the sanctioned seam.</summary>
    private const string ScopeSource =
        "src/gateway/BotNexus.Gateway.Sessions/LegacyConversationTelemetryScope.cs";

    /// <summary>The static accumulator whose delta shape is fenced.</summary>
    private const string TelemetrySource =
        "src/gateway/BotNexus.Gateway.Sessions/LegacyConversationTelemetry.cs";

    /// <summary>
    /// The one file expected to exercise the seam end to end, so the fence fails loudly if the
    /// converted tests are deleted or reverted rather than passing vacuously.
    /// </summary>
    private const string ConvertedTestSource =
        "tests/gateway/BotNexus.Gateway.Tests/Sessions/LegacyConversationTelemetryTests.cs";

    /// <summary>
    /// This fence's own source, excluded from the scan.
    /// </summary>
    /// <remarks>
    /// Not an exemption in the allow-list sense - this file contains no executed telemetry
    /// assertion at all. It contains the offending shape as a <b>string literal specimen</b>
    /// inside <see cref="Fence_IsNotVacuous_DetectsTheStaticDeltaAndDoesNotFlagTheScopedRemedy"/>,
    /// which is exactly what proves the detector is not vacuous. A detector that flagged its own
    /// test corpus could never go green, so authors would delete the specimen - and then the
    /// fence would be silently matching nothing. The exclusion is kept honest by
    /// <see cref="Fence_OwnSource_StillCarriesTheSpecimen"/>, which fails if the specimen ever
    /// stops being present.
    /// </remarks>
    private const string FenceSource =
        "tests/architecture/BotNexus.Architecture.Tests/LegacyTelemetryScopeFenceArchitectureTests.cs";

    /// <summary>
    /// Test files permitted to subtract one static telemetry snapshot from another, each with
    /// the reason. Empty today - #3227 converted the only site - and it should stay that way.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedStaticDeltaSites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Binds a local to a read of the process-wide statics, e.g.
    /// <c>var before = LegacyConversationTelemetry.Snapshot();</c>. Whitespace-tolerant, and it
    /// deliberately does NOT match <c>telemetry.Snapshot()</c> on a scope instance - that is the
    /// remedy, not the defect.
    /// </summary>
    private static readonly Regex StaticSnapshotBinding =
        new(@"\b(?:var|LegacyConversationTelemetrySnapshot)\s+(\w+)\s*=\s*LegacyConversationTelemetry\s*\.\s*Snapshot\s*\(\s*\)",
            RegexOptions.Compiled);

    /// <summary>The sanctioned seam: opening a scoped accumulator.</summary>
    private static readonly Regex ScopeUse =
        new(@"\bLegacyConversationTelemetry\s*\.\s*BeginScope\s*\(\s*\)", RegexOptions.Compiled);

    private static string RepoRoot => FindRepoRoot();

    [Fact]
    public void ScopedSeam_Exists()
    {
        var scopePath = ResolvePath(ScopeSource);
        File.Exists(scopePath).ShouldBeTrue(
            "LegacyConversationTelemetryScope - the AsyncLocal-flowed accumulator that gives " +
            "telemetry tests an assertion seam they actually control (#3227) - is missing. " +
            $"Expected at: {scopePath}");

        var telemetry = File.ReadAllText(ResolvePath(TelemetrySource));
        telemetry.ShouldContain(
            "BeginScope",
            Case.Sensitive,
            "LegacyConversationTelemetry no longer exposes BeginScope, so the remedy this fence " +
            "directs authors to does not exist. See #3227.");
    }

    [Fact]
    public void NoTest_AssertsADeltaBetweenTwoStaticSnapshots()
    {
        var offenders = EnumerateTestSources()
            .Select(file => (File: file, Text: File.ReadAllText(file)))
            .Where(candidate => TakesStaticDelta(candidate.Text))
            .Select(candidate => ToRepoRelative(candidate.File))
            .Where(relative => !AllowedStaticDeltaSites.ContainsKey(relative)
                            && !string.Equals(relative, FenceSource, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These test files subtract one LegacyConversationTelemetry.Snapshot() reading from " +
            "another: " + string.Join(", ", offenders) +
            ".\nThose counters are process-wide statics, so an exact-delta assertion also " +
            "asserts - invisibly - that no sibling test incremented the same counter between " +
            "the two reads. xUnit runs collections in parallel, so that is false at random and " +
            "produces inherited red gates on unrelated branches (#3227). " +
            "REMEDY: open 'using var telemetry = LegacyConversationTelemetry.BeginScope();' " +
            "around the exercised code and assert on 'telemetry.Snapshot()' directly - the " +
            "scope starts empty and only ever collects this flow's activity, so no exact-delta " +
            "expectation has to be weakened. A monotone assertion (HasActivity, " +
            "ShouldBeGreaterThanOrEqualTo) against the statics remains permitted.");
    }

    [Fact]
    public void EveryAllowListEntry_StillExists_AndStillTakesAStaticDelta()
    {
        foreach (var (relative, reason) in AllowedStaticDeltaSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Allow-listed file no longer exists: {relative} (reason on record: {reason}). " +
                "Remove the entry - a stale allow-list slowly becomes a blanket exemption. See #3227.");

            TakesStaticDelta(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' is allow-listed to take a static telemetry delta but no longer " +
                "does. Remove the entry so the exemption cannot silently cover a future " +
                "assertion added to this file. See #3227.");
        }
    }

    /// <summary>
    /// The positive half of the fence: the converted tests must actually use the scoped seam, so
    /// a file that is emptied or reverted to a no-op fails rather than passing vacuously by
    /// simply containing no static delta.
    /// </summary>
    [Fact]
    public void ConvertedTests_AssertThroughTheScopedSeam()
    {
        var path = ResolvePath(ConvertedTestSource);
        File.Exists(path).ShouldBeTrue(
            $"Expected telemetry test source not found: {path}. If it was renamed, update this " +
            "fence - do not drop the entry without confirming the scoped assertions moved with it.");

        var text = File.ReadAllText(path);
        ScopeUse.Matches(text).Count.ShouldBeGreaterThanOrEqualTo(
            5,
            "LegacyConversationTelemetryTests no longer opens telemetry scopes, so either the " +
            "#3227 conversion was reverted or the tests were gutted. Every case that asserts an " +
            "exact activation count must own a scope. See #3227 clause 1.");
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsTheStaticDeltaAndDoesNotFlagTheScopedRemedy()
    {
        const string racyTest = """
            public sealed class NinthTelemetryTests
            {
                [Fact]
                public async Task Bind_RecordsActivation()
                {
                    var before = LegacyConversationTelemetry.Snapshot();
                    await resolver.BindActiveSessionIfNoneAsync(conversation, sessionId);
                    var after = LegacyConversationTelemetry.Snapshot();
                    (after.TotalBinds - before.TotalBinds).ShouldBe(1);
                }
            }
            """;

        TakesStaticDelta(racyTest).ShouldBeTrue(
            "Vacuity guard: the exact shape that reddened gate 20260816200757-1c98012f MUST be " +
            "detected. If this fails the fence matches nothing and the ninth telemetry test " +
            "reintroduces #3227 unnoticed.");

        const string scopedTest = """
            public sealed class CompliantTelemetryTests
            {
                [Fact]
                public async Task Bind_RecordsActivation()
                {
                    using var telemetry = LegacyConversationTelemetry.BeginScope();
                    await resolver.BindActiveSessionIfNoneAsync(conversation, sessionId);
                    telemetry.Snapshot().TotalBinds.ShouldBe(1);
                }
            }
            """;

        TakesStaticDelta(scopedTest).ShouldBeFalse(
            "Positive pin: the sanctioned remedy must NOT be flagged, otherwise correct code " +
            "cannot go green and authors will route around the fence.");
        ScopeUse.IsMatch(scopedTest).ShouldBeTrue(
            "Positive pin: the sanctioned remedy must satisfy the BeginScope detector.");

        const string monotoneAssertion = """
            public sealed class MonotoneTelemetryTests
            {
                [Fact]
                public void Statics_StillRecord()
                {
                    var before = LegacyConversationTelemetry.Snapshot();
                    LegacyConversationTelemetry.RecordBind();
                    var after = LegacyConversationTelemetry.Snapshot();
                    (after.TotalBinds - before.TotalBinds).ShouldBeGreaterThanOrEqualTo(1);
                }
            }
            """;

        TakesStaticDelta(monotoneAssertion).ShouldBeFalse(
            "A lower-bound assertion on the statics is sound under concurrency - a sibling " +
            "increment can only make it more true - so it must remain permitted. Fencing it " +
            "would push authors to delete the process-wide coverage that #2311 depends on.");
    }

    /// <summary>
    /// Keeps the <see cref="FenceSource"/> exclusion honest: the only reason this file is
    /// skipped is that it carries the offending shape as a specimen. If the specimen goes, the
    /// exclusion has become an unexamined blanket exemption over a real test file, and this
    /// fails so the exclusion is removed with it.
    /// </summary>
    [Fact]
    public void Fence_OwnSource_StillCarriesTheSpecimen()
    {
        var path = ResolvePath(FenceSource);
        File.Exists(path).ShouldBeTrue(
            $"This fence's own source was not found at {path}. If it was renamed, update " +
            "FenceSource - otherwise the scan will flag it and no run can go green.");

        TakesStaticDelta(File.ReadAllText(path)).ShouldBeTrue(
            "This fence's own source no longer contains the racy specimen it is excluded for. " +
            "Either restore the specimen in Fence_IsNotVacuous..., or delete the FenceSource " +
            "exclusion - an exclusion with no remaining justification is a blanket exemption " +
            "over a real test file. See #3227.");
    }

    /// <summary>
    /// Guards the scan itself: if the test tree moves or empties, every offender query returns
    /// nothing and the fence would pass while enforcing nothing.
    /// </summary>
    [Fact]
    public void Fence_ScansANonEmptyTestTree()
        => EnumerateTestSources().Count().ShouldBeGreaterThan(
            200,
            "Vacuity guard: the test tree scanned by this fence is missing or nearly empty, so " +
            "every assertion above would pass without inspecting anything. Check that " +
            $"'{TestsRoot}' still exists relative to the repo root.");

    /// <summary>
    /// True when the source binds two locals to readings of the process-wide statics and then
    /// asserts an exact value on the difference between them. Both halves are required: the
    /// bindings alone are harmless, and the exactness is what makes the assertion depend on the
    /// absence of concurrent writers.
    /// </summary>
    private static bool TakesStaticDelta(string source)
    {
        var boundLocals = StaticSnapshotBinding.Matches(source)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (boundLocals.Count < 2)
            return false;

        foreach (var left in boundLocals)
        {
            foreach (var right in boundLocals)
            {
                if (string.Equals(left, right, StringComparison.Ordinal))
                    continue;

                // e.g. "(after.TotalBinds - before.TotalBinds).ShouldBe(" - anchored on ShouldBe
                // so a lower-bound assertion (ShouldBeGreaterThanOrEqualTo) is not caught.
                var exactDelta = new Regex(
                    $@"{Regex.Escape(left)}\s*\.\s*\w+\s*-\s*{Regex.Escape(right)}\s*\.\s*\w+\s*\)?\s*\.\s*ShouldBe\s*\(",
                    RegexOptions.None);

                if (exactDelta.IsMatch(source))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateTestSources()
    {
        var testsRoot = Path.Combine(RepoRoot, TestsRoot);
        Directory.Exists(testsRoot).ShouldBeTrue($"Test source root not found: {testsRoot}");
        return Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));
    }

    private static string ToRepoRelative(string absolutePath) =>
        Path.GetRelativePath(RepoRoot, absolutePath).Replace('\\', '/');

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
