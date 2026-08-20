using System.Text.RegularExpressions;

using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2774: the testhost firewall lease
/// must derive its program set from <b>build output</b>, never from a
/// hard-coded <c>testhost.exe</c> literal.
/// </summary>
/// <remarks>
/// <para>
/// Why this fence exists rather than only the PowerShell suites: the
/// behavioural pins in <c>scripts/repo/FirewallLeaseProgram.Tests.ps1</c> and
/// <c>scripts/repo/FirewallRulePrune.Tests.ps1</c> reason about Windows paths
/// and Windows Firewall rule shapes, so they cannot run on the Linux
/// validation runner. Without a fence here, a future edit could quietly
/// restore the literal and no gate would notice - which is exactly how #2774
/// survived PR #2783 with clause 2 unmet.
/// </para>
/// <para>
/// The defect the literal caused: fixtures that spawn a child process
/// (<c>CliTestFixture</c>, <c>CrossProcessConfigWriteTests</c>) launch
/// <c>BotNexus.Cli.exe</c>, which the literal never leased. That binary
/// therefore prompted, and answering the prompt created an ungrouped
/// <c>TCP Query User{GUID}</c> rule - the rule class the prune could not
/// reclaim, hence the monotonic accumulation.
/// </para>
/// <para>
/// The fence carries anti-vacuity pins: it asserts the files it depends on
/// actually exist and are non-trivial, so it cannot pass by reading nothing.
/// </para>
/// </remarks>
public sealed class FirewallLeaseDerivationArchitectureTests
{
    private const string LeaseScript = "scripts/repo/Ensure-TesthostFirewallRules.ps1";
    private const string DerivationScript = "scripts/repo/FirewallLeaseProgram.ps1";
    private const string DerivationTests = "scripts/repo/FirewallLeaseProgram.Tests.ps1";
    private const string PruneScript = "scripts/repo/FirewallRulePrune.ps1";
    private const string PruneTests = "scripts/repo/FirewallRulePrune.Tests.ps1";
    private const string ReclaimScript = "scripts/repo/Invoke-FirewallRuleReclaim.ps1";

    /// <summary>
    /// Matches composing an output path that ends in the literal
    /// <c>testhost.exe</c>, i.e. the pre-#2774 derivation.
    /// </summary>
    private static readonly Regex ComposedTesthostLiteral = new(
        @"Join-Path[^\r\n]*['""]testhost\.exe['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void LeaseScript_DoesNotComposeTheTesthostLiteral()
    {
        var content = ReadRepoFile(LeaseScript);

        ComposedTesthostLiteral.IsMatch(content).ShouldBeFalse(
            $"{LeaseScript} composes a hard-coded 'testhost.exe' output path (issue #2774 clause 2). " +
            "The leased program set must come from the project's build output via " +
            $"{DerivationScript}, so that binaries a fixture spawns - notably BotNexus.Cli.exe - " +
            "are covered without anyone remembering to add them. " +
            "The only permitted use of the literal is the unbuilt-project fallback inside " +
            $"{DerivationScript}.");
    }

    [Fact]
    public void LeaseScript_DotSourcesTheDerivationHelper()
    {
        var content = ReadRepoFile(LeaseScript);

        content.ShouldContain(
            "FirewallLeaseProgram.ps1",
            Case.Sensitive,
            $"{LeaseScript} must dot-source {DerivationScript} and call Get-LeasedProgramPath; " +
            "otherwise the derivation is dead code and the lease silently narrows again.");

        content.ShouldContain(
            "Get-LeasedProgramPath",
            Case.Sensitive,
            $"{LeaseScript} must call Get-LeasedProgramPath to build its candidate path set.");
    }

    [Fact]
    public void DerivationHelper_KeepsTheLiteralOnlyAsAnUnbuiltFallback()
    {
        var content = ReadRepoFile(DerivationScript);

        content.ShouldContain(
            "Get-LeasedProgramPath",
            Case.Sensitive,
            $"{DerivationScript} must expose Get-LeasedProgramPath.");

        // Exactly one literal occurrence: the documented fallback for a project
        // that has not been built yet. More than one means the derivation has
        // started special-casing the binary again.
        var occurrences = Regex.Matches(content, @"['""]testhost\.exe['""]", RegexOptions.IgnoreCase).Count;
        occurrences.ShouldBe(
            1,
            $"{DerivationScript} should reference the 'testhost.exe' literal exactly once - " +
            "as the fallback used when the output directory does not exist yet. " +
            $"Found {occurrences} occurrence(s), which suggests the derivation is special-casing " +
            "the binary rather than enumerating build output.");
    }

    /// <summary>
    /// Anti-vacuity: every file this fence reasons about must exist and be
    /// substantial. A deleted or emptied script would otherwise make the
    /// assertions above pass for the wrong reason.
    /// </summary>
    [Theory]
    [InlineData(LeaseScript, 2000)]
    [InlineData(DerivationScript, 1000)]
    [InlineData(DerivationTests, 1000)]
    [InlineData(PruneScript, 1000)]
    [InlineData(PruneTests, 1000)]
    [InlineData(ReclaimScript, 1000)]
    public void RequiredFirewallScript_ExistsAndIsNonTrivial(string relativePath, int minimumLength)
    {
        var absolute = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(absolute).ShouldBeTrue(
            $"{relativePath} is missing. Issue #2774's fix depends on it; deleting it would make " +
            "this fence pass vacuously while the firewall rules accumulate again.");

        File.ReadAllText(absolute).Length.ShouldBeGreaterThan(
            minimumLength,
            $"{relativePath} is suspiciously short - it may have been gutted.");
    }

    /// <summary>
    /// The prune's narrowness contract (clause 4) must stay asserted by name.
    /// An over-broad firewall prune on a developer machine is worse than the
    /// bug it fixes, so the negative assertions are the load-bearing ones.
    /// </summary>
    [Theory]
    [InlineData("AC4: rule outside repo roots must survive the prune")]
    [InlineData("AC4: rules whose program path still exists on disk must survive the prune.")]
    [InlineData("AC4: a lease rule owned by a live process must survive.")]
    [InlineData("AC3: ungrouped TCP Query User rule for a nonexistent in-root path must be pruned.")]
    public void PruneTests_RetainTheNarrownessAssertions(string assertionMessage)
    {
        ReadRepoFile(PruneTests).ShouldContain(
            assertionMessage,
            Case.Sensitive,
            $"{PruneTests} no longer carries the assertion \"{assertionMessage}\". " +
            "Issue #2774 clause 4 requires the prune's narrowness to be asserted explicitly; " +
            "clause 6 requires clause 3's assertion to be identifiable by name.");
    }

    /// <summary>
    /// Clause 2's assertion must name <c>BotNexus.Cli.exe</c> explicitly. The
    /// issue asks for that binary by name because it is the one a fixture
    /// actually spawns.
    /// </summary>
    [Fact]
    public void DerivationTests_AssertBotNexusCliExeByName()
    {
        ReadRepoFile(DerivationTests).ShouldContain(
            "AC2: BotNexus.Cli.exe must be in the derived lease set for BotNexus.Cli.Tests.",
            Case.Sensitive,
            $"{DerivationTests} must assert BotNexus.Cli.exe is in the derived lease set for " +
            "BotNexus.Cli.Tests - issue #2774 clause 2 names that binary explicitly.");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var absolute = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(absolute).ShouldBeTrue($"Expected {relativePath} to exist at {absolute}.");
        return File.ReadAllText(absolute);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current.FullName;
    }
}
