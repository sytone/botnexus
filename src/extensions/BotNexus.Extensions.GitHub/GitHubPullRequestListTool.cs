using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Lists pull requests with the same explicit pagination contract as the issue list tool.</summary>
public sealed class GitHubPullRequestListTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubPullRequestListTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_pr_list";

    /// <inheritdoc />
    public override string Label => "GitHub Pull Request List";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "List GitHub pull requests. Returns a structured, explicitly paginated result (page, perPage, count, hasMore) - never a silently truncated set.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "state": { "type": "string", "enum": ["open", "closed", "all"], "description": "Pull request state filter. Default: open." },
                "perPage": { "type": "integer", "description": "Results per page. Clamped to the configured maximum; the effective value is reported back." },
                "page": { "type": "integer", "description": "1-based page number. Default: 1." }
              }
            }
            """));

    /// <inheritdoc />
    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var state = ReadString(arguments, "state") ?? "open";
        if (state is not ("open" or "closed" or "all"))
            throw new ArgumentException("state must be one of: open, closed, all.");

        var page = ReadInt(arguments, "page") ?? 1;
        if (page < 1)
            throw new ArgumentException("page must be 1 or greater.");

        prepared["state"] = state;
        prepared["page"] = page;
        prepared["requestedPerPage"] = ReadInt(arguments, "perPage");
        prepared["perPage"] = ClampPageSize(ReadInt(arguments, "perPage"));
    }

    /// <inheritdoc />
    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var repository = (string)arguments["repository"]!;
        var state = (string)arguments["state"]!;
        var page = (int)arguments["page"]!;
        var perPage = (int)arguments["perPage"]!;
        var requestedPerPage = arguments["requestedPerPage"] as int?;

        var response = await Api.SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/pulls?state={state}&per_page={perPage}&page={page}",
            null,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { ValueKind: JsonValueKind.Array } array)
            return ErrorResult(Name, repository, response);

        var items = array.EnumerateArray().Select(GitHubProjections.PullRequest).ToArray();

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            state,
            page,
            perPage,
            hasMore = items.Length == perPage,
            perPageClamped = requestedPerPage is { } r && r != perPage,
            count = items.Length,
            pullRequests = items,
        });
    }
}
