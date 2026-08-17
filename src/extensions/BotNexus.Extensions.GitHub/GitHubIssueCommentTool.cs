using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Posts a comment on an issue or pull request via the REST endpoint.</summary>
/// <remarks>
/// <para><b>REST, not GraphQL, deliberately (#2627 AC5).</b> The GraphQL <c>addComment</c> mutation
/// fails under an Enterprise Managed User account. That workaround was previously rediscovered per
/// agent, per session; encoding it here means the platform carries it once. This tool has no GraphQL
/// code path at all, so the failure mode is unreachable rather than merely avoided.</para>
/// <para>Pull-request comments use the same <c>issues/{n}/comments</c> endpoint, because GitHub
/// models a PR as an issue for conversation purposes - hence one tool, not two.</para>
/// </remarks>
public sealed class GitHubIssueCommentTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubIssueCommentTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_issue_comment";

    /// <inheritdoc />
    public override string Label => "GitHub Issue Comment";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "Post a comment on a GitHub issue or pull request. Uses the REST comment endpoint, which works under Enterprise Managed User accounts where the GraphQL mutation fails.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "number": { "type": "integer", "description": "Issue or pull request number." },
                "body": { "type": "string", "description": "Comment body in GitHub-flavoured markdown." }
              },
              "required": ["number", "body"]
            }
            """));

    /// <inheritdoc />
    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var number = RequireInt(arguments, "number");
        if (number <= 0)
            throw new ArgumentException("number must be a positive issue or pull request number.");

        var body = RequireString(arguments, "body");
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("body must not be empty.");

        prepared["number"] = number;
        prepared["body"] = body;
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
        var body = (string)arguments["body"]!;

        var response = await Api.SendAsync(
            HttpMethod.Post,
            $"repos/{repository}/issues/{number}/comments",
            new { body },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } created)
            return ErrorResult(Name, repository, response);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            number,
            identity = Config.Identity,
            comment = GitHubProjections.Comment(created),
        });
    }
}
