using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Reads a pull request's changed files as structured per-file records, with the unified patch
/// hunks available on request.
/// </summary>
/// <remarks>
/// <para>This deliberately does NOT return the raw <c>.diff</c> media type. A raw diff is exactly
/// the command text this extension exists to stop returning: the caller would have to parse file
/// boundaries out of a string to answer "which files changed". The files endpoint already gives per
/// file <c>path</c>, <c>status</c> and line counts as fields, and the patch hunk is one of those
/// fields rather than the whole payload.</para>
/// <para><c>includePatch</c> defaults to false because patch text is unbounded. A large pull request
/// would otherwise spend an entire transcript budget on hunks the agent never asked for.</para>
/// </remarks>
public sealed class GitHubPullRequestDiffTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubPullRequestDiffTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_pr_diff";

    /// <inheritdoc />
    public override string Label => "GitHub Pull Request Diff";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "Read the changed files of a GitHub pull request as structured records (path, status, additions, deletions), optionally including the unified patch for each file. Explicitly paginated.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "number": { "type": "integer", "description": "Pull request number." },
                "includePatch": { "type": "boolean", "description": "Include the unified patch hunk for each file. Default: false, because patch text is unbounded." },
                "perPage": { "type": "integer", "description": "Files per page. Clamped to the configured maximum; the effective value is reported back." },
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
        prepared["includePatch"] = ReadBool(arguments, "includePatch") ?? false;
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
        var includePatch = (bool)arguments["includePatch"]!;
        var page = (int)arguments["page"]!;
        var perPage = (int)arguments["perPage"]!;
        var requestedPerPage = arguments["requestedPerPage"] as int?;

        var response = await Api.SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/pulls/{number}/files?per_page={perPage}&page={page}",
            null,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { ValueKind: JsonValueKind.Array } array)
            return ErrorResult(Name, repository, response);

        var files = array.EnumerateArray()
            .Select(f => GitHubProjections.PullRequestFile(f, includePatch))
            .ToArray();

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            number,
            includePatch,
            page,
            perPage,
            hasMore = files.Length == perPage,
            perPageClamped = requestedPerPage is { } r && r != perPage,
            count = files.Length,
            additions = files.Sum(f => f["additions"] as long? ?? 0),
            deletions = files.Sum(f => f["deletions"] as long? ?? 0),
            files,
        });
    }
}
