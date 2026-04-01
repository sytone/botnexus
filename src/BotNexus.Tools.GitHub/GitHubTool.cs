using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Tools;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Tools.GitHub;

/// <summary>
/// Extension tool that exposes read-only GitHub API operations to BotNexus agents.
/// Demonstrates how to create a custom tool as an external extension library.
///
/// Supported actions:
/// <list type="bullet">
///   <item><c>get_repo</c> — get repository metadata</item>
///   <item><c>list_issues</c> — list open issues for a repository</item>
///   <item><c>get_issue</c> — get a single issue by number</item>
///   <item><c>list_prs</c> — list open pull requests</item>
///   <item><c>search_code</c> — search code in a repository</item>
/// </list>
/// </summary>
public sealed class GitHubTool : ToolBase
{
    private readonly HttpClient _http;
    private readonly string? _defaultOwner;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public GitHubTool(GitHubToolsConfig config, HttpClient? httpClient = null, ILogger? logger = null)
        : base(logger)
    {
        _defaultOwner = config.DefaultOwner;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(config.ApiBase.TrimEnd('/') + '/');
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.Add("User-Agent", "BotNexus-GitHub-Tool/1.0");

        if (!string.IsNullOrWhiteSpace(config.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "github",
        "Interact with GitHub repositories (read-only). Actions: get_repo, list_issues, get_issue, list_prs, search_code.",
        new Dictionary<string, ToolParameterSchema>
        {
            ["action"] = new("string", "Action to perform",
                Required: true,
                EnumValues: ["get_repo", "list_issues", "get_issue", "list_prs", "search_code"]),
            ["owner"] = new("string", "Repository owner (user or organisation)", Required: false),
            ["repo"] = new("string", "Repository name", Required: false),
            ["number"] = new("string", "Issue or PR number (for get_issue)", Required: false),
            ["query"] = new("string", "Search query (for search_code)", Required: false),
            ["state"] = new("string", "Filter state: open, closed, or all (default: open)", Required: false,
                EnumValues: ["open", "closed", "all"]),
            ["per_page"] = new("string", "Number of results to return (1-100, default: 10)", Required: false)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var action = GetRequiredString(arguments, "action");
        var owner = GetOptionalString(arguments, "owner", _defaultOwner ?? string.Empty);
        var repo = GetOptionalString(arguments, "repo");
        var state = GetOptionalString(arguments, "state", "open");
        var perPage = GetOptionalInt(arguments, "per_page", 10);

        return action.ToLowerInvariant() switch
        {
            "get_repo" => await GetRepoAsync(owner, repo, cancellationToken),
            "list_issues" => await ListIssuesAsync(owner, repo, state, perPage, cancellationToken),
            "get_issue" => await GetIssueAsync(owner, repo, GetRequiredString(arguments, "number"), cancellationToken),
            "list_prs" => await ListPrsAsync(owner, repo, state, perPage, cancellationToken),
            "search_code" => await SearchCodeAsync(owner, repo, GetRequiredString(arguments, "query"), perPage, cancellationToken),
            _ => throw new ToolArgumentException($"Unknown action '{action}'")
        };
    }

    private async Task<string> GetRepoAsync(string owner, string repo, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        var json = await GetJsonAsync($"repos/{owner}/{repo}", ct).ConfigureAwait(false);
        if (json is not JsonObject obj) return "Repository not found";

        return FormatJson(new
        {
            full_name = obj["full_name"]?.GetValue<string>(),
            description = obj["description"]?.GetValue<string>(),
            language = obj["language"]?.GetValue<string>(),
            stars = obj["stargazers_count"]?.GetValue<int>(),
            forks = obj["forks_count"]?.GetValue<int>(),
            open_issues = obj["open_issues_count"]?.GetValue<int>(),
            default_branch = obj["default_branch"]?.GetValue<string>(),
            html_url = obj["html_url"]?.GetValue<string>(),
            visibility = obj["visibility"]?.GetValue<string>(),
            topics = obj["topics"]?.AsArray()?.Select(t => t?.GetValue<string>()).ToList()
        });
    }

    private async Task<string> ListIssuesAsync(string owner, string repo, string state, int perPage, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        var json = await GetJsonAsync($"repos/{owner}/{repo}/issues?state={state}&per_page={perPage}", ct).ConfigureAwait(false);
        if (json is not JsonArray items) return "No issues found";

        var issues = items.OfType<JsonObject>().Select(i => new
        {
            number = i["number"]?.GetValue<int>(),
            title = i["title"]?.GetValue<string>(),
            state = i["state"]?.GetValue<string>(),
            author = i["user"]?["login"]?.GetValue<string>(),
            created_at = i["created_at"]?.GetValue<string>(),
            html_url = i["html_url"]?.GetValue<string>()
        });
        return FormatJson(issues);
    }

    private async Task<string> GetIssueAsync(string owner, string repo, string number, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        if (!int.TryParse(number, out _))
            throw new ToolArgumentException("'number' must be a valid integer");
        var json = await GetJsonAsync($"repos/{owner}/{repo}/issues/{number}", ct).ConfigureAwait(false);
        if (json is not JsonObject obj) return "Issue not found";

        return FormatJson(new
        {
            number = obj["number"]?.GetValue<int>(),
            title = obj["title"]?.GetValue<string>(),
            state = obj["state"]?.GetValue<string>(),
            author = obj["user"]?["login"]?.GetValue<string>(),
            body = obj["body"]?.GetValue<string>(),
            labels = obj["labels"]?.AsArray()?.OfType<JsonObject>().Select(l => l["name"]?.GetValue<string>()).ToList(),
            html_url = obj["html_url"]?.GetValue<string>()
        });
    }

    private async Task<string> ListPrsAsync(string owner, string repo, string state, int perPage, CancellationToken ct)
    {
        ValidateOwnerRepo(owner, repo);
        var json = await GetJsonAsync($"repos/{owner}/{repo}/pulls?state={state}&per_page={perPage}", ct).ConfigureAwait(false);
        if (json is not JsonArray items) return "No pull requests found";

        var prs = items.OfType<JsonObject>().Select(p => new
        {
            number = p["number"]?.GetValue<int>(),
            title = p["title"]?.GetValue<string>(),
            state = p["state"]?.GetValue<string>(),
            author = p["user"]?["login"]?.GetValue<string>(),
            head = p["head"]?["label"]?.GetValue<string>(),
            base_branch = p["base"]?["label"]?.GetValue<string>(),
            html_url = p["html_url"]?.GetValue<string>()
        });
        return FormatJson(prs);
    }

    private async Task<string> SearchCodeAsync(string owner, string repo, string query, int perPage, CancellationToken ct)
    {
        var repoFilter = (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo))
            ? $"+repo:{owner}/{repo}" : string.Empty;
        var json = await GetJsonAsync(
            $"search/code?q={Uri.EscapeDataString(query)}{repoFilter}&per_page={perPage}", ct).ConfigureAwait(false);
        if (json is not JsonObject result) return "No results";

        var items = result["items"]?.AsArray()?.OfType<JsonObject>().Select(i => new
        {
            path = i["path"]?.GetValue<string>(),
            repo = i["repository"]?["full_name"]?.GetValue<string>(),
            html_url = i["html_url"]?.GetValue<string>()
        });
        return FormatJson(new
        {
            total_count = result["total_count"]?.GetValue<int>(),
            items
        });
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(body);
    }

    private static void ValidateOwnerRepo(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ToolArgumentException("'owner' is required (or set a default in GitHubToolsConfig)");
        if (string.IsNullOrWhiteSpace(repo))
            throw new ToolArgumentException("'repo' is required");
    }

    private static string FormatJson(object? value) =>
        JsonSerializer.Serialize(value, s_jsonOptions);
}
