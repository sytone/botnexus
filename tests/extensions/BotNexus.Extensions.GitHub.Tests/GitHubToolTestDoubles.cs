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
          "head": { "ref": "feat/2627-github-tool" },
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
