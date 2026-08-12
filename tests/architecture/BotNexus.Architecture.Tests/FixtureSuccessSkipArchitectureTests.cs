using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2491, acceptance criterion 4:
/// <b>"Repo-wide audit of fixture-success-gated skips, with findings recorded."</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>Skip.IfNot(fixture.Succeeded, ...)</c> is a <b>mass-vacuity generator</b>. An xUnit
/// collection fixture is constructed once for an entire collection, so a single provisioning
/// fault flips one boolean and every test class in that collection converts itself into a
/// skip. The runner then prints "Passed!" and exits 0. That is strictly worse than a red
/// build: it reports confidence nobody earned, and it does so silently.
/// </para>
/// <para>
/// This is the concrete history, not a hypothetical. On <c>main</c> the
/// <c>NewUserExperience</c> collection - 23 test classes - was fully dark because
/// <c>botnexus init</c> seeds the <c>assistant</c> agent and the fixture's provisioning loop
/// then re-added it, so the CLI exited non-zero (#2491/#2738). It went dark a second time when
/// the fixture's solution prebuild raced concurrent test hosts (#2739/#2749). In both cases CI
/// was green throughout, and the E2E run reported <i>"No test matches the given testcase
/// filter"</i>.
/// </para>
/// <para>
/// The structural remedy is not "never skip". Skipping is legitimate when a suite is genuinely
/// opt-in on external infrastructure. The remedy is that <b>the skip must never be the only
/// signal</b>: every fixture whose success flag gates a collection must additionally be watched
/// by a test that <i>asserts</i> - a plain <c>[Fact]</c> that fails loudly and by name when
/// provisioning failed. <see cref="!:BotNexus.Integration.E2E.Tests.FixtureHealthTests"/> is the
/// reference implementation of that shape.
/// </para>
/// <para>
/// Fixtures that are genuinely environment-gated and cannot assert in CI are permitted, but only
/// by explicit registration in <see cref="EnvironmentGatedFixtures"/> with a written reason. The
/// point is that the exemption is visible in source review rather than implied by an absent test.
/// </para>
/// <para>
/// The fence carries anti-vacuity assertions of its own: it proves it scanned a plausible number
/// of test sources, that it actually discovered collection fixtures and success flags, and it
/// pins the health-assertion detector with positive and negative samples. A fence that silently
/// matches nothing would be the very defect it exists to prevent.
/// </para>
/// </remarks>
public sealed class FixtureSuccessSkipArchitectureTests
{
    /// <summary>
    /// Fixtures whose success flag legitimately cannot be asserted in an automated gate, with the
    /// recorded reason. Adding an entry here is a deliberate, reviewable act.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EnvironmentGatedFixtures =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LiveGatewayFixture"] =
                "BotNexus.Conversation.Tests probes a DEVELOPER-RUN gateway at localhost:5006 that no " +
                "CI or container gate starts. Asserting IsAvailable would fail every unattended run. " +
                "Recorded finding (#2491 AC4): this suite is permanently dark in CI - its ~28 tests " +
                "have never contributed gate signal and should be either re-hosted on a self-provisioned " +
                "fixture or removed from the gate scope.",
        };

    /// <summary>
    /// Public boolean fixture properties that act as a collection-wide kill switch. Matched on the
    /// property name so a renamed flag (Succeeded -> InstallSucceeded) cannot slip past the fence.
    /// </summary>
    private static readonly Regex SuccessFlagDeclaration = new(
        @"public\s+bool\s+(?<flag>\w*(?:Succeeded|Available|Ready|Initialized|Provisioned))\s*\{\s*get",
        RegexOptions.Compiled);

    private static readonly Regex CollectionFixtureUsage = new(
        @"ICollectionFixture<\s*(?<fixture>\w+)\s*>",
        RegexOptions.Compiled);

    /// <summary>Minimum tracked test sources the sweep must inspect before its result means anything.</summary>
    private const int MinimumScannedTestFiles = 100;

    /// <summary>Minimum collection fixtures the sweep must discover. The repo has several.</summary>
    private const int MinimumDiscoveredFixtures = 4;

    [Fact]
    public void EveryCollectionFixtureSuccessFlag_IsWatchedByAnAssertingHealthTest()
    {
        var sources = EnumerateTrackedTestSources().ToList();

        sources.Count.ShouldBeGreaterThan(
            MinimumScannedTestFiles,
            $"Anti-vacuity: the sweep only inspected {sources.Count} tracked test sources. " +
            "A fence that scans nothing is trivially green - fix the enumeration.");

        var fixtureNames = sources
            .SelectMany(s => CollectionFixtureUsage.Matches(s.Content).Select(m => m.Groups["fixture"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        fixtureNames.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumDiscoveredFixtures,
            $"Anti-vacuity: only {fixtureNames.Count} collection fixtures were discovered. The " +
            "ICollectionFixture<T> detector has stopped matching, so this fence would pass without " +
            "inspecting anything real.");

        var flagsFound = 0;
        var offenders = new List<string>();

        foreach (var fixture in fixtureNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            var declaration = sources.FirstOrDefault(
                s => Regex.IsMatch(s.Content, $@"class\s+{Regex.Escape(fixture)}\b"));
            if (declaration.Content is null)
            {
                // Fixture declared outside tests/ (or in a package). Nothing to audit.
                continue;
            }

            var flags = SuccessFlagDeclaration.Matches(declaration.Content)
                .Select(m => m.Groups["flag"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (flags.Count == 0)
            {
                // A fixture with no success flag cannot gate a collection on one.
                continue;
            }

            flagsFound += flags.Count;

            if (EnvironmentGatedFixtures.ContainsKey(fixture))
            {
                continue;
            }

            // Only the fixture's own project can see it, so scope the search there.
            var project = ProjectOf(declaration.Relative);
            var siblings = sources.Where(s => ProjectOf(s.Relative) == project).ToList();

            foreach (var flag in flags)
            {
                var gatedBySkip = siblings.Any(s => IsSkipGatedOn(s.Content, flag));
                if (!gatedBySkip)
                {
                    // The flag is not used as a collection kill switch at all - nothing to guard.
                    continue;
                }

                var watched = siblings.Any(s => HasAssertingHealthTest(s.Content, flag));
                if (!watched)
                {
                    offenders.Add(
                        $"{project}: fixture '{fixture}' gates tests on '{flag}' via Skip, but no " +
                        "non-skippable [Fact] asserts it. A provisioning failure would turn the whole " +
                        "collection into silent skips while the gate reports green.");
                }
            }
        }

        flagsFound.ShouldBeGreaterThan(
            0,
            "Anti-vacuity: no fixture success flags were found at all. The success-flag detector " +
            "has stopped matching and this fence is inspecting nothing.");

        offenders.Sort(StringComparer.Ordinal);
        offenders.ShouldBeEmpty(
            "Collection fixtures gate their tests on a success flag with no asserting health test " +
            "(issue #2491). One provisioning fault silently disables the entire collection while CI " +
            "stays green - this is the exact failure that hid 56 broken E2E tests on main.\n" +
            "Fix: add a class modelled on BotNexus.Integration.E2E.Tests.FixtureHealthTests with a " +
            "plain [Fact] (NOT [SkippableFact], and containing no Skip. call) that fails by name when " +
            "the flag is false. If the fixture is genuinely environment-gated, register it in " +
            "EnvironmentGatedFixtures with a written reason.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    // ------------------------------------------------------------------
    // Anti-vacuity pins on the detectors themselves.
    // ------------------------------------------------------------------

    [Theory]
    // the reference shape: plain [Fact], reads the flag, no Skip anywhere in the body
    [InlineData("[Fact]\npublic void Health()\n{\n    if (_fx.Succeeded) return;\n    throw new XunitException(\"dead\");\n}")]
    // a direct Shouldly assertion is equally acceptable
    [InlineData("[Fact]\npublic void Health()\n{\n    _fixture.Succeeded.ShouldBeTrue(\"fixture died\");\n}")]
    public void HealthDetector_AcceptsAnAssertingFact(string sample)
        => HasAssertingHealthTest(sample, "Succeeded").ShouldBeTrue(
            "Positive pin failed - the detector no longer recognises an asserting health test, so " +
            "the fence would flag compliant code. Sample:\n" + sample);

    [Theory]
    // a SkippableFact is the failure mode, not the remedy
    [InlineData("[SkippableFact]\npublic void Health()\n{\n    Skip.If(!_fx.Succeeded, \"dead\");\n    _fx.Succeeded.ShouldBeTrue();\n}")]
    // a [Fact] that still skips is just as dark
    [InlineData("[Fact]\npublic void Health()\n{\n    Skip.If(!_fx.Succeeded, \"dead\");\n}")]
    // indirection through a helper does not change that it skips
    [InlineData("[SkippableFact]\npublic void Health()\n{\n    Skip.If(ShouldSkip(), Reason());\n    _fx.Succeeded.ShouldBeTrue();\n}")]
    // a test that never mentions the flag cannot be watching it
    [InlineData("[Fact]\npublic void Unrelated()\n{\n    Assert.True(true);\n}")]
    public void HealthDetector_RejectsSkippingOrUnrelatedTests(string sample)
        => HasAssertingHealthTest(sample, "Succeeded").ShouldBeFalse(
            "Negative pin failed - the detector accepted a test that still permits the collection to " +
            "go silently dark. Sample:\n" + sample);

    [Theory]
    [InlineData("Skip.IfNot(_fx.Succeeded, $\"Fixture failed: {_fx.Error}\");")]
    [InlineData("Skip.If(!_fix.Succeeded, _fix.Error ?? \"Fixture not ready\");")]
    // ONE LEVEL OF INDIRECTION. This is not a hypothetical: ExtensionBootSmokeTests spelled its
    // kill switch as Skip.If(ShouldSkip(), ...) with `private bool ShouldSkip() => !_fx.Succeeded;`.
    // A literal-only detector reports that collection as ungated and the fence goes vacuously green
    // on the very shape it exists to catch.
    [InlineData("Skip.If(ShouldSkip(), SkipReason());\nprivate bool ShouldSkip() => !_fx.Succeeded;")]
    public void SkipGateDetector_RecognisesFlagGatedSkips(string sample)
        => IsSkipGatedOn(sample, "Succeeded").ShouldBeTrue(
            "Positive pin failed - the fence would stop noticing flag-gated skips entirely. Sample:\n" + sample);

    [Theory]
    [InlineData("Skip.If(browser is null, \"no browser\");")]
    [InlineData("_fixture.Succeeded.ShouldBeTrue(\"install failed\");")]
    // indirection through a helper that does NOT consult the flag stays unflagged
    [InlineData("Skip.If(ShouldSkip(), \"no browser\");\nprivate bool ShouldSkip() => _browser is null;")]
    public void SkipGateDetector_IgnoresUnrelatedSkipsAndPlainAssertions(string sample)
        => IsSkipGatedOn(sample, "Succeeded").ShouldBeFalse(
            "Negative pin failed - the fence would demand health tests for flags that gate nothing. Sample:\n" + sample);

    // ------------------------------------------------------------------
    // Detectors.
    // ------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="content"/> contains a <c>Skip.If</c>/<c>Skip.IfNot</c> whose
    /// condition mentions <paramref name="flag"/> - i.e. the flag is a collection kill switch.
    /// </summary>
    internal static bool IsSkipGatedOn(string content, string flag)
    {
        var escaped = Regex.Escape(flag);

        // Direct form: the flag appears inside the Skip condition.
        if (Regex.IsMatch(content, $@"Skip\.(?:If|IfNot)\s*\([^;\r\n]*\b{escaped}\b"))
        {
            return true;
        }

        // Indirect form: Skip.If(Helper(), ...) where Helper() consults the flag. Resolving one
        // level is enough for every shape in this repo and keeps the detector explainable.
        foreach (Match call in Regex.Matches(content, @"Skip\.(?:If|IfNot)\s*\(\s*!?\s*(?<helper>\w+)\s*\("))
        {
            var helper = call.Groups["helper"].Value;
            if (Regex.IsMatch(
                    content,
                    $@"\b(?:bool|Task<bool>)\s+{Regex.Escape(helper)}\s*\([^)]*\)\s*(?:=>[^;\r\n]*\b{escaped}\b|\{{[^}}]*\b{escaped}\b)"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="content"/> declares at least one test method that is attributed
    /// <c>[Fact]</c> (never <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>), mentions
    /// <paramref name="flag"/>, and contains no <c>Skip.</c> call anywhere in its body. Such a
    /// method cannot degrade into a skip, so it fails loudly when provisioning fails.
    /// </summary>
    internal static bool HasAssertingHealthTest(string content, string flag)
    {
        foreach (var body in EnumerateFactBodies(content))
        {
            if (!Regex.IsMatch(body, $@"\b{Regex.Escape(flag)}\b"))
            {
                continue;
            }

            if (body.Contains("Skip.", StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Yields the source of each method attributed with a plain <c>[Fact]</c>, from the attribute to
    /// the start of the next attribute or the end of the type. Brace matching is unnecessary here:
    /// the detector only asks whether a token appears within the method's own text, and attributes
    /// reliably delimit consecutive test methods.
    /// </summary>
    private static IEnumerable<string> EnumerateFactBodies(string content)
    {
        var attributes = Regex.Matches(content, @"^[ \t]*\[(?<attr>\w+)[^\]]*\]", RegexOptions.Multiline)
            .Cast<Match>()
            .ToList();

        for (var i = 0; i < attributes.Count; i++)
        {
            if (!string.Equals(attributes[i].Groups["attr"].Value, "Fact", StringComparison.Ordinal))
            {
                continue;
            }

            var start = attributes[i].Index;
            var end = i + 1 < attributes.Count ? attributes[i + 1].Index : content.Length;
            yield return content[start..end];
        }
    }

    private static string ProjectOf(string relative)
    {
        var parts = relative.Split('/');
        // tests/<category>/<Project>/... - fall back to the whole path when shallower.
        return parts.Length >= 3 ? string.Join('/', parts.Take(3)) : relative;
    }

    // ------------------------------------------------------------------
    // Source enumeration (read-only git; see TestGitInvocationScopeArchitectureTests).
    // ------------------------------------------------------------------

    private static IEnumerable<(string Relative, string Content)> EnumerateTrackedTestSources()
    {
        var repoRoot = FindSweepRepoRoot();
        foreach (var relative in EnumerateTrackedFiles(repoRoot))
        {
            var normalised = relative.Replace('\\', '/');
            if (!normalised.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
                !normalised.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // This fence documents both the compliant and the non-compliant shapes in its own
            // detector pins, so it would match itself. Allowlist by basename, as sibling fences do.
            if (string.Equals(Path.GetFileName(normalised), "FixtureSuccessSkipArchitectureTests.cs", StringComparison.OrdinalIgnoreCase))
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
}
