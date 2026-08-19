using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Lists GitHub Actions workflow runs for a repository as structured records, with explicit
/// pagination and optional branch, status and workflow filters.
/// </summary>
/// <remarks>
/// <para>The total run count GitHub reports is carried through as <c>totalCount</c> alongside the
/// page bounds. It is the one list endpoint in this extension that returns a real total, so
/// <c>hasMore</c> is computed from it rather than inferred from a full page - a strictly better
/// signal, and worth the divergence from the issue/PR list shape.</para>
/// <para>The filters are query parameters, not client-side trimming: filtering after the fetch would
/// return fewer rows than <c>perPage</c> and make the continuation signal lie.</para>
/// </remarks>
public sealed class GitHubWorkflowRunsTool : GitHubToolBase
{
    private static readonly string[] AllowedStatuses =
    [
        "queued", "in_progress", "completed", "success", "failure", "cancelled",
        "timed_out", "action_required", "neutral", "skipped", "stale", "requested", "waiting",
    ];

    /// <summary>Creates the tool.</summary>
    public GitHubWorkflowRunsTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_workflow_runs";

    /// <inheritdoc />
    public override string Label => "GitHub Workflow Runs";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "List GitHub Actions workflow runs. Returns structured runs (status, conclusion, branch, headSha) with explicit pagination (page, perPage, count, totalCount, hasMore).",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "workflow": { "type": "string", "description": "Workflow file name (e.g. 'ci-build-test.yml') or numeric workflow id. Omit to list runs across all workflows." },
                "branch": { "type": "string", "description": "Filter to runs on this branch." },
                "status": { "type": "string", "description": "Filter by run status or conclusion, e.g. queued, in_progress, completed, success, failure." },
                "perPage": { "type": "integer", "description": "Runs per page. Clamped to the configured maximum; the effective value is reported back." },
                "page": { "type": "integer", "description": "1-based page number. Default: 1." }
              }
            }
            """));

    /// <inheritdoc />
    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var page = ReadInt(arguments, "page") ?? 1;
        if (page < 1)
            throw new ArgumentException("page must be 1 or greater.");

        var status = ReadString(arguments, "status");
        if (status is not null && !AllowedStatuses.Contains(status, StringComparer.Ordinal))
        {
            // Rejecting here beats passing an unknown value through: GitHub answers an unrecognised
            // status with an empty list, which an agent reads as "no runs" rather than "bad filter".
            throw new ArgumentException(
                "status must be one of: " + string.Join(", ", AllowedStatuses) + ".");
        }

        var workflow = ReadString(arguments, "workflow");
        if (workflow is not null && (workflow.Contains('/') || workflow.Contains("..", StringComparison.Ordinal)))
            throw new ArgumentException("workflow must be a workflow file name or numeric id, not a path.");

        prepared["workflow"] = workflow;
        prepared["branch"] = ReadString(arguments, "branch");
        prepared["status"] = status;
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
        var workflow = arguments["workflow"] as string;
        var branch = arguments["branch"] as string;
        var status = arguments["status"] as string;
        var page = (int)arguments["page"]!;
        var perPage = (int)arguments["perPage"]!;
        var requestedPerPage = arguments["requestedPerPage"] as int?;

        var path = string.IsNullOrWhiteSpace(workflow)
            ? $"repos/{repository}/actions/runs?per_page={perPage}&page={page}"
            : $"repos/{repository}/actions/workflows/{Uri.EscapeDataString(workflow)}/runs?per_page={perPage}&page={page}";

        if (!string.IsNullOrWhiteSpace(branch))
            path += "&branch=" + Uri.EscapeDataString(branch);
        if (!string.IsNullOrWhiteSpace(status))
            path += "&status=" + Uri.EscapeDataString(status);

        var response = await Api.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { } body)
            return ErrorResult(Name, repository, response);

        var runs = Runs(body).Select(GitHubProjections.WorkflowRun).ToArray();
        var totalCount = TotalCount(body);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            workflow,
            branch,
            status,
            page,
            perPage,
            count = runs.Length,
            totalCount,
            // GitHub reports a real total here, so continuation is computed rather than inferred
            // from a full page. Falls back to the full-page heuristic when the total is absent.
            hasMore = totalCount is { } total ? (long)page * perPage < total : runs.Length == perPage,
            perPageClamped = requestedPerPage is { } r && r != perPage,
            runs,
        });
    }

    private static IEnumerable<JsonElement> Runs(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("workflow_runs", out var runs)
        && runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray()
            : [];

    private static long? TotalCount(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("total_count", out var total)
        && total.ValueKind == JsonValueKind.Number
        && total.TryGetInt64(out var value)
            ? value
            : null;
}
