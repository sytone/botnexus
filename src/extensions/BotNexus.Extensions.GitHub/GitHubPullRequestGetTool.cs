using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Reads a single pull request by number, returning a structured object.</summary>
/// <remarks>
/// Uses the <c>pulls</c> endpoint rather than <c>issues</c> because merge state, head/base refs and
/// diff statistics exist only there - and those are the fields agents shell out for.
/// </remarks>
public sealed class GitHubPullRequestGetTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubPullRequestGetTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_pr_get";

    /// <inheritdoc />
    public override string Label => "GitHub Pull Request Get";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "Read a GitHub pull request by number. Returns structured fields including state, draft, merged, mergeable, head/base refs and diff statistics.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "number": { "type": "integer", "description": "Pull request number." }
              },
              "required": ["number"]
            }
            """));

    /// <inheritdoc />
    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var number = RequireInt(arguments, "number");
        if (number <= 0)
            throw new ArgumentException("number must be a positive pull request number.");

        prepared["number"] = number;
    }

    /// <inheritdoc />
    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var repository = (string)arguments["repository"]!;
        var number = (int)arguments["number"]!;

        var response = await Api.SendAsync(
            HttpMethod.Get, $"repos/{repository}/pulls/{number}", null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } body)
            return ErrorResult(Name, repository, response);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            pullRequest = GitHubProjections.PullRequest(body),
        });
    }
}
