using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Build-failing fence for issue #3073, acceptance criterion 3: there must be exactly ONE
/// cron-classification predicate in the Blazor client.
/// </summary>
/// <remarks>
/// <para>
/// #2327 extracted the picker grouping into <c>PortalConversationGrouping</c> but copied only the
/// FIRST of <c>MainLayout.IsCronConversation</c>'s two clauses, so the mobile picker mis-grouped 61
/// conversations that the desktop grouped correctly. Because <c>ConversationState.Source</c> is
/// write-once (#2304), the projection clause alone can never see a channel-created conversation that
/// a cron job later adopted; the cron-id map is the only signal that identifies it.
/// </para>
/// <para>
/// Behaviour tests prove the two form factors agree TODAY. They cannot prove HOW, and they all stay
/// green if a third surface hand-rolls the rule again - which is precisely the failure this issue
/// is. This fence pins the mechanism: the projection-vs-<c>Scheduled</c> comparison and the cron-id
/// set membership test may each appear in exactly one place, inside
/// <c>PortalConversationGrouping.IsScheduled</c>.
/// </para>
/// <para>
/// The scan strips comments first, or the fence fires on its own explanatory prose and on the
/// doc comments that legitimately name the rule (the #2813 / #2955 lesson).
/// </para>
/// </remarks>
public sealed class CronClassificationSingleSiteArchitectureTests
{
    private const string GroupingFile =
        "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/PortalConversationGrouping.cs";

    /// <summary>
    /// Clause 1 - the projection comparison - lives only in the shared helper. A second site would
    /// be a surface re-deriving "is this scheduled?" for itself, which is how mobile and desktop
    /// drifted in the first place.
    /// </summary>
    [Fact]
    public void ScheduledProjectionComparison_AppearsOnlyInTheSharedHelper()
    {
        var offenders = ClientSourceFiles()
            .Where(f => ScheduledProjectionProbe().IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Rel)
            .Order()
            .ToList();

        offenders.ShouldBe(
            [GroupingFile],
            "The `Group == ConversationListGroup.Scheduled` comparison must exist in exactly one " +
            "place - PortalConversationGrouping.IsScheduled - and every surface must call it. A " +
            "second site is a second cron classifier, and the projection clause ALONE is wrong: " +
            "ConversationState.Source is write-once (#2304), so a channel-created conversation later " +
            "adopted by a cron job is invisible to it forever. That is #3073: 61 conversations " +
            "grouped correctly on desktop and wrongly on mobile from the same inputs.\nSites: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Clause 2 - the cron-id set membership test - likewise lives only in the shared helper.
    /// Callers hold the set (the desktop caches it, mobile fetches it once) but must not test it.
    /// </summary>
    [Fact]
    public void CronConversationIdMembershipTest_AppearsOnlyInTheSharedHelper()
    {
        var offenders = ClientSourceFiles()
            .Where(f => CronIdMembershipProbe().IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Rel)
            .Order()
            .ToList();

        offenders.ShouldBe(
            [GroupingFile],
            "Membership in the cron-job -> conversation-id map may only be tested inside " +
            "PortalConversationGrouping.IsScheduled. A caller that tests it itself has re-created " +
            "half the classifier, which is exactly the split that let #2327's extraction ship with " +
            "one of the two clauses.\nSites: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Anti-vacuity. A fence whose detectors match nothing, or whose file scan resolves to an empty
    /// set, silently guards nothing - so assert the scan is real and both detectors recognise the
    /// exact shapes this issue is about while ignoring benign neighbours.
    /// </summary>
    [Fact]
    public void CronClassificationDetectors_AreNotVacuous()
    {
        ClientSourceFiles().Count.ShouldBeGreaterThan(
            50,
            "The scan should read the whole client tree; a near-empty file set means path " +
            "resolution broke and the fence stopped guarding anything.");

        ScheduledProjectionProbe()
            .IsMatch("c.Project(selectionSource).Group == ConversationListGroup.Scheduled")
            .ShouldBeTrue("Clause-1 detector must match the shipped comparison shape.");
        ScheduledProjectionProbe()
            .IsMatch("proj.Group == ConversationListGroup.Automated")
            .ShouldBeFalse("Clause-1 detector must not fire on the webhook group (#2122).");
        ScheduledProjectionProbe()
            .IsMatch("ConversationSource.Cron => ConversationListGroup.Scheduled,")
            .ShouldBeFalse(
                "Clause-1 detector must not fire on ConversationRenderProjection's own definition " +
                "of the mapping. That file DEFINES the group; the fence bans re-testing it.");

        CronIdMembershipProbe()
            .IsMatch("cronConversationIds.Contains(id)")
            .ShouldBeTrue("Clause-2 detector must match the shipped membership shape.");
        CronIdMembershipProbe()
            .IsMatch("_cronConversationIds = PortalConversationGrouping.CronConversationIds(jobs)")
            .ShouldBeFalse("Clause-2 detector must not fire on merely HOLDING the set.");

        // And the one legitimate site really is the helper, so the expected-single-site assertions
        // above cannot be satisfied by a scan that matches nothing at all.
        var helper = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), GroupingFile)));
        ScheduledProjectionProbe().IsMatch(helper).ShouldBeTrue(
            "PortalConversationGrouping must itself contain the projection comparison.");
        CronIdMembershipProbe().IsMatch(helper).ShouldBeTrue(
            "PortalConversationGrouping must itself contain the cron-id membership test.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Regex ScheduledProjectionProbe() => s_scheduledProjectionProbe;

    private static readonly Regex s_scheduledProjectionProbe = new(
        @"==\s*ConversationListGroup\s*\.\s*Scheduled", RegexOptions.Compiled);

    private static Regex CronIdMembershipProbe() => s_cronIdMembershipProbe;

    private static readonly Regex s_cronIdMembershipProbe = new(
        @"\bcronConversationIds\b\s*(?:is\s+\{[^}]*\}\s*\w+\s*)?\.\s*Contains\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IReadOnlyList<string> ClientSourceFiles()
    {
        var extensions = Path.Combine(RepoRoot(), "src", "extensions");
        return Directory
            .EnumerateDirectories(extensions, "BotNexus.Extensions.Channels.SignalR.BlazorClient*")
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private static string Rel(string file) =>
        Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

    private static string StripComments(string source)
    {
        var noRazor = Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
        var noBlock = Regex.Replace(noRazor, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
