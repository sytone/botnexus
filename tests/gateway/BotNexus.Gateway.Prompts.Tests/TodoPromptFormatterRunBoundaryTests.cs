using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Agenda-vs-minutes separation for recurring runs (#2984).
/// </summary>
/// <remarks>
/// A recurring cron conversation is a standup. Items an EARLIER run completed are that meeting's
/// minutes; re-injecting them as live <c>[x]</c> agenda entries told a fresh run its work was already
/// done, which produced four consecutive zero-tool-call runs that reported the previous run's outcomes
/// as their own (2026-08-11). Open items must still carry forward - that is "what is next".
/// </remarks>
public sealed class TodoPromptFormatterRunBoundaryTests
{
    private static readonly DateTimeOffset PriorRun = new(2026, 8, 11, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThisRun = new(2026, 8, 11, 21, 0, 0, TimeSpan.Zero);

    private static string Item(string text, string status, DateTimeOffset updatedAt)
        => $$"""{ "text": "{{text}}", "status": "{{status}}", "updatedAt": "{{updatedAt:o}}" }""";

    private static string Doc(params string[] items) => $$"""{ "items": [ {{string.Join(",", items)}} ] }""";

    [Fact]
    public void BuildSection_DoneByEarlierRun_IsNotRenderedAsLiveAgendaItem()
    {
        var json = Doc(Item("CI/PR status: 13 open PRs, 12 rebased", "done", PriorRun));

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json, ThisRun));

        // The exact shape that caused the incident: a completed outcome presented as a ticked agenda item.
        joined.ShouldNotContain("[x] CI/PR status: 13 open PRs, 12 rebased");
        // It is still visible as context, so the run is not made amnesiac about the standing work.
        joined.ShouldContain(TodoPromptFormatter.PriorRunHeading);
        joined.ShouldContain("CI/PR status: 13 open PRs, 12 rebased");
    }

    [Fact]
    public void BuildSection_CancelledByEarlierRun_IsAlsoDemoted()
    {
        var json = Doc(Item("abandoned approach", "cancelled", PriorRun));

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json, ThisRun));

        joined.ShouldNotContain("[-] abandoned approach");
        joined.ShouldContain(TodoPromptFormatter.PriorRunHeading);
    }

    [Fact]
    public void BuildSection_OpenItemsFromEarlierRun_CarryForwardAsAgenda()
    {
        var json = Doc(
            Item("finish the migration", "pending", PriorRun),
            Item("chase the flaky test", "in_progress", PriorRun));

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json, ThisRun));

        // "What is next" must survive the run boundary or the standup is useless.
        joined.ShouldContain(TodoPromptFormatter.SectionHeading);
        joined.ShouldContain("[ ] finish the migration");
        joined.ShouldContain("[~] chase the flaky test");
        joined.ShouldNotContain(TodoPromptFormatter.PriorRunHeading);
    }

    [Fact]
    public void BuildSection_DoneWithinTheCurrentRun_StaysInTheAgenda()
    {
        // Progress made by THIS run is current context, not minutes - the durable within-run spine.
        var json = Doc(Item("auth + sync main", "done", ThisRun.AddMinutes(3)));

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json, ThisRun));

        joined.ShouldContain("[x] auth + sync main");
        joined.ShouldNotContain(TodoPromptFormatter.PriorRunHeading);
    }

    /// <summary>
    /// Non-regression for the feature the tool exists to provide: the todo tool's contract promises the
    /// checklist "survives context compaction, interruption, and session continuation". Within one unit of
    /// work the list must render in full, done items included, or this fix has traded one bug for another.
    /// </summary>
    [Fact]
    public void BuildSection_WithinOneUnitOfWork_SurvivesSessionContinuationInFull()
    {
        var json = Doc(
            Item("step one", "done", ThisRun.AddMinutes(1)),
            Item("step two", "in_progress", ThisRun.AddMinutes(2)),
            Item("step three", "pending", ThisRun.AddMinutes(2)));

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json, ThisRun));

        joined.ShouldContain("[x] step one");
        joined.ShouldContain("[~] step two");
        joined.ShouldContain("[ ] step three");
        joined.ShouldNotContain(TodoPromptFormatter.PriorRunHeading);
    }

    [Fact]
    public void BuildSection_WithoutRunBoundary_RendersExactlyAsBefore()
    {
        // Every interactive caller passes no boundary; their rendering must be byte-identical.
        var json = Doc(
            Item("done thing", "done", PriorRun),
            Item("open thing", "pending", PriorRun));

        var withoutBoundary = TodoPromptFormatter.BuildSection(json);
        var legacyOverload = TodoPromptFormatter.BuildSection(json, runStartedAt: null);

        withoutBoundary.ShouldBe(legacyOverload);
        string.Join('\n', withoutBoundary).ShouldContain("[x] done thing");
        string.Join('\n', withoutBoundary).ShouldNotContain(TodoPromptFormatter.PriorRunHeading);
    }

    [Fact]
    public void BuildSection_ItemWithNoUpdatedAt_IsTreatedAsCurrentNotHidden()
    {
        // Legacy/malformed payloads keep their existing rendering rather than silently vanishing.
        var json = """{ "items": [ { "text": "legacy item", "status": "done" } ] }""";

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json, ThisRun));

        joined.ShouldContain("[x] legacy item");
        joined.ShouldNotContain(TodoPromptFormatter.PriorRunHeading);
    }

    /// <summary>
    /// The full 2026-08-11 incident shape: five outcome-worded items completed by the 17:00 run plus one
    /// in-progress "Report to Jon", handed to the 21:00 run. The prompt must not present the five as this
    /// run's completed work, and the single open item must remain.
    /// </summary>
    [Fact]
    public void BuildSection_TheIncidentShape_DoesNotPresentPriorWorkAsThisRunsOwn()
    {
        var json = Doc(
            Item("Auth + sync main + claim sweep (8 claimed, 3 released)", "done", PriorRun),
            Item("CI/PR status: 13 open PRs all passing; 12 rebased, 12 pushed", "done", PriorRun),
            Item("Housekeeping: 2 merged worktrees+branches removed (2788, 2839)", "done", PriorRun),
            Item("Triage: 209 open, 2 priority-backfilled, aged sweep no-op", "done", PriorRun),
            Item("Dispatched 3 conversations: #2961, #2323, #2865", "done", PriorRun),
            Item("Report to Jon", "in_progress", PriorRun));

        var lines = TodoPromptFormatter.BuildSection(json, ThisRun);
        var joined = string.Join('\n', lines);

        // Not one of the five may appear as a ticked agenda ENTRY. Asserted per-item on the rendered
        // item lines only: the advisory line legitimately contains the literal "[x]" while explaining
        // that narration cannot flip a box, so a blanket scan of the whole section would match
        // instruction text and pass/fail for the wrong reason.
        var itemLines = lines.Where(static l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        itemLines.ShouldNotContain(l => l.StartsWith("- [x]", StringComparison.Ordinal));
        // The open item survives as the agenda.
        joined.ShouldContain("[~] Report to Jon");
        // And the prior work is restated as context, explicitly not credited to this run.
        joined.ShouldContain(TodoPromptFormatter.PriorRunHeading);
        joined.ShouldContain("Dispatched 3 conversations: #2961, #2323, #2865");
        joined.ShouldContain("not by this one");
    }

    [Fact]
    public void BuildSection_AllItemsAreEarlierRunMinutes_RendersNoAgendaSection()
    {
        // The precise trap: nothing left to advance. The run must not be shown a fully ticked checklist.
        var json = Doc(
            Item("everything", "done", PriorRun),
            Item("also everything", "done", PriorRun));

        var lines = TodoPromptFormatter.BuildSection(json, ThisRun);
        var joined = string.Join('\n', lines);

        joined.ShouldNotContain(TodoPromptFormatter.SectionHeading);
        // Per-item assertion for the same reason as the incident-shape test: the advisory line carries
        // a literal "[x]", so only rendered item lines are meaningful evidence here.
        lines.ShouldNotContain(l => l.StartsWith("- [x]", StringComparison.Ordinal));
        joined.ShouldContain(TodoPromptFormatter.PriorRunHeading);
    }
}
