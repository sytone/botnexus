using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Reads a single issue by number, returning a structured object.</summary>
/// <remarks>The highest-frequency measured GitHub operation: 1,672 <c>gh issue view</c> shell calls.</remarks>
public sealed class GitHubIssueGetTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubIssueGetTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_issue_get";

    /// <inheritdoc />
    public override string Label => "GitHub Issue Get";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "Read a GitHub issue by number. Returns structured fields (number, title, state, author, body, labels) - no output parsing required.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "number": { "type": "integer", "description": "Issue number." },
                "includeComments": { "type": "boolean", "description": "Also fetch the issue's comments. Default: false." }
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
            throw new ArgumentException("number must be a positive issue number.");

        prepared["number"] = number;
        prepared["includeComments"] = arguments.TryGetValue("includeComments", out var raw) && IsTrue(raw);
    }

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.String } el => bool.TryParse(el.GetString(), out var b) && b,
        string s => bool.TryParse(s, out var b) && b,
        _ => false,
    };

    /// <inheritdoc />
    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var repository = (string)arguments["repository"]!;
        var number = (int)arguments["number"]!;
        var includeComments = (bool)arguments["includeComments"]!;

        var response = await Api.SendAsync(
            HttpMethod.Get, $"repos/{repository}/issues/{number}", null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } body)
            return ErrorResult(Name, repository, response);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool"] = Name,
            ["repository"] = repository,
            ["ok"] = true,
            ["issue"] = GitHubProjections.Issue(body),
        };

        if (includeComments)
        {
            var comments = await Api.SendAsync(
                HttpMethod.Get,
                $"repos/{repository}/issues/{number}/comments?per_page={ClampPageSize(null)}",
                null,
                cancellationToken).ConfigureAwait(false);

            // A failed comment fetch does NOT fail the whole call - the issue was read successfully
            // and discarding it would force a retry of both. The failure is reported as a field so
            // the caller can distinguish "no comments" from "comments could not be read".
            payload["comments"] = comments is { IsSuccess: true, Body.ValueKind: JsonValueKind.Array }
                ? comments.Body.Value.EnumerateArray().Select(GitHubProjections.Comment).ToArray()
                : null;
            payload["commentsError"] = comments.IsSuccess ? null : comments.ErrorMessage;
        }

        return StructuredResult(payload);
    }
}
