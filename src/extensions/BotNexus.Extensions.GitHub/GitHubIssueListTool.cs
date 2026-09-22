using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Lists issues with explicit, non-silent pagination.</summary>
/// <remarks>
/// Every list result carries <c>page</c>, <c>perPage</c>, <c>count</c> and <c>hasMore</c>. A caller
/// can therefore always distinguish a complete page from a bounded one - silent truncation is the
/// specific failure the pagination criterion forbids, because it presents partial data as total.
/// </remarks>
public sealed class GitHubIssueListTool : GitHubToolBase
{
    /// <summary>Creates the tool.</summary>
    public GitHubIssueListTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    /// <inheritdoc />
    public override string Name => "github_issue_list";

    /// <inheritdoc />
    public override string Label => "GitHub Issue List";

    /// <inheritdoc />
    public override Tool Definition => new(
        Name,
        "List GitHub issues with a compact default projection and explicit pagination. Use fields to opt into allow-listed detail such as body.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "state": { "type": "string", "enum": ["open", "closed", "all"], "description": "Issue state filter. Default: open." },
                "labels": { "type": "string", "description": "Comma-separated label names to filter by." },
                "fields": {
                  "type": "array",
                  "description": "Allow-listed fields to return. Defaults to compact census fields; body must be requested explicitly.",
                  "maxItems": 12,
                  "uniqueItems": true,
                  "items": {
                    "type": "string",
                    "enum": ["itemKey", "number", "title", "state", "author", "body", "labels", "commentCount", "createdAt", "updatedAt", "url", "isPullRequest"]
                  }
                },
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
        prepared["labels"] = ReadString(arguments, "labels");
        prepared["fields"] = ReadFields(arguments);
        prepared["page"] = page;
        // Requested and effective are BOTH carried: the result reports the clamp, so a caller that
        // asked for 500 learns it received a bounded page rather than inferring the repo is small.
        prepared["requestedPerPage"] = ReadInt(arguments, "perPage");
        prepared["perPage"] = ClampPageSize(ReadInt(arguments, "perPage"));
    }

    private static string[] ReadFields(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("fields", out var raw) || raw is null)
            return GitHubProjections.DefaultIssueListFields;

        var fields = raw switch
        {
            string value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            string[] values => values,
            IEnumerable<string> values => values.ToArray(),
            JsonElement { ValueKind: JsonValueKind.Array } element => element.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : throw new ArgumentException("fields must contain only strings."))
                .ToArray(),
            _ => throw new ArgumentException("fields must be an array of allow-listed field names."),
        };

        if (fields.Length is 0 or > 12)
            throw new ArgumentException("fields must contain between 1 and 12 unique field names.");

        var normalized = fields.Select(field => field.Trim()).ToArray();
        if (normalized.Any(string.IsNullOrWhiteSpace) || normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("fields must contain between 1 and 12 unique field names.");

        var unknown = normalized.Where(field => !GitHubProjections.IssueListFields.Contains(field, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown issue list field(s): {string.Join(", ", unknown)}. Allowed fields: {string.Join(", ", GitHubProjections.IssueListFields)}.");
        }

        return normalized;
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
        var labels = arguments["labels"] as string;
        var page = (int)arguments["page"]!;
        var perPage = (int)arguments["perPage"]!;
        var requestedPerPage = arguments["requestedPerPage"] as int?;
        var fields = (string[])arguments["fields"]!;

        var path = $"repos/{repository}/issues?state={state}&per_page={perPage}&page={page}";
        if (!string.IsNullOrWhiteSpace(labels))
            path += "&labels=" + Uri.EscapeDataString(labels);

        var response = await Api.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess || response.Body is not { ValueKind: JsonValueKind.Array } array)
            return ErrorResult(Name, repository, response);

        var items = array.EnumerateArray()
            .Select(item => GitHubProjections.IssueListItem(item, fields))
            .ToArray();
        var projectedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(items, GitHubJson.ResultOptions));

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            state,
            page,
            perPage,
            // hasMore is derived from a full page rather than a count GitHub does not return. It can
            // over-report by one page at an exact boundary, which is the correct direction to be
            // wrong in: claiming completeness you do not have is the defect being avoided.
            hasMore = items.Length == perPage,
            perPageClamped = requestedPerPage is { } r && r != perPage,
            count = items.Length,
            fields,
            projectedBytes,
            issues = items,
        });
    }
}
