using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Creates a GitHub issue through the REST API.</summary>
public sealed class GitHubIssueCreateTool : GitHubToolBase
{
    public GitHubIssueCreateTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    public override string Name => "github_issue_create";
    public override string Label => "GitHub Issue Create";

    public override Tool Definition => new(
        Name,
        "Create a GitHub issue with the platform-managed identity.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "title": { "type": "string", "description": "Issue title." },
                "body": { "type": "string", "description": "Issue body in GitHub-flavoured markdown." },
                "labels": { "type": "string", "description": "Optional comma-separated label names." }
              },
              "required": ["title"]
            }
            """));

    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var title = RequireString(arguments, "title");
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title must not be empty.");

        prepared["title"] = title;
        prepared["body"] = ReadString(arguments, "body");
        prepared["labels"] = ReadStringList(arguments, "labels");
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
        };
        AddIfPresent(payload, "body", arguments["body"]);
        AddIfPresent(payload, "labels", arguments["labels"]);

        var response = await Api.SendAsync(
            HttpMethod.Post, $"repos/{repository}/issues", payload, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } created)
            return ErrorResult(Name, repository, response);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            identity = Config.Identity,
            issue = GitHubProjections.Issue(created),
        });
    }
}
