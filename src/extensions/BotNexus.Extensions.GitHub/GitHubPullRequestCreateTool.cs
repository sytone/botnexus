using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Creates a GitHub pull request through the REST API.</summary>
public sealed class GitHubPullRequestCreateTool : GitHubToolBase
{
    public GitHubPullRequestCreateTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    public override string Name => "github_pr_create";
    public override string Label => "GitHub Pull Request Create";

    public override Tool Definition => new(
        Name,
        "Create a GitHub pull request with the platform-managed identity.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "title": { "type": "string", "description": "Pull request title." },
                "head": { "type": "string", "description": "Branch containing the changes." },
                "base": { "type": "string", "description": "Target branch. Default: main." },
                "body": { "type": "string", "description": "Pull request body in GitHub-flavoured markdown." },
                "draft": { "type": "boolean", "description": "Create as a draft. Default: false." }
              },
              "required": ["title", "head"]
            }
            """));

    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        prepared["title"] = RequireString(arguments, "title");
        prepared["head"] = RequireString(arguments, "head");
        prepared["base"] = ReadString(arguments, "base") ?? "main";
        prepared["body"] = ReadString(arguments, "body");
        prepared["draft"] = ReadBool(arguments, "draft") ?? false;
    }

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var repository = (string)arguments["repository"]!;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = arguments["title"],
            ["head"] = arguments["head"],
            ["base"] = arguments["base"],
            ["draft"] = arguments["draft"],
        };
        AddIfPresent(payload, "body", arguments["body"]);

        var response = await Api.SendAsync(
            HttpMethod.Post, $"repos/{repository}/pulls", payload, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } created)
            return ErrorResult(Name, repository, response);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            identity = Config.Identity,
            pullRequest = GitHubProjections.PullRequest(created),
        });
    }
}
