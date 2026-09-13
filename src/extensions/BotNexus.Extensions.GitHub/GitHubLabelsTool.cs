using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.GitHub;

/// <summary>Lists or creates repository labels through the REST API.</summary>
public sealed partial class GitHubLabelsTool : GitHubToolBase
{
    public GitHubLabelsTool(IGitHubApiClient api, GitHubToolsConfig config) : base(api, config) { }

    public override string Name => "github_labels";
    public override string Label => "GitHub Labels";

    public override Tool Definition => new(
        Name,
        "List or create GitHub repository labels with explicit pagination for list results.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "repository": { "type": "string", "description": "Target repository as 'owner/repo'. Defaults to the agent's configured repository." },
                "action": { "type": "string", "enum": ["list", "create"], "description": "Label operation. Default: list." },
                "name": { "type": "string", "description": "Label name. Required for create." },
                "color": { "type": "string", "description": "Six-digit hexadecimal color, with or without '#'. Required for create." },
                "description": { "type": "string", "description": "Optional label description." },
                "perPage": { "type": "integer", "description": "Results per page for list. Clamped to the configured maximum." },
                "page": { "type": "integer", "description": "1-based page number for list. Default: 1." }
              }
            }
            """));

    protected override void Prepare(
        IReadOnlyDictionary<string, object?> arguments,
        IDictionary<string, object?> prepared)
    {
        var action = ReadString(arguments, "action") ?? "list";
        if (action is not ("list" or "create"))
            throw new ArgumentException("action must be one of: list, create.");

        prepared["action"] = action;
        if (action == "list")
        {
            var page = ReadInt(arguments, "page") ?? 1;
            if (page < 1) throw new ArgumentException("page must be 1 or greater.");
            var requestedPerPage = ReadInt(arguments, "perPage");
            prepared["page"] = page;
            prepared["requestedPerPage"] = requestedPerPage;
            prepared["perPage"] = ClampPageSize(requestedPerPage);
            return;
        }

        var name = RequireString(arguments, "name");
        var color = RequireString(arguments, "color").TrimStart('#');
        if (!HexColor().IsMatch(color))
            throw new ArgumentException("color must be exactly six hexadecimal characters, with or without '#'.");

        prepared["name"] = name;
        prepared["color"] = color.ToUpperInvariant();
        prepared["description"] = ReadString(arguments, "description");
    }

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var repository = (string)arguments["repository"]!;
        var action = (string)arguments["action"]!;
        if (action == "list")
        {
            var page = (int)arguments["page"]!;
            var perPage = (int)arguments["perPage"]!;
            var requestedPerPage = arguments["requestedPerPage"] as int?;
            var response = await Api.SendAsync(
                HttpMethod.Get,
                $"repos/{repository}/labels?per_page={perPage}&page={page}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess || response.Body is not { ValueKind: JsonValueKind.Array } labels)
                return ErrorResult(Name, repository, response);

            var projected = labels.EnumerateArray().Select(GitHubProjections.Label).ToArray();
            return StructuredResult(new
            {
                tool = Name,
                repository,
                ok = true,
                action,
                page,
                perPage,
                perPageClamped = requestedPerPage is { } requested && requested != perPage,
                count = projected.Length,
                hasMore = projected.Length == perPage,
                labels = projected,
            });
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = arguments["name"],
            ["color"] = arguments["color"],
        };
        AddIfPresent(payload, "description", arguments["description"]);
        var created = await Api.SendAsync(
            HttpMethod.Post, $"repos/{repository}/labels", payload, cancellationToken).ConfigureAwait(false);

        if (!created.IsSuccess || created.Body is not { } label)
            return ErrorResult(Name, repository, created);

        return StructuredResult(new
        {
            tool = Name,
            repository,
            ok = true,
            action,
            identity = Config.Identity,
            label = GitHubProjections.Label(label),
        });
    }

    [GeneratedRegex("^[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}
