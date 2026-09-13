using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Updates or closes a GitHub issue through the REST API.</summary>
public sealed class GitHubIssueUpdateTool : GitHubToolBase
{
    public GitHubIssueUpdateTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    public override string Name => "github_issue_update";
    public override string Label => "GitHub Issue Update";

    public override Tool Definition => new(
        Name,
        "Update or close a GitHub issue with the platform-managed identity.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "number": { "type": "integer", "description": "Issue number." },
                "title": { "type": "string", "description": "Replacement issue title." },
                "body": { "type": "string", "description": "Replacement issue body in GitHub-flavoured markdown." },
                "state": { "type": "string", "enum": ["open", "closed"], "description": "Replacement issue state." },
                "labels": { "type": "string", "description": "Replacement comma-separated label names. An empty string clears labels." }
              },
              "required": ["number"]
            }
            """));

    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var number = RequireInt(arguments, "number");
        if (number <= 0)
            throw new ArgumentException("number must be a positive issue number.");

        var title = ReadString(arguments, "title");
        var bodySupplied = arguments.ContainsKey("body");
        var body = ReadString(arguments, "body") ?? (bodySupplied ? string.Empty : null);
        var state = ReadString(arguments, "state");
        if (state is not null and not ("open" or "closed"))
            throw new ArgumentException("state must be one of: open, closed.");

        var labelsSupplied = arguments.ContainsKey("labels");
        var labels = ReadStringList(arguments, "labels");
        if (title is null && !bodySupplied && state is null && !labelsSupplied)
            throw new ArgumentException("at least one of title, body, state, or labels must be supplied.");

        prepared["number"] = number;
        prepared["title"] = title;
        prepared["body"] = body;
        prepared["state"] = state;
        prepared["labels"] = labels;
        prepared["bodySupplied"] = bodySupplied;
        prepared["labelsSupplied"] = labelsSupplied;
    }

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var repository = (string)arguments["repository"]!;
        var number = (int)arguments["number"]!;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfPresent(payload, "title", arguments["title"]);
        if ((bool)arguments["bodySupplied"]!) payload["body"] = arguments["body"];
        AddIfPresent(payload, "state", arguments["state"]);
        if ((bool)arguments["labelsSupplied"]!) payload["labels"] = arguments["labels"];

        var response = await Api.SendAsync(
            HttpMethod.Patch, $"repos/{repository}/issues/{number}", payload, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } updated)
            return ErrorResult(Name, repository, response);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            identity = Config.Identity,
            issue = GitHubProjections.Issue(updated),
        });
    }
}
