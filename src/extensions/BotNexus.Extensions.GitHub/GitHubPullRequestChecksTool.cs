using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Reads the CI check runs for a pull request's head commit as a structured, summarised result.
/// </summary>
/// <remarks>
/// <para>Two calls are unavoidable: check runs are addressed by commit SHA, not by pull request
/// number, so the head SHA has to be read first. Doing it inside one tool is the point - the shell
/// equivalent was two <c>gh</c> invocations plus a <c>--jq</c> filter to thread the SHA between
/// them, which is exactly the ceremony this extension removes.</para>
/// <para>The <c>summary</c> block is a derived rollup, not a re-encoding of the list: an agent
/// deciding "is this PR mergeable" reads <c>summary.failed</c> as a number instead of counting
/// conclusions itself, and <c>allCompleted</c> distinguishes "green" from "not finished yet".</para>
/// </remarks>
public sealed class GitHubPullRequestChecksTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubPullRequestChecksTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_pr_checks";

    /// <inheritdoc />
    public override string Label => "GitHub Pull Request Checks";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "Read the CI check runs for a pull request. Returns structured check runs plus a rollup summary (total, succeeded, failed, pending, allCompleted).",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "number": { "type": "integer", "description": "Pull request number." },
                "perPage": { "type": "integer", "description": "Check runs per page. Clamped to the configured maximum; the effective value is reported back." },
                "page": { "type": "integer", "description": "1-based page number. Default: 1." }
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

        var page = ReadInt(arguments, "page") ?? 1;
        if (page < 1)
            throw new ArgumentException("page must be 1 or greater.");

        prepared["number"] = number;
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
        var number = (int)arguments["number"]!;
        var page = (int)arguments["page"]!;
        var perPage = (int)arguments["perPage"]!;
        var requestedPerPage = arguments["requestedPerPage"] as int?;

        var prResponse = await Api.SendAsync(
            HttpMethod.Get, $"repos/{repository}/pulls/{number}", null, cancellationToken)
            .ConfigureAwait(false);

        if (!prResponse.IsSuccess || prResponse.Body is not { } prBody)
            return ErrorResult(Name, repository, prResponse);

        var headSha = HeadSha(prBody);
        if (headSha is null)
        {
            // A pull request with no readable head SHA cannot be checked. Reporting that as its own
            // error beats issuing a request against 'commits//check-runs' and surfacing GitHub's 404
            // as though the checks were missing.
            return StructuredResult(new
            {
                tool = Name,
                repository,
                ok = false,
                number,
                status = prResponse.StatusCode,
                error = "The pull request payload carried no head commit SHA, so check runs cannot be resolved.",
            });
        }

        var checksResponse = await Api.SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/commits/{headSha}/check-runs?per_page={perPage}&page={page}",
            null,
            cancellationToken).ConfigureAwait(false);

        if (!checksResponse.IsSuccess || checksResponse.Body is not { } checksBody)
            return ErrorResult(Name, repository, checksResponse);

        var runs = CheckRuns(checksBody).Select(GitHubProjections.CheckRun).ToArray();

        var succeeded = runs.Count(r => Equals(r["conclusion"], "success"));
        var failed = runs.Count(r => r["conclusion"] is "failure" or "timed_out" or "cancelled" or "action_required");
        var pending = runs.Count(r => !Equals(r["status"], "completed"));

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            number,
            headSha,
            page,
            perPage,
            hasMore = runs.Length == perPage,
            perPageClamped = requestedPerPage is { } r && r != perPage,
            count = runs.Length,
            summary = new
            {
                total = runs.Length,
                succeeded,
                failed,
                pending,
                // Green is not the absence of failure: a PR whose checks have not finished has zero
                // failures too. allCompleted keeps those two states apart.
                allCompleted = pending == 0,
            },
            checkRuns = runs,
        });
    }

    private static string? HeadSha(JsonElement pullRequest) =>
        pullRequest.ValueKind == JsonValueKind.Object
        && pullRequest.TryGetProperty("head", out var head)
        && head.ValueKind == JsonValueKind.Object
        && head.TryGetProperty("sha", out var sha)
        && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()
            : null;

    private static IEnumerable<JsonElement> CheckRuns(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("check_runs", out var runs)
        && runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray()
            : [];
}
