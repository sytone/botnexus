using System.Text.Json;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Projects GitHub's REST JSON into the small, stable objects the tools return.
/// </summary>
/// <remarks>
/// <para>These projections are the concrete answer to "results are structured data, not command
/// output" (#2627 AC4). Each one names the fields agents actually consume, so a caller reads
/// <c>number</c> or <c>state</c> as a field instead of running a <c>--jq</c> filter over text - the
/// filter that, under PowerShell's nested-quoting preflight, forced 84 throwaway temp scripts.</para>
/// <para>They are also a deliberate size bound: GitHub's issue payload is ~40 fields and most of it
/// is URLs the agent cannot use. Projecting keeps the transcript cost proportional to the useful
/// content.</para>
/// </remarks>
internal static class GitHubProjections
{
    /// <summary>Projects an issue or pull-request object.</summary>
    internal static Dictionary<string, object?> Issue(JsonElement element) => new(StringComparer.Ordinal)
    {
        ["number"] = Int(element, "number"),
        ["title"] = Str(element, "title"),
        ["state"] = Str(element, "state"),
        ["author"] = Str(Obj(element, "user"), "login"),
        ["body"] = Str(element, "body"),
        ["labels"] = Labels(element),
        ["comments"] = Int(element, "comments"),
        ["createdAt"] = Str(element, "created_at"),
        ["updatedAt"] = Str(element, "updated_at"),
        ["url"] = Str(element, "html_url"),
        // Present only on pull requests. Its presence is how a caller distinguishes the two without
        // a second request - GitHub returns PRs from the issues endpoint too.
        ["isPullRequest"] = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("pull_request", out _),
    };

    /// <summary>Projects the pull-request-specific fields on top of the shared issue shape.</summary>
    internal static Dictionary<string, object?> PullRequest(JsonElement element)
    {
        var projected = Issue(element);
        projected["draft"] = Bool(element, "draft");
        projected["merged"] = Bool(element, "merged");
        projected["mergeable"] = Bool(element, "mergeable");
        projected["headRef"] = Str(Obj(element, "head"), "ref");
        projected["baseRef"] = Str(Obj(element, "base"), "ref");
        projected["additions"] = Int(element, "additions");
        projected["deletions"] = Int(element, "deletions");
        projected["changedFiles"] = Int(element, "changed_files");
        projected["isPullRequest"] = true;
        return projected;
    }

    /// <summary>Projects a comment object.</summary>
    internal static Dictionary<string, object?> Comment(JsonElement element) => new(StringComparer.Ordinal)
    {
        ["id"] = Int(element, "id"),
        ["author"] = Str(Obj(element, "user"), "login"),
        ["body"] = Str(element, "body"),
        ["createdAt"] = Str(element, "created_at"),
        ["url"] = Str(element, "html_url"),
    };

    /// <summary>Projects a single check run from the check-runs endpoint.</summary>
    /// <remarks>
    /// <c>conclusion</c> is null while a run is still in flight. Keeping it null rather than
    /// substituting "pending" preserves the distinction between "not finished" and "finished with an
    /// unknown outcome" - collapsing them is how a caller concludes a red PR is merely slow.
    /// </remarks>
    internal static Dictionary<string, object?> CheckRun(JsonElement element) => new(StringComparer.Ordinal)
    {
        ["id"] = Int(element, "id"),
        ["name"] = Str(element, "name"),
        ["status"] = Str(element, "status"),
        ["conclusion"] = Str(element, "conclusion"),
        ["startedAt"] = Str(element, "started_at"),
        ["completedAt"] = Str(element, "completed_at"),
        ["url"] = Str(element, "html_url"),
    };

    /// <summary>Projects one changed file from the pull-request files endpoint.</summary>
    /// <remarks>
    /// The unified <c>patch</c> hunk is projected only when the caller asks for it. It is unbounded
    /// text and a large PR would otherwise spend the whole transcript budget on a diff the agent
    /// only wanted the file list from.
    /// </remarks>
    internal static Dictionary<string, object?> PullRequestFile(JsonElement element, bool includePatch)
    {
        var projected = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = Str(element, "filename"),
            ["status"] = Str(element, "status"),
            ["additions"] = Int(element, "additions"),
            ["deletions"] = Int(element, "deletions"),
            ["changes"] = Int(element, "changes"),
            ["previousPath"] = Str(element, "previous_filename"),
        };

        if (includePatch)
            projected["patch"] = Str(element, "patch");

        return projected;
    }

    /// <summary>Projects one workflow run.</summary>
    internal static Dictionary<string, object?> WorkflowRun(JsonElement element) => new(StringComparer.Ordinal)
    {
        ["id"] = Int(element, "id"),
        ["name"] = Str(element, "name"),
        ["workflowId"] = Int(element, "workflow_id"),
        ["runNumber"] = Int(element, "run_number"),
        ["attempt"] = Int(element, "run_attempt"),
        ["event"] = Str(element, "event"),
        ["status"] = Str(element, "status"),
        ["conclusion"] = Str(element, "conclusion"),
        ["branch"] = Str(element, "head_branch"),
        ["headSha"] = Str(element, "head_sha"),
        ["createdAt"] = Str(element, "created_at"),
        ["updatedAt"] = Str(element, "updated_at"),
        ["url"] = Str(element, "html_url"),
    };

    private static object?[] Labels(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("labels", out var labels)
            || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labels.EnumerateArray()
            .Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : Str(l, "name"))
            .Where(n => n is not null)
            .Cast<object?>()
            .ToArray();
    }

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child)
            ? child
            : default;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
