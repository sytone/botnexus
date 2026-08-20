using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for <c>#2677</c>: there must be exactly <b>one</b> definition of
/// "which <see cref="SubAgentStatus"/> values are terminal", namely
/// <see cref="SubAgentStatusPolicy"/>.
/// <para>
/// The defect this fence guards was not a missing value, it was a duplicated decision.
/// <c>SubAgentWorkspaceReaper</c> held a four-element <c>HashSet&lt;string&gt;</c> and
/// <c>DefaultSubAgentManager</c> held a five-value <c>or</c>-pattern. #2656 added
/// <c>BudgetExhausted</c> and updated only the second, so budget-exhausted workspaces were
/// classified <c>Running</c> forever and no prune path reclaimed them. Because the reaper's copy
/// was keyed on <i>strings</i>, no compiler diagnostic could ever have caught it.
/// </para>
/// <para>
/// This fence fails if a second such list is reintroduced anywhere under <c>src/</c>: any file
/// other than the policy itself that enumerates three or more terminal status names in one
/// place - whether as string literals or as <c>SubAgentStatus.X</c> members - is a re-emerging
/// hand-maintained list and must call <see cref="SubAgentStatusPolicy.IsTerminal"/> instead.
/// </para>
/// <para>
/// <b>Vacuity.</b> The scan is asserted to have found a plausible number of files, the policy
/// file itself is asserted to exist and to be the sole exemption, and the detector is pinned with
/// positive and negative samples so a regex that matches nothing cannot masquerade as coverage.
/// </para>
/// </summary>
public sealed class SubAgentTerminalStatusFenceArchitectureTests
{
    /// <summary>
    /// The one file permitted to enumerate terminal sub-agent statuses: the shared predicate.
    /// Anything else that does so is by definition a second, drift-prone copy.
    /// </summary>
    private const string PolicySource =
        "src/domain/BotNexus.Domain/Gateway/Models/SubAgentStatusPolicy.cs";

    /// <summary>
    /// The status names whose co-occurrence marks a terminal-status list. Deliberately excludes
    /// <c>Running</c>: a file mentioning only the live state is not making a terminal decision.
    /// </summary>
    private static readonly string[] TerminalNames =
    [
        "Completed",
        "Failed",
        "Killed",
        "TimedOut",
        "BudgetExhausted"
    ];

    /// <summary>
    /// How many distinct terminal names in a single file constitute a "list". Two can be an
    /// ordinary two-way branch; three or more in one file is a hand-maintained enumeration.
    /// </summary>
    private const int ListThreshold = 3;

    /// <summary>
    /// The primary fence. Scans every C# file under <c>src/</c> and fails if any file other than
    /// the policy enumerates <see cref="ListThreshold"/> or more terminal status names.
    /// </summary>
    [Fact]
    public void NoSecondTerminalSubAgentStatusList_ExistsUnderSrc()
    {
        var sourceFiles = EnumerateSourceFiles();

        // Anti-vacuity: a scan that walked an empty or wrong tree is green for the wrong reason.
        sourceFiles.Count.ShouldBeGreaterThan(
            200,
            $"Only {sourceFiles.Count} C# files were found under src/. The fence is scanning the "
            + "wrong tree and cannot be trusted.");

        var policyPath = ResolvePath(PolicySource);
        File.Exists(policyPath).ShouldBeTrue(
            $"{PolicySource} not found. The #2677 fence exempts exactly one file - the shared "
            + "predicate. If it moved, update PolicySource so the fence keeps guarding something.");

        var offenders = new List<string>();
        foreach (var file in sourceFiles)
        {
            if (string.Equals(file, policyPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var found = FindTerminalNames(File.ReadAllText(file));
            if (found.Count >= ListThreshold)
                offenders.Add($"{Relative(file)} ({string.Join(", ", found.Order(StringComparer.Ordinal))})");
        }

        offenders.ShouldBeEmpty(
            "A second hand-maintained list of terminal SubAgentStatus values has been "
            + "reintroduced. This is the exact defect of #2677: two independent definitions "
            + "drifted when #2656 added BudgetExhausted, and the reaper silently stopped "
            + "reclaiming budget-exhausted workspaces. Call "
            + "SubAgentStatusPolicy.IsTerminal(SubAgentStatus) (or IsTerminalStatusName for "
            + "persisted text) instead of re-enumerating the values.\n"
            + "Offending files:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Anti-vacuity self-test: the shared predicate must classify <b>every</b> declared
    /// <see cref="SubAgentStatus"/> member explicitly. If it did not, the fence would be
    /// guarding a predicate that itself silently defaults some state.
    /// <para>
    /// Checked by name rather than through <c>FindTerminalNames</c>, because the policy is a
    /// switch expression with one arm per member - deliberately NOT the <c>or</c>-chain or
    /// string-collection shape the detector hunts for. That is the point: the one permitted
    /// definition does not look like a hand-maintained list.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePolicy_ClassifiesEveryDeclaredStatusExplicitly()
    {
        var policy = File.ReadAllText(ResolvePath(PolicySource));

        foreach (var name in Enum.GetNames<SubAgentStatus>())
        {
            policy.ShouldContain(
                $"SubAgentStatus.{name}",
                Case.Sensitive,
                $"{name} is a declared SubAgentStatus but the shared predicate does not name it. "
                + "Every member must be classified explicitly - a status that falls through is "
                + "exactly how #2656's BudgetExhausted went unnoticed.");
        }
    }

    /// <summary>
    /// Anti-vacuity self-test (positive): both shapes the two original lists used - bare string
    /// literals and <c>SubAgentStatus.X</c> member access - must be detected.
    /// </summary>
    [Fact]
    public void Detector_Recognises_BothStringAndEnumListShapes()
    {
        const string stringShape = """
            new(StringComparer.OrdinalIgnoreCase) { "Completed", "Failed", "Killed", "TimedOut" };
            """;
        const string enumShape = """
            if (info.Status is SubAgentStatus.Completed or SubAgentStatus.Failed or SubAgentStatus.Killed)
            """;

        FindTerminalNames(stringShape).Count.ShouldBeGreaterThanOrEqualTo(ListThreshold);
        FindTerminalNames(enumShape).Count.ShouldBeGreaterThanOrEqualTo(ListThreshold);
    }

    /// <summary>
    /// Anti-vacuity self-test (negative): a file that simply calls the shared predicate, or
    /// mentions one status in passing, must not be reported. A detector that over-matches would
    /// make the fence unshippable and it would be deleted rather than fixed.
    /// </summary>
    [Fact]
    public void Detector_DoesNotReport_CallersOfTheSharedPredicate()
    {
        const string goodCaller = """
            if (SubAgentStatusPolicy.IsTerminal(info.Status))
                return false;
            record.Status = SubAgentStatus.Completed;
            """;

        FindTerminalNames(goodCaller).Count.ShouldBeLessThan(
            ListThreshold,
            "Consuming the shared predicate must never trip the fence.");
    }

    /// <summary>
    /// Anti-vacuity self-test (negative, and the reason this detector keys on decision SHAPE):
    /// naming several statuses without deciding terminality must NOT be reported.
    /// <para>
    /// The first version of this fence counted distinct names anywhere in a file. It flagged
    /// <c>DefaultSubAgentManager</c> even after that file had been correctly converted to call
    /// the shared predicate, because its <c>DescribeStatus</c> display mapping and its
    /// per-status assignment sites name five statuses between them. Both samples below are drawn
    /// from that real false positive.
    /// </para>
    /// </summary>
    [Fact]
    public void Detector_DoesNotReport_DisplayMappingOrAssignmentSites()
    {
        const string displayMapping = """
            private static string DescribeStatus(SubAgentStatus status)
                => status switch
                {
                    SubAgentStatus.Completed => "completed",
                    SubAgentStatus.Failed => "failed",
                    SubAgentStatus.TimedOut => "timed out",
                    SubAgentStatus.BudgetExhausted => "exhausted its turn budget",
                    SubAgentStatus.Killed => "was killed",
                    _ => "updated"
                };
            """;

        const string assignmentSites = """
            => CompleteTerminalAsync(subAgentId, SubAgentStatus.BudgetExhausted, diagnostic);
            => CompleteTerminalAsync(subAgentId, SubAgentStatus.TimedOut, diagnostic);
            => CompleteTerminalAsync(subAgentId, SubAgentStatus.Failed, diagnostic);
            Status = SubAgentStatus.Killed,
            if (updated.Status == SubAgentStatus.Completed)
            """;

        FindTerminalNames(displayMapping).ShouldBeEmpty(
            "Mapping each status to a display string is not a terminality decision.");
        FindTerminalNames(assignmentSites).ShouldBeEmpty(
            "Setting or comparing an individual status is not a terminality decision.");
    }

    /// <summary>
    /// Pins that the two sites named in #2677 actually consume the shared predicate, so the
    /// fence cannot pass merely because both lists were deleted and nothing replaced them.
    /// </summary>
    [Theory]
    [InlineData("src/gateway/BotNexus.Cli/Commands/SubAgentWorkspaceReaper.cs")]
    [InlineData("src/gateway/BotNexus.Gateway/Agents/DefaultSubAgentManager.cs")]
    public void BothOriginalSites_ConsumeTheSharedPredicate(string relativePath)
    {
        var path = ResolvePath(relativePath);
        File.Exists(path).ShouldBeTrue($"{relativePath} not found; update this fence.");

        File.ReadAllText(path).ShouldContain(
            "SubAgentStatusPolicy.IsTerminal",
            Case.Sensitive,
            $"{relativePath} was one of the two sites that independently defined 'terminal "
            + "SubAgentStatus' (#2677). It must consume the shared predicate.");
    }

    /// <summary>
    /// Finds the terminal status names participating in a <b>terminality decision</b> in
    /// <paramref name="text"/>. Returns empty when no such decision is present, even if the text
    /// names every status individually.
    /// <para>
    /// Two shapes are recognised, being exactly the two the original duplicated definitions used:
    /// an <c>is</c>/<c>or</c> pattern chain over <c>SubAgentStatus</c> members (as
    /// <c>DefaultSubAgentManager</c> had), and a run of bare status string literals in one
    /// collection (as <c>SubAgentWorkspaceReaper</c>'s <c>HashSet&lt;string&gt;</c> had).
    /// </para>
    /// <para>
    /// Keying on decision SHAPE rather than on file-wide co-occurrence is load-bearing. The first
    /// version of this fence counted distinct names anywhere in a file and reported
    /// <c>DefaultSubAgentManager</c> even after it had been converted to call the shared
    /// predicate - its <c>DescribeStatus</c> display mapping and its per-status assignment sites
    /// name five statuses between them. A fence that flags a file for correctly consuming the
    /// predicate gets deleted rather than fixed.
    /// </para>
    /// <para>
    /// The string alternative is deliberately NOT <c>\b</c>-anchored after the closing quote: a
    /// quote and a following comma are both non-word characters, so <c>\b</c> there can never
    /// match and would silently disable the entire string-literal branch.
    /// </para>
    /// </summary>
    private static IReadOnlyCollection<string> FindTerminalNames(string text)
    {
        var names = string.Join("|", TerminalNames);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var chain = $@"SubAgentStatus\.({names})\b(?:\s+or\s+SubAgentStatus\.({names})\b){{{ListThreshold - 1}}}";
        var literalRun = $@"""({names})""(?:\s*,\s*""({names})""){{{ListThreshold - 1}}}";
        foreach (var pattern in new[] { chain, literalRun })
        {
            foreach (Match match in Regex.Matches(text, pattern))
            {
                foreach (Capture capture in match.Groups[1].Captures)
                    found.Add(capture.Value);
                foreach (Capture capture in match.Groups[2].Captures)
                    found.Add(capture.Value);
            }
        }
        return found;
    }

    private static IReadOnlyList<string> EnumerateSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static string Relative(string absolute) =>
        Path.GetRelativePath(FindRepoRoot(), absolute).Replace('\\', '/');

    private static string ResolvePath(string relative) =>
        Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        current.ShouldNotBeNull("Could not locate repo root (Directory.Packages.props) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
