using System.Text.Json;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// Behaviour tests for the GitHub agent tool surface (#2627).
/// </summary>
/// <remarks>
/// Every test drives a tool through the real <c>PrepareArgumentsAsync</c> -> <c>ExecuteAsync</c>
/// pair rather than calling a helper directly, because the prepared-argument dictionary is an
/// allow-list: an argument declared in a schema but never copied through would be silently dropped,
/// producing a plausible answer for the wrong input. Only the full path catches that.
/// </remarks>
public sealed class GitHubToolsTests
{
    // ---- Structured results, not command text -------------------------------------------------

    [Fact]
    public async Task IssueGet_ReturnsStructuredFieldsReadableWithoutStringParsing()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Issue);
        var tool = new GitHubIssueGetTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 2627 });

        json.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var issue = json.GetProperty("issue");
        issue.GetProperty("number").GetInt32().ShouldBe(2627);
        issue.GetProperty("state").GetString().ShouldBe("open");
        issue.GetProperty("author").GetString().ShouldBe("agent-farnsworth");
        issue.GetProperty("labels").EnumerateArray().Select(l => l.GetString())
            .ShouldBe(["type:feature", "area:platform"]);

        api.Calls.Single().Path.ShouldBe("repos/Sytone/botnexus/issues/2627");
    }

    [Fact]
    public async Task IssueGet_ResultIsParsableJson_NotCommandOutput()
    {
        // The anti-regression clause for AC4: if a tool ever returns a formatted table or the raw
        // body text instead of the projection, Parse throws and THIS test reddens by name.
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Issue);
        var tool = new GitHubIssueGetTool(api, GitHubFixtures.Config());

        var text = await GitHubFixtures.InvokeAsync(tool, new() { ["number"] = 2627 });

        var parsed = Should.NotThrow(() => JsonDocument.Parse(text));
        parsed.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        parsed.RootElement.GetProperty("issue").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task IssueGet_WithIncludeComments_FetchesCommentsAndProjectsThem()
    {
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.Issue)
            .Returns($"[{GitHubFixtures.Comment}]");
        var tool = new GitHubIssueGetTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["number"] = 2627, ["includeComments"] = true });

        var comments = json.GetProperty("comments").EnumerateArray().ToArray();
        comments.Length.ShouldBe(1);
        comments[0].GetProperty("author").GetString().ShouldBe("agent-farnsworth");
        api.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task IssueGet_WhenCommentFetchFails_StillReturnsTheIssueAndReportsTheCommentFailure()
    {
        // Discarding a successfully read issue because a secondary call failed would force the agent
        // to retry both. The partial failure is a FIELD, so the caller can tell "no comments" from
        // "comments could not be read".
        var api = new RecordingGitHubApiClient()
            .Returns(GitHubFixtures.Issue)
            .Fails(403, "Resource not accessible by integration");
        var tool = new GitHubIssueGetTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["number"] = 2627, ["includeComments"] = true });

        json.GetProperty("ok").GetBoolean().ShouldBeTrue();
        json.GetProperty("issue").GetProperty("number").GetInt32().ShouldBe(2627);
        json.GetProperty("commentsError").GetString().ShouldBe("Resource not accessible by integration");
    }

    [Fact]
    public async Task PullRequestGet_ProjectsMergeStateAndRefs()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequest);
        var tool = new GitHubPullRequestGetTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 3300 });

        var pr = json.GetProperty("pullRequest");
        pr.GetProperty("draft").GetBoolean().ShouldBeFalse();
        pr.GetProperty("mergeable").GetBoolean().ShouldBeTrue();
        pr.GetProperty("headRef").GetString().ShouldBe("feat/2627-github-tool");
        pr.GetProperty("baseRef").GetString().ShouldBe("main");
        pr.GetProperty("changedFiles").GetInt32().ShouldBe(14);
        pr.GetProperty("isPullRequest").GetBoolean().ShouldBeTrue();
        api.Calls.Single().Path.ShouldBe("repos/Sytone/botnexus/pulls/3300");
    }

    // ---- Pagination is explicit, never silent -------------------------------------------------

    [Fact]
    public async Task IssueList_ReportsPageBoundsAndAContinuationSignal()
    {
        var api = new RecordingGitHubApiClient().Returns($"[{GitHubFixtures.Issue},{GitHubFixtures.Issue}]");
        var tool = new GitHubIssueListTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["perPage"] = 2, ["page"] = 3 });

        json.GetProperty("page").GetInt32().ShouldBe(3);
        json.GetProperty("perPage").GetInt32().ShouldBe(2);
        json.GetProperty("count").GetInt32().ShouldBe(2);
        // A full page means more MAY exist. Reporting false here would present a bounded set as a
        // total - the silent truncation this criterion forbids.
        json.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        api.Calls.Single().Path.ShouldContain("per_page=2");
        api.Calls.Single().Path.ShouldContain("page=3");
    }

    [Fact]
    public async Task IssueList_WithAPartialPage_ReportsNoContinuation()
    {
        var api = new RecordingGitHubApiClient().Returns($"[{GitHubFixtures.Issue}]");
        var tool = new GitHubIssueListTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["perPage"] = 5 });

        json.GetProperty("count").GetInt32().ShouldBe(1);
        json.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task IssueList_WhenPerPageExceedsTheBound_ClampsAndSaysSo()
    {
        // Clamping silently would let a caller that asked for 500 conclude the repository has 10
        // issues. The clamp is reported as a field so that inference is impossible.
        var api = new RecordingGitHubApiClient().Returns("[]");
        var tool = new GitHubIssueListTool(api, GitHubFixtures.Config(maxPageSize: 10));

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["perPage"] = 500 });

        json.GetProperty("perPage").GetInt32().ShouldBe(10);
        json.GetProperty("perPageClamped").GetBoolean().ShouldBeTrue();
        api.Calls.Single().Path.ShouldContain("per_page=10");
    }

    [Fact]
    public async Task IssueList_LabelFilterIsUrlEncodedIntoTheQuery()
    {
        var api = new RecordingGitHubApiClient().Returns("[]");
        var tool = new GitHubIssueListTool(api, GitHubFixtures.Config());

        await GitHubFixtures.InvokeJsonAsync(tool, new() { ["labels"] = "type:bug,area:platform" });

        api.Calls.Single().Path.ShouldContain("labels=type%3Abug%2Carea%3Aplatform");
    }

    [Fact]
    public async Task PullRequestList_ReportsPageBounds()
    {
        var api = new RecordingGitHubApiClient().Returns($"[{GitHubFixtures.PullRequest}]");
        var tool = new GitHubPullRequestListTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["state"] = "all" });

        json.GetProperty("state").GetString().ShouldBe("all");
        json.GetProperty("count").GetInt32().ShouldBe(1);
        json.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        api.Calls.Single().Path.ShouldContain("state=all");
    }

    // ---- AC5: comment writes use REST, never GraphQL ------------------------------------------

    [Fact]
    public async Task IssueComment_PostsToTheRestCommentEndpoint_NotGraphQl()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Comment);
        var tool = new GitHubIssueCommentTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["number"] = 2627, ["body"] = "PR is open." });

        var call = api.Calls.Single();
        call.Method.ShouldBe(HttpMethod.Post);
        // The REST path, asserted positively AND the GraphQL path excluded: the GraphQL addComment
        // mutation fails under an EMU account, so the transport is the behaviour under test.
        call.Path.ShouldBe("repos/Sytone/botnexus/issues/2627/comments");
        call.Path.ShouldNotContain("graphql");
        json.GetProperty("comment").GetProperty("id").GetInt64().ShouldBe(5312211422);
        json.GetProperty("identity").GetString().ShouldBe("agent-farnsworth[bot]");
    }

    [Fact]
    public async Task IssueComment_SendsTheBodyAsAJsonPayload_NotAShellArgument()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Comment);
        var tool = new GitHubIssueCommentTool(api, GitHubFixtures.Config());

        // A body containing quotes, backticks and a newline is precisely what forced throwaway
        // tmp/*.ps1 files under the shell path. Here it is just a JSON string field.
        const string awkward = "Line one\n`backticks` and \"quotes\" and $vars";
        await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 1, ["body"] = awkward });

        var body = api.Calls.Single().Body.ShouldNotBeNull();
        var serialised = JsonSerializer.Serialize(body);
        JsonDocument.Parse(serialised).RootElement.GetProperty("body").GetString().ShouldBe(awkward);
    }

    // ---- Sad paths ----------------------------------------------------------------------------

    [Fact]
    public async Task IssueGet_WhenGitHubFails_ReturnsAStructuredErrorCarryingTheStatus()
    {
        var api = new RecordingGitHubApiClient().Fails(404, "Not Found");
        var tool = new GitHubIssueGetTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["number"] = 999999 });

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        // The status is a FIELD, not a substring of stderr an agent must pattern-match to decide
        // whether to retry.
        json.GetProperty("status").GetInt32().ShouldBe(404);
        json.GetProperty("error").GetString().ShouldBe("Not Found");
    }

    [Fact]
    public async Task IssueGet_WithNoNumber_IsRejectedAtPrepareTime()
    {
        var tool = new GitHubIssueGetTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task IssueGet_WithANonPositiveNumber_IsRejected(int number)
    {
        var tool = new GitHubIssueGetTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["number"] = number }));
    }

    [Fact]
    public async Task IssueComment_WithAnEmptyBody_IsRejectedBeforeAnyCallIsMade()
    {
        var api = new RecordingGitHubApiClient();
        var tool = new GitHubIssueCommentTool(api, GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["number"] = 1, ["body"] = "   " }));

        api.Calls.ShouldBeEmpty("validation must reject before an authenticated write reaches GitHub");
    }

    [Fact]
    public async Task IssueList_WithAnUnknownState_IsRejected()
    {
        var tool = new GitHubIssueListTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["state"] = "merged" }));
    }

    [Fact]
    public async Task Tool_WithNoRepositoryArgumentAndNoConfiguredDefault_ExplainsBothRemedies()
    {
        var tool = new GitHubIssueGetTool(new RecordingGitHubApiClient(), new GitHubToolsConfig());

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["number"] = 1 }));

        ex.Message.ShouldContain("owner/repo");
        ex.Message.ShouldContain("configure a default repository");
    }

    [Theory]
    [InlineData("no-slash")]
    [InlineData("too/many/parts")]
    [InlineData("owner/")]
    public async Task Tool_WithAMalformedRepository_IsRejected(string repository)
    {
        var tool = new GitHubIssueGetTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["number"] = 1, ["repository"] = repository }));
    }

    [Fact]
    public async Task Tool_WithAnExplicitRepository_OverridesTheConfiguredDefault()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Issue);
        var tool = new GitHubIssueGetTool(api, GitHubFixtures.Config());

        await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["number"] = 7, ["repository"] = "other/repo" });

        api.Calls.Single().Path.ShouldBe("repos/other/repo/issues/7");
    }

    // ---- AC7: the escape hatch ----------------------------------------------------------------

    [Fact]
    public async Task GitHubApi_CallsAnArbitraryPathWithTheManagedCredential()
    {
        var api = new RecordingGitHubApiClient().Returns("""{"login":"agent-farnsworth"}""");
        var tool = new GitHubApiTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["path"] = "user" });

        json.GetProperty("ok").GetBoolean().ShouldBeTrue();
        json.GetProperty("data").GetProperty("login").GetString().ShouldBe("agent-farnsworth");
        api.Calls.Single().Method.ShouldBe(HttpMethod.Get);
        api.Calls.Single().Path.ShouldBe("user");
    }

    [Fact]
    public async Task GitHubApi_AcceptsANonRepositoryPathWithNoConfiguredRepository()
    {
        // The escape hatch must not inherit the base's owner/repo requirement: 'rate_limit' has no
        // repository, and demanding one would send the agent back to the shell for exactly the tail
        // endpoints this tool exists to cover.
        var api = new RecordingGitHubApiClient().Returns("""{"rate":{"remaining":4999}}""");
        var tool = new GitHubApiTool(api, new GitHubToolsConfig());

        var json = await GitHubFixtures.InvokeJsonAsync(tool, new() { ["path"] = "rate_limit" });

        json.GetProperty("data").GetProperty("rate").GetProperty("remaining").GetInt32().ShouldBe(4999);
    }

    [Fact]
    public async Task GitHubApi_WithAnAbsoluteUrl_IsRejected()
    {
        var tool = new GitHubApiTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["path"] = "https://evil.example.com/steal" }));
    }

    [Fact]
    public async Task GitHubApi_WithAnUnsupportedMethod_IsRejected()
    {
        var tool = new GitHubApiTool(new RecordingGitHubApiClient(), GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["path"] = "user", ["method"] = "TRACE" }));
    }

    [Fact]
    public async Task GitHubApi_OnFailure_ReportsStatusAndErrorRatherThanThrowing()
    {
        var api = new RecordingGitHubApiClient().Fails(422, "Validation Failed");
        var tool = new GitHubApiTool(api, GitHubFixtures.Config());

        var json = await GitHubFixtures.InvokeJsonAsync(
            tool, new() { ["path"] = "repos/o/r/labels", ["method"] = "POST" });

        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("status").GetInt32().ShouldBe(422);
        json.GetProperty("error").GetString().ShouldBe("Validation Failed");
    }
}
