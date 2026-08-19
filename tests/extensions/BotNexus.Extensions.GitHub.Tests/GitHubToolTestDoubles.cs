using System.Text.Json;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// Records every REST call made through the seam and replays scripted responses.
/// </summary>
/// <remarks>
/// Recording the requests is the point: it is what lets a test assert that a comment write went to
/// the REST <c>issues/{n}/comments</c> path and NOT to <c>graphql</c> (#2627 AC5). Asserting only on
/// the returned result would pass equally well for either transport.
/// </remarks>
internal sealed class RecordingGitHubApiClient : IGitHubApiClient
{
    private readonly Queue<GitHubApiResponse> _responses = new();

    /// <summary>Every call made, in order.</summary>
    public List<(HttpMethod Method, string Path, object? Body)> Calls { get; } = [];

    /// <summary>Queues a successful response carrying <paramref name="json"/> as its body.</summary>
    public RecordingGitHubApiClient Returns(string json, int status = 200)
    {
        _responses.Enqueue(new GitHubApiResponse(status, true, JsonDocument.Parse(json).RootElement.Clone()));
        return this;
    }

    /// <summary>Queues a failure response.</summary>
    public RecordingGitHubApiClient Fails(int status, string message)
    {
        _responses.Enqueue(new GitHubApiResponse(status, false, null, message));
        return this;
    }

    /// <inheritdoc />
    public Task<GitHubApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((method, path, body));

        return Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : new GitHubApiResponse(404, false, null, "No response scripted for this call."));
    }
}

/// <summary>Helper JSON fixtures shaped like real GitHub REST payloads.</summary>
internal static class GitHubFixtures
{
    internal const string Issue = """
        {
          "number": 2627,
          "title": "Add a GitHub agent tool extension",
          "state": "open",
          "body": "Agents shell out to gh.",
          "comments": 4,
          "created_at": "2026-06-01T10:00:00Z",
          "updated_at": "2026-08-01T10:00:00Z",
          "html_url": "https://github.com/Sytone/botnexus/issues/2627",
          "user": { "login": "agent-farnsworth" },
          "labels": [ { "name": "type:feature" }, { "name": "area:platform" } ]
        }
        """;

    internal const string PullRequest = """
        {
          "number": 3300,
          "title": "feat(#2627): add github tools",
          "state": "open",
          "body": "Adds the tool surface.",
          "draft": false,
          "merged": false,
          "mergeable": true,
          "additions": 900,
          "deletions": 12,
          "changed_files": 14,
          "created_at": "2026-08-17T10:00:00Z",
          "html_url": "https://github.com/Sytone/botnexus/pull/3300",
          "user": { "login": "agent-farnsworth" },
          "labels": [],
          "head": { "ref": "feat/2627-github-tool", "sha": "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678" },
          "base": { "ref": "main" }
        }
        """;

    internal const string Comment = """
        {
          "id": 5312211422,
          "body": "PR is open.",
          "created_at": "2026-08-17T12:00:00Z",
          "html_url": "https://github.com/Sytone/botnexus/issues/2627#issuecomment-5312211422",
          "user": { "login": "agent-farnsworth" }
        }
        """;

    internal const string CheckRuns = """
        {
          "total_count": 3,
          "check_runs": [
            {
              "id": 101,
              "name": "build",
              "status": "completed",
              "conclusion": "success",
              "started_at": "2026-08-17T10:00:00Z",
              "completed_at": "2026-08-17T10:06:00Z",
              "html_url": "https://github.com/Sytone/botnexus/runs/101"
            },
            {
              "id": 102,
              "name": "unit-tests",
              "status": "completed",
              "conclusion": "failure",
              "started_at": "2026-08-17T10:00:00Z",
              "completed_at": "2026-08-17T10:12:00Z",
              "html_url": "https://github.com/Sytone/botnexus/runs/102"
            },
            {
              "id": 103,
              "name": "docs",
              "status": "in_progress",
              "conclusion": null,
              "started_at": "2026-08-17T10:00:00Z",
              "html_url": "https://github.com/Sytone/botnexus/runs/103"
            }
          ]
        }
        """;

    internal const string PullRequestFiles = """
        [
          {
            "filename": "src/extensions/BotNexus.Extensions.GitHub/GitHubWorkflowRunsTool.cs",
            "status": "added",
            "additions": 140,
            "deletions": 0,
            "changes": 140,
            "patch": "@@ -0,0 +1,3 @@\n+namespace BotNexus.Extensions.GitHub;"
          },
          {
            "filename": "docs/extensions/github.md",
            "status": "modified",
            "additions": 12,
            "deletions": 3,
            "changes": 15,
            "patch": "@@ -1,2 +1,3 @@\n+a line"
          }
        ]
        """;

    internal const string WorkflowRuns = """
        {
          "total_count": 42,
          "workflow_runs": [
            {
              "id": 900001,
              "name": "CI Build and Test",
              "workflow_id": 77,
              "run_number": 1204,
              "run_attempt": 1,
              "event": "pull_request",
              "status": "completed",
              "conclusion": "success",
              "head_branch": "feat/2734-github-read-tools",
              "head_sha": "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
              "created_at": "2026-08-19T09:00:00Z",
              "updated_at": "2026-08-19T09:14:00Z",
              "html_url": "https://github.com/Sytone/botnexus/actions/runs/900001"
            }
          ]
        }
        """;

    /// <summary>Builds a config with a default repository so calls need not name one.</summary>
    internal static GitHubToolsConfig Config(int maxPageSize = 100) => new()
    {
        DefaultRepository = "Sytone/botnexus",
        Identity = "agent-farnsworth[bot]",
        DefaultPageSize = Math.Min(30, maxPageSize),
        MaxPageSize = maxPageSize,
    };

    /// <summary>Runs a tool end to end and returns its result text.</summary>
    internal static async Task<string> InvokeAsync(
        GitHubToolBase tool,
        Dictionary<string, object?> arguments)
    {
        var prepared = await tool.PrepareArgumentsAsync(arguments);
        var result = await tool.ExecuteAsync("call-1", prepared);
        return result.Content[0].Value ?? string.Empty;
    }

    /// <summary>Runs a tool end to end and returns its parsed structured result.</summary>
    internal static async Task<JsonElement> InvokeJsonAsync(
        GitHubToolBase tool,
        Dictionary<string, object?> arguments)
    {
        var text = await InvokeAsync(tool, arguments);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
