namespace BotNexus.Architecture.Tests;

/// <summary>
/// Pins the documentation-impact workflow to the source surfaces it evaluates. The job runs on
/// every pull request and exits early for unrelated changes, avoiding both the original source-only
/// blind spot and a permanently pending required check on unrelated pull requests.
/// </summary>
public sealed class DocsImpactWorkflowArchitectureTests : ArchitectureTest
{
    private string WorkflowPath =>
        Repository.Path(".github", "workflows", "docs-impact.yml");

    [Fact]
    public void PullRequestTrigger_DoesNotFilterOutSourceOnlyChanges()
    {
        var trigger = ReadPullRequestTrigger(File.ReadAllText(WorkflowPath));

        trigger.ShouldNotContain("paths:",
            customMessage: "The docs-impact workflow must start for source-only pull requests. " +
            "Filtering the enclosing workflow recreates the unreachable job from issue #4090.");
        trigger.ShouldContain("edited",
            customMessage: "Editing the PR body to add a no-docs-impact justification must rerun the check.");
    }

    [Theory]
    [InlineData("*botnexus-extension.json")]
    [InlineData("*IApiProvider*")]
    [InlineData("Controller\\.cs$")]
    public void DocsImpactPredicate_IncludesEveryDocumentedSourceSurface(string requiredPredicate)
    {
        var workflow = File.ReadAllText(WorkflowPath);

        workflow.ShouldContain(requiredPredicate,
            customMessage: $"The docs-impact predicate must retain {requiredPredicate}; removing " +
            "one documented source surface silently drops its documentation-impact signal.");
    }

    [Fact]
    public void DocsImpactJob_RetainsFailureAndBothSatisfyingOutcomes()
    {
        var workflow = File.ReadAllText(WorkflowPath);

        workflow.ShouldContain("$touchedDocs.Count -gt 0",
            customMessage: "A matching docs change must continue to satisfy docs-impact.");
        workflow.ShouldContain("no-docs-impact",
            customMessage: "An explicit no-docs-impact justification must remain supported.");
        workflow.ShouldContain("exit 1",
            customMessage: "A sensitive source-only change without either outcome must fail.");
    }

    [Fact]
    public void RegressionPin_RejectsTheOriginalFilteredTrigger()
    {
        const string originalWorkflow = """
            on:
              pull_request:
                branches: [main]
                paths:
                  - 'docs/**'
                  - 'scripts/repo/docs-lint.ps1'
                  - '.github/workflows/docs-lint.yml'
              push:
                branches: [main]
            """;

        var trigger = ReadPullRequestTrigger(originalWorkflow);

        trigger.ShouldContain("paths:",
            customMessage: "Sanity: this fixture must retain the filtered trigger from issue #4090.");
        trigger.ShouldNotContain("*botnexus-extension.json");
        trigger.ShouldNotContain("*IApiProvider*");
        trigger.ShouldNotContain("Controller\\.cs$");
    }

    private static string ReadPullRequestTrigger(string workflow)
    {
        var normalized = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        var pullRequestStart = normalized.IndexOf("  pull_request:\n", StringComparison.Ordinal);
        pullRequestStart.ShouldBeGreaterThanOrEqualTo(0,
            "The docs-impact workflow must retain a pull_request trigger.");

        var nextEventStart = normalized.IndexOf("  workflow_dispatch:", pullRequestStart, StringComparison.Ordinal);
        if (nextEventStart < 0)
            nextEventStart = normalized.IndexOf("  push:\n", pullRequestStart, StringComparison.Ordinal);

        nextEventStart.ShouldBeGreaterThan(pullRequestStart,
            "The pull_request trigger must be bounded by the following workflow event.");
        return normalized[pullRequestStart..nextEventStart];
    }
}
