using System.Text.Json;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// Behaviour tests for the three read tools added by #2734: <c>github_pr_checks</c>,
/// <c>github_pr_diff</c> and <c>github_workflow_runs</c>.
/// </summary>
/// <remarks>
/// As with the existing suite, every test drives the real
/// <c>PrepareArgumentsAsync</c> -> <c>ExecuteAsync</c> pair. The prepared dictionary is an
/// allow-list, so a schema argument that is never copied through is silently dropped - a defect that
/// yields a plausible answer for the wrong input, and only the full path catches it.
/// </remarks>
public sealed class GitHubReadToolsTests
{
    // ---- github_pr_checks ---------------------------------------------------------------------

    [Fact]
    public async Task PullRequestChecks_ResolvesTheHeadShaThenReadsCheckRuns()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Returns(GitHubFixtures.CheckRuns);
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        json.GetProperty("ok").GetBoolean().ShouldBeTrue();
        json.GetProperty("headSha").GetString().ShouldBe("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678");
        api.Calls[0].Path.ShouldBe("repos/Sytone/botnexus/pulls/3300");
        // Threading the SHA between the two calls is the whole point of the tool: under the shell
        // path this was two gh invocations plus a --jq filter to carry the value across.
        api.Calls[1].Path.ShouldStartWith(
            "repos/Sytone/botnexus/commits/a1b2c3d4e5f60718293a4b5c6d7e8f9012345678/check-runs");
    }

    [Fact]
    public async Task PullRequestChecks_ReturnsStructuredRunsReadableWithoutStringParsing()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Returns(GitHubFixtures.CheckRuns);
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        var runs = json.GetProperty("checkRuns").EnumerateArray().ToArray();
        runs.Length.ShouldBe(3);
        runs[0].GetProperty("name").GetString().ShouldBe("build");
        runs[0].GetProperty("conclusion").GetString().ShouldBe("success");
        runs[1].GetProperty("conclusion").GetString().ShouldBe("failure");
        // An in-flight run has NO conclusion. Substituting "pending" here would let a caller read a
        // red PR as merely slow, which is the distinction this null preserves.
        runs[2].GetProperty("conclusion").ValueKind.ShouldBe(JsonValueKind.Null);
        runs[2].GetProperty("status").GetString().ShouldBe("in_progress");
    }

    [Fact]
    public async Task PullRequestChecks_RollsUpASummaryTheAgentCanActOnDirectly()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Returns(GitHubFixtures.CheckRuns);
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var summary = (await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 }))
            .GetProperty("summary");

        summary.GetProperty("total").GetInt32().ShouldBe(3);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        summary.GetProperty("pending").GetInt32().ShouldBe(1);
        // "Green" is not "no failures": an unfinished run set also has zero failures.
        summary.GetProperty("allCompleted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PullRequestChecks_WhenEveryRunHasCompleted_ReportsAllCompleted()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Returns("""{"total_count":1,"check_runs":[{"id":1,"name":"build","status":"completed","conclusion":"success"}]}""");
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var summary = (await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 }))
            .GetProperty("summary");

        summary.GetProperty("allCompleted").GetBoolean().ShouldBeTrue();
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task PullRequestChecks_ReportsPageBoundsAndAContinuationSignal()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Returns(GitHubFixtures.CheckRuns);
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300, ["perPage"] = 3 });

        json.GetProperty("page").GetInt32().ShouldBe(1);
        json.GetProperty("perPage").GetInt32().ShouldBe(3);
        json.GetProperty("count").GetInt32().ShouldBe(3);
        json.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        api.Calls[1].Path.ShouldContain("per_page=3");
    }

    [Fact]
    public async Task PullRequestChecks_WhenPerPageExceedsTheBound_ClampsAndSaysSo()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Returns("""{"total_count":0,"check_runs":[]}""");
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config(maxPageSize: 10));

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300, ["perPage"] = 500 });

        json.GetProperty("perPage").GetInt32().ShouldBe(10);
        json.GetProperty("perPageClamped").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task PullRequestChecks_WhenThePullRequestReadFails_ReportsThatFailureNotAnEmptyCheckSet()
    {
        // Returning "no checks" for a PR that could not be read would be indistinguishable from a
        // genuinely unchecked PR - the collapsed-error-state defect this project keeps re-learning.
        var api = new RecordingGitHubApiClient().Fails(404, "Not Found");
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 999999 });

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("status").GetInt32().ShouldBe(404);
        json.TryGetProperty("checkRuns", out _).ShouldBeFalse();
        api.Calls.Count.ShouldBe(1, "the check-runs call must not be attempted with no head SHA");
    }

    [Fact]
    public async Task PullRequestChecks_WhenTheHeadShaIsMissing_SaysSoRatherThanQueryingAnEmptySha()
    {
        var api = new RecordingGitHubApiClient().Returns("""{"number":3300,"head":{"ref":"x"}}""");
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("head commit SHA");
        api.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PullRequestChecks_WhenTheCheckRunFetchFails_ReturnsAStructuredError()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.PullRequest)
            .Fails(403, "Resource not accessible by integration");
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("status").GetInt32().ShouldBe(403);
        json.GetProperty("error").GetString().ShouldBe("Resource not accessible by integration");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PullRequestChecks_WithANonPositiveNumber_IsRejectedBeforeAnyCall(int number)
    {
        var api = new RecordingGitHubApiClient();
        var tool = new GitHubPullRequestChecksTool(api, GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["number"] = number }));

        api.Calls.ShouldBeEmpty();
    }

    // ---- github_pr_diff -----------------------------------------------------------------------

    [Fact]
    public async Task PullRequestDiff_ReturnsPerFileRecords_NotARawDiffString()
    {
        // The anti-regression clause for AC3/AC6 on this tool: returning GitHub's raw .diff media
        // type would force the caller to parse file boundaries out of text, and THIS test reddens by
        // name if the projection is removed.
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequestFiles);
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        json.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var files = json.GetProperty("files").EnumerateArray().ToArray();
        files.Length.ShouldBe(2);
        files[0].GetProperty("path").GetString()
            .ShouldBe("src/extensions/BotNexus.Extensions.GitHub/GitHubWorkflowRunsTool.cs");
        files[0].GetProperty("status").GetString().ShouldBe("added");
        files[0].GetProperty("additions").GetInt32().ShouldBe(140);
        files[1].GetProperty("path").GetString().ShouldBe("docs/extensions/github.md");
        files[1].GetProperty("deletions").GetInt32().ShouldBe(3);
        api.Calls.Single().Path.ShouldStartWith("repos/Sytone/botnexus/pulls/3300/files");
    }

    [Fact]
    public async Task PullRequestDiff_ByDefault_OmitsTheUnboundedPatchText()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequestFiles);
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        json.GetProperty("includePatch").GetBoolean().ShouldBeFalse();
        json.GetProperty("files")[0].TryGetProperty("patch", out _)
            .ShouldBeFalse("patch text is unbounded and must be opt-in");
    }

    [Fact]
    public async Task PullRequestDiff_WithIncludePatch_CarriesTheUnifiedHunkAsAField()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequestFiles);
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["number"] = 3300, ["includePatch"] = true });

        json.GetProperty("includePatch").GetBoolean().ShouldBeTrue();
        json.GetProperty("files")[0].GetProperty("patch").GetString()
            .ShouldNotBeNull().ShouldContain("@@ -0,0 +1,3 @@");
    }

    [Fact]
    public async Task PullRequestDiff_TotalsAdditionsAndDeletionsAcrossThePage()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequestFiles);
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        json.GetProperty("additions").GetInt32().ShouldBe(152);
        json.GetProperty("deletions").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task PullRequestDiff_ReportsPageBoundsAndAContinuationSignal()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequestFiles);
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["number"] = 3300, ["perPage"] = 2, ["page"] = 2 });

        json.GetProperty("page").GetInt32().ShouldBe(2);
        json.GetProperty("perPage").GetInt32().ShouldBe(2);
        json.GetProperty("count").GetInt32().ShouldBe(2);
        json.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        api.Calls.Single().Path.ShouldContain("per_page=2");
        api.Calls.Single().Path.ShouldContain("page=2");
    }

    [Fact]
    public async Task PullRequestDiff_WithAPartialPage_ReportsNoContinuation()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequestFiles);
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300, ["perPage"] = 30 });

        json.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PullRequestDiff_WhenGitHubFails_ReturnsAStructuredErrorCarryingTheStatus()
    {
        var api = new RecordingGitHubApiClient().Fails(404, "Not Found");
        var tool = new GitHubPullRequestDiffTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 999999 });

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact]
    public async Task PullRequestDiff_WithNoNumber_IsRejectedAtPrepareTime()
    {
        var tool = new GitHubPullRequestDiffTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task PullRequestDiff_WithAZeroPage_IsRejected()
    {
        var tool = new GitHubPullRequestDiffTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["number"] = 1, ["page"] = 0 }));
    }

    // ---- github_workflow_runs -----------------------------------------------------------------

    [Fact]
    public async Task WorkflowRuns_ReturnsStructuredRunsReadableWithoutStringParsing()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new());

        json.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var run = json.GetProperty("runs")[0];
        run.GetProperty("id").GetInt64().ShouldBe(900001);
        run.GetProperty("status").GetString().ShouldBe("completed");
        run.GetProperty("conclusion").GetString().ShouldBe("success");
        run.GetProperty("branch").GetString().ShouldBe("feat/2734-github-read-tools");
        run.GetProperty("runNumber").GetInt32().ShouldBe(1204);
        api.Calls.Single().Path.ShouldStartWith("repos/Sytone/botnexus/actions/runs");
    }

    [Fact]
    public async Task WorkflowRuns_ResultIsParsableJson_NotCommandOutput()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        var text = await GitHubFixtures.InvokeAsync(tool, new());

        var parsed = Should.NotThrow(() => JsonDocument.Parse(text));
        parsed.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        parsed.RootElement.GetProperty("runs").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task WorkflowRuns_WithAWorkflowName_TargetsThatWorkflowsRunEndpoint()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        await GitHubFixtures.InvokeJsonAsync(tool, new() { ["workflow"] = "ci-build-test.yml" });

        api.Calls.Single().Path.ShouldStartWith(
            "repos/Sytone/botnexus/actions/workflows/ci-build-test.yml/runs");
    }

    [Fact]
    public async Task WorkflowRuns_FiltersAreQueryParameters_NotClientSideTrimming()
    {
        // Trimming after the fetch would return fewer rows than perPage and make hasMore lie.
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["branch"] = "feat/2734-github-read-tools", ["status"] = "completed" });

        var path = api.Calls.Single().Path;
        path.ShouldContain("branch=feat%2F2734-github-read-tools");
        path.ShouldContain("status=completed");
    }

    [Fact]
    public async Task WorkflowRuns_DerivesContinuationFromTheReportedTotal()
    {
        // GitHub returns a real total here, so hasMore is computed rather than inferred from a full
        // page: page 1 of 42 runs at 1 per page unambiguously has more.
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["perPage"] = 1 });

        json.GetProperty("totalCount").GetInt64().ShouldBe(42);
        json.GetProperty("count").GetInt32().ShouldBe(1);
        json.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WorkflowRuns_OnTheFinalPage_ReportsNoContinuation()
    {
        var api = new RecordingGitHubApiClient()
            .Returns("""{"total_count":1,"workflow_runs":[{"id":1,"status":"completed","conclusion":"success"}]}""");
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["perPage"] = 1 });

        json.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task WorkflowRuns_WhenPerPageExceedsTheBound_ClampsAndSaysSo()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config(maxPageSize: 10));

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["perPage"] = 500 });

        json.GetProperty("perPage").GetInt32().ShouldBe(10);
        json.GetProperty("perPageClamped").GetBoolean().ShouldBeTrue();
        api.Calls.Single().Path.ShouldContain("per_page=10");
    }

    [Fact]
    public async Task WorkflowRuns_WithAnUnknownStatus_IsRejectedRatherThanReturningAnEmptyList()
    {
        // GitHub answers an unrecognised status with an empty list, which an agent reads as "no
        // runs" rather than "bad filter". Rejecting up front keeps those two apart.
        var api = new RecordingGitHubApiClient();
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["status"] = "green" }));

        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("owner/repo/runs")]
    public async Task WorkflowRuns_WithAPathShapedWorkflow_IsRejected(string workflow)
    {
        var tool = new GitHubWorkflowRunsTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["workflow"] = workflow }));
    }

    [Fact]
    public async Task WorkflowRuns_WhenGitHubFails_ReturnsAStructuredErrorCarryingTheStatus()
    {
        var api = new RecordingGitHubApiClient().Fails(500, "Server Error");
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new());

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("status").GetInt32().ShouldBe(500);
        json.GetProperty("error").GetString().ShouldBe("Server Error");
    }

    [Fact]
    public async Task WorkflowRuns_WithAnExplicitRepository_OverridesTheConfiguredDefault()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.WorkflowRuns);
        var tool = new GitHubWorkflowRunsTool(api, GitHubFixtures.Config());

        await GitHubFixtures.InvokeJsonAsync(tool, new() { ["repository"] = "other/repo" });

        api.Calls.Single().Path.ShouldStartWith("repos/other/repo/actions/runs");
    }
}
