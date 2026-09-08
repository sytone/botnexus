using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function pinning the stale-base merge gate from issue #3173.
///
/// On 2026-08-14 <c>main</c> went red without any individual pull request being wrong. #3148
/// added the #3099 primitive-ID fence at 07:44:32 and #3164 added a file violating it at
/// 07:48:20. Neither branch contained the other's commit, so both <c>core-tests</c> results
/// were correct for their own bases, and the first tree that ever contained both the rule and
/// the violation was <c>main</c> itself (run 31811335987).
///
/// The defect class is structural and applies to every <b>tree-wide</b> rule - this fence,
/// <c>ConfigFieldCoverage</c>, <c>CoreTestScopeConsistency</c> - because such a rule and a
/// violation of it can travel in separate commits. Per-file unit tests are immune, since a file
/// and its test move together.
///
/// <c>.github/workflows/ci-base-freshness.yml</c> closes the hole by merging the current tip of
/// <c>main</c> into the PR head and re-running the architecture suite against that merged tree.
/// This fence exists because that workflow is exactly the kind of guard that gets deleted or
/// quietly defanged during an unrelated CI cleanup, at which point the failure mode returns
/// silently and is only rediscovered the next time <c>main</c> breaks.
/// </summary>
/// <remarks>
/// Assertions are shape-level on purpose: they require the job, the merge-with-main step, the
/// architecture-test invocation, the inherited-vs-introduced classification and the main-health
/// probe's event-driven trigger to all still be present. They deliberately do NOT pin exact
/// commands, runner images, or the schedule interval, which are maintainer tuning decisions.
/// Anti-vacuity is asserted first: a scanner that cannot find the file must fail loudly rather
/// than pass by finding nothing.
/// </remarks>
public sealed class BaseFreshnessGateArchitectureTests : ArchitectureTest
{
    private const string WorkflowFileName = "ci-base-freshness.yml";

    private const string DocFileName = "stale-base-merges.md";


    private string WorkflowPath =>
        Path.Combine(Repository.Root, ".github", "workflows", WorkflowFileName);

    private string DocPath =>
        Path.Combine(Repository.Root, "docs", "development", DocFileName);

    [Fact]
    public void BaseFreshnessWorkflow_Exists()
    {
        File.Exists(WorkflowPath).ShouldBeTrue(
            $"The stale-base merge gate workflow is missing: {WorkflowPath}. Without it, a PR " +
            "green against a stale base can land a tree-wide architecture violation on `main` - " +
            "the #3148/#3164 race that reddened `main` on 2026-08-14. See issue #3173. If this " +
            "gate was deliberately replaced (for example by a GitHub merge queue), delete this " +
            "fence in the same change and say so, rather than leaving an unenforced guard.");
    }

    [Fact]
    public void BaseFreshnessWorkflow_IsNotEmpty()
    {
        var text = ReadWorkflow();

        text.Length.ShouldBeGreaterThan(500,
            "Anti-vacuity: the base-freshness workflow is present but implausibly small " +
            $"({text.Length} chars). A truncated or stubbed workflow passes every content check " +
            "below without gating anything. See issue #3173.");
    }

    [Fact]
    public void BaseFreshnessWorkflow_RunsOnPullRequestsTargetingMain()
    {
        var text = ReadWorkflow();

        text.ShouldContain("pull_request",
            customMessage: "The gate must run on pull requests - that is the only point at which a " +
            "stale base can still be corrected before it lands. See issue #3173.");
        text.ShouldContain("branches: [ main ]",
            customMessage: "The gate must target `main`, the branch whose health the tree-wide " +
            "rules describe. See issue #3173.");
    }

    [Fact]
    public void BaseFreshnessWorkflow_MergesCurrentMainBeforeEvaluating()
    {
        var text = ReadWorkflow();

        text.ShouldContain("origin/main",
            customMessage: "The gate must fetch the CURRENT tip of `main`. Evaluating the PR's " +
            "recorded base answers the wrong question: `core-tests` already does that, and it was " +
            "correctly green for both sides of the #3148/#3164 race. See issue #3173.");
        text.ShouldContain("git merge",
            customMessage: "The gate must construct the prospective MERGED tree and judge that, " +
            "not the branch in isolation. Without the merge step the workflow re-runs what " +
            "`core-tests` already ran and adds no guarantee. See issue #3173.");
    }

    [Fact]
    public void BaseFreshnessWorkflow_RunsTheArchitectureSuiteAgainstTheMergedTree()
    {
        var text = ReadWorkflow();

        text.ShouldContain("BotNexus.Architecture.Tests",
            customMessage: "The gate must run the architecture project against the merged tree. " +
            "That project is where every tree-wide rule lives - the primitive-ID fence (#3099), " +
            "ConfigFieldCoverage, CoreTestScopeConsistency - and tree-wide rules are precisely the " +
            "class a stale base can defeat. See issue #3173.");
    }

    [Fact]
    public void BaseFreshnessWorkflow_DistinguishesInheritedFromIntroducedFailures()
    {
        var text = ReadWorkflow();

        text.ShouldContain("INHERITED",
            customMessage: "AC4 of #3173: a PR that inherited a red `main` must be distinguishable " +
            "from one that introduced a failure, without manual log archaeology. The gate must " +
            "state the verdict in words in its step summary.");
        text.ShouldContain("INTRODUCED",
            customMessage: "AC4 of #3173: the gate must name the 'introduced' case explicitly too. " +
            "Reporting only the inherited case makes silence ambiguous, which is the state the " +
            "issue was filed to eliminate.");
    }

    [Fact]
    public void BaseFreshnessWorkflow_ProbesMainHealthOnASchedule()
    {
        var text = ReadWorkflow();

        text.ShouldContain("schedule",
            customMessage: "AC3 of #3173: a red `main` must surface within one gate cycle rather " +
            "than being discovered via an unrelated PR. That requires a scheduled probe - a " +
            "pull_request-only workflow can only report to whoever happens to open a PR.");
    }

    [Fact]
    public void MainHealthProbe_DoesNotDependOnCronDeliveryAlone()
    {
        var text = ReadWorkflow();

        text.ShouldContain("workflow_run",
            customMessage: "Issue #3715: `schedule` events are delivered best-effort and were " +
            "measured at 8% of the declared `*/15` rate over 213 h (68 runs against an expected " +
            "853, median gap 149 min, worst gap 11.4 h). A cron-only probe therefore advertises a " +
            "detection guarantee GitHub does not honour - during the 2026-08-30 incident `main` " +
            "sat red for ~90 h. The probe must be driven by an EVENT that fires when `main`'s " +
            "verdict actually exists, with cron demoted to a backstop. This fence deliberately " +
            "does not pin the cron interval, which remains a tuning decision; it pins only that a " +
            "non-cron trigger exists.");
        text.ShouldContain("CI: Build & Test",
            customMessage: "Issue #3715: the non-cron trigger must be the completion of the " +
            "workflow whose conclusion the probe reports, otherwise it fires at a moment when " +
            "`main`'s verdict is not yet available and reports the PREVIOUS commit's result.");
    }

    [Fact]
    public void MainHealthProbe_ReadsOnlyCompletedRuns()
    {
        var text = ReadWorkflow();

        text.ShouldContain("--status completed",
            customMessage: "Issue #3715: the probe is now triggered by `workflow_run`, so it can " +
            "race an in-progress run on `main`. `gh run list` without `--status completed` returns " +
            "the newest run regardless of state, whose `conclusion` is null while it is running - " +
            "which the probe would read as not-a-failure and report a red `main` as fine. " +
            "Filtering to completed runs is what makes the faster trigger safe.");
    }

    [Fact]
    public void Fence_IsNotVacuous_RejectsACronOnlyMainHealthProbe()
    {
        // Synthetic regression: the pre-#3715 shape. It has a `schedule` and therefore passes
        // the AC3 check above, yet its detection latency is whatever GitHub feels like
        // delivering - measured at 8% of the declared rate. This pins the #3715 detectors as
        // actually discriminating rather than matching text every workflow contains.
        const string cronOnlyYaml = """
            name: "CI: Base Freshness"
            on:
              pull_request:
                branches: [ main ]
              schedule:
                - cron: '*/15 * * * *'
            jobs:
              main-health:
                runs-on: ubuntu-latest
                steps:
                  - run: gh run list --workflow 'CI: Build & Test' --branch main --limit 1
            """;

        cronOnlyYaml.ShouldContain("schedule",
            customMessage: "Sanity: the synthetic pre-#3715 shape really is the cron-only shape.");
        cronOnlyYaml.ShouldNotContain("workflow_run",
            customMessage: "Vacuity guard: the cron-only workflow must NOT satisfy the #3715 " +
            "event-driven check. If it did, that detector would pass on the exact configuration " +
            "the issue was filed about.");
        cronOnlyYaml.ShouldNotContain("--status completed",
            customMessage: "Vacuity guard: the cron-only workflow must NOT satisfy the " +
            "completed-runs check.");
    }

    [Fact]
    public void StaleBaseMergeMechanism_IsDocumented()
    {
        File.Exists(DocPath).ShouldBeTrue(
            $"AC2 of #3173 requires the mechanism to be documented: {DocPath} is missing. A CI " +
            "guard whose rationale is not written down is deleted by the next person who finds it " +
            "slow, because nothing on the file says what it prevents.");

        var doc = File.ReadAllText(DocPath);

        doc.ShouldContain("tree-wide",
            customMessage: "AC2 of #3173: the documentation must name the rule CLASS being " +
            "protected, not just describe the incident. The incident was one instance; the class " +
            "is the reason the gate is permanent.");
        doc.ShouldContain("#3173",
            customMessage: "The documentation must cite the issue so a reader can recover the full " +
            "evidence trail (run 31811335987, commits 8fa88a1f and c97109df).");
    }

    // ---- non-vacuity pins: the detectors must reject the broken shape ----

    [Fact]
    public void Fence_IsNotVacuous_RejectsAWorkflowThatOnlyTestsTheBranch()
    {
        // Synthetic regression: the pre-#3173 shape. This is a perfectly ordinary CI workflow -
        // it builds and tests the PR head - and it is exactly what fails to prevent the bug,
        // because it never constructs the tree that will exist after the merge.
        const string brokenYaml = """
            name: "CI: Build & Test"
            on:
              pull_request:
                branches: [ main ]
            jobs:
              core-tests:
                runs-on: ubuntu-latest
                timeout-minutes: 45
                steps:
                  - uses: actions/checkout@v4
                  - run: dotnet test tests/dirs.proj
            """;

        brokenYaml.ShouldNotContain("origin/main",
            customMessage: "Vacuity guard: the synthetic pre-fix workflow must NOT satisfy the merged-tree check. " +
            "If it did, the detector would be matching something every workflow contains and the " +
            "whole fence would pass vacuously.");
        brokenYaml.ShouldNotContain("INHERITED",
            customMessage: "Vacuity guard: the synthetic pre-fix workflow must NOT satisfy the classification " +
            "check.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsAMergedTreeWorkflowShape()
    {
        // Synthetic positive: the minimum shape the fence intends to accept. This pins the
        // detectors as not over-tightened - they must pass on a compliant workflow that does not
        // happen to be byte-identical to the one in this repo.
        const string fixedYaml = """
            name: "CI: Base Freshness"
            on:
              pull_request:
                branches: [ main ]
              schedule:
                - cron: '*/15 * * * *'
            jobs:
              base-freshness:
                runs-on: ubuntu-latest
                timeout-minutes: 30
                steps:
                  - uses: actions/checkout@v4
                  - run: git fetch origin main && git merge --no-edit origin/main
                  - run: dotnet test tests/architecture/BotNexus.Architecture.Tests/BotNexus.Architecture.Tests.csproj
                  - run: echo "INHERITED or INTRODUCED verdict"
            """;

        fixedYaml.ShouldContain("origin/main");
        fixedYaml.ShouldContain("git merge");
        fixedYaml.ShouldContain("BotNexus.Architecture.Tests");
        fixedYaml.ShouldContain("INHERITED");
        fixedYaml.ShouldContain("INTRODUCED");
        fixedYaml.ShouldContain("schedule");
    }

    // ---- helpers ----

    private string ReadWorkflow()
    {
        File.Exists(WorkflowPath).ShouldBeTrue(
            $"Anti-vacuity: {WorkflowPath} not found, so every content assertion below would be " +
            "evaluated against an empty string. See issue #3173.");
        return File.ReadAllText(WorkflowPath);
    }

}
