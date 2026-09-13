using System.Text.Json;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>Behavioral contracts for the complete GitHub write-tool surface (#2735).</summary>
public sealed class GitHubWriteToolsTests
{
    [Fact]
    public async Task IssueCreate_PostsIssueAndProjectsCreatedIssue()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Issue, 201);

        var result = await GitHubFixtures.InvokeJsonAsync(new GitHubIssueCreateTool(api, GitHubFixtures.Config()), new()
        {
            ["title"] = "Add a bounded write tool",
            ["body"] = "Structured body",
            ["labels"] = "type:feature,area:platform",
        });

        var call = api.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(HttpMethod.Post);
        call.Path.ShouldBe("repos/Sytone/botnexus/issues");
        var payload = JsonSerializer.SerializeToElement(call.Body);
        payload.GetProperty("title").GetString().ShouldBe("Add a bounded write tool");
        payload.GetProperty("body").GetString().ShouldBe("Structured body");
        payload.GetProperty("labels").EnumerateArray().Select(x => x.GetString())
            .ShouldBe(["type:feature", "area:platform"]);
        result.GetProperty("issue").GetProperty("number").GetInt32().ShouldBe(2627);
        result.GetProperty("identity").GetString().ShouldBe("agent-farnsworth[bot]");
    }

    [Fact]
    public async Task IssueUpdate_PatchesOnlySuppliedFieldsAndCanCloseIssue()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.ClosedIssue);

        var result = await GitHubFixtures.InvokeJsonAsync(new GitHubIssueUpdateTool(api, GitHubFixtures.Config()), new()
        {
            ["number"] = 2735,
            ["state"] = "closed",
            ["labels"] = "type:feature,area:platform",
        });

        var call = api.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(HttpMethod.Patch);
        call.Path.ShouldBe("repos/Sytone/botnexus/issues/2735");
        var payload = JsonSerializer.SerializeToElement(call.Body);
        payload.EnumerateObject().Select(x => x.Name).ShouldBe(["state", "labels"]);
        payload.GetProperty("state").GetString().ShouldBe("closed");
        result.GetProperty("issue").GetProperty("state").GetString().ShouldBe("closed");
    }

    [Fact]
    public async Task PullRequestCreate_PostsHeadBaseBodyAndDraft()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.PullRequest, 201);

        var result = await GitHubFixtures.InvokeJsonAsync(new GitHubPullRequestCreateTool(api, GitHubFixtures.Config()), new()
        {
            ["title"] = "feat(tools): add write-side tools",
            ["head"] = "feat/2735-github-write-tools",
            ["base"] = "main",
            ["body"] = "Closes #2735",
            ["draft"] = true,
        });

        var call = api.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(HttpMethod.Post);
        call.Path.ShouldBe("repos/Sytone/botnexus/pulls");
        var payload = JsonSerializer.SerializeToElement(call.Body);
        payload.GetProperty("head").GetString().ShouldBe("feat/2735-github-write-tools");
        payload.GetProperty("base").GetString().ShouldBe("main");
        payload.GetProperty("draft").GetBoolean().ShouldBeTrue();
        result.GetProperty("pullRequest").GetProperty("number").GetInt32().ShouldBe(3300);
    }

    [Fact]
    public async Task Labels_List_UsesExplicitPaginationAndProjectsLabels()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Labels);

        var result = await GitHubFixtures.InvokeJsonAsync(new GitHubLabelsTool(api, GitHubFixtures.Config()), new()
        {
            ["action"] = "list",
            ["perPage"] = 2,
            ["page"] = 3,
        });

        var call = api.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(HttpMethod.Get);
        call.Path.ShouldBe("repos/Sytone/botnexus/labels?per_page=2&page=3");
        result.GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        result.GetProperty("labels")[0].GetProperty("name").GetString().ShouldBe("type:feature");
    }

    [Fact]
    public async Task Labels_Create_NormalizesColorAndProjectsCreatedLabel()
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Label, 201);

        var result = await GitHubFixtures.InvokeJsonAsync(new GitHubLabelsTool(api, GitHubFixtures.Config()), new()
        {
            ["action"] = "create",
            ["name"] = "area:tools",
            ["color"] = "#1D76DB",
            ["description"] = "Agent tools",
        });

        var call = api.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(HttpMethod.Post);
        call.Path.ShouldBe("repos/Sytone/botnexus/labels");
        var payload = JsonSerializer.SerializeToElement(call.Body);
        payload.GetProperty("color").GetString().ShouldBe("1D76DB");
        result.GetProperty("label").GetProperty("name").GetString().ShouldBe("area:tools");
    }

    [Fact]
    public async Task IssueUpdate_WithNoChanges_IsRejectedBeforeAnyWrite()
    {
        var api = new RecordingGitHubApiClient();
        var tool = new GitHubIssueUpdateTool(api, GitHubFixtures.Config());

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["number"] = 2735 }));

        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create", null, null)]
    [InlineData("create", "area:tools", "xyzxyz")]
    [InlineData("delete", null, null)]
    public async Task Labels_WithInvalidArguments_AreRejectedBeforeAnyWrite(
        string action, string? name, string? color)
    {
        var api = new RecordingGitHubApiClient();
        var tool = new GitHubLabelsTool(api, GitHubFixtures.Config());
        var arguments = new Dictionary<string, object?> { ["action"] = action };
        if (name is not null) arguments["name"] = name;
        if (color is not null) arguments["color"] = color;

        await Should.ThrowAsync<ArgumentException>(() => tool.PrepareArgumentsAsync(arguments));
        api.Calls.ShouldBeEmpty();
    }
}
