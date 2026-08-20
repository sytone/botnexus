using BotNexus.Agent.Core.Tools;
using System.Text.Json;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Derives the remedy sentence appended to a truncation marker from the DECLARED PARAMETERS of the
/// tool that was actually invoked (issue #3404, the deferred clause 3 of #2760).
/// </summary>
/// <remarks>
/// <para>
/// The pre-#3404 marker appended one unconditional sentence - "rerun with a narrower scope,
/// paginate, or select fewer items" - to every result regardless of origin. The #2760 forensics
/// window recorded the failing calls as <c>read</c>, <c>exec</c> and <c>shell</c>, none of which
/// declare the dials that wording implies; <c>shell</c> and <c>exec</c> declare no pagination
/// parameter at all. Telling a caller to turn a dial that is not on its console produced identical
/// retries at identical byte counts, because there was nothing it could change.
/// </para>
/// <para>
/// The authoritative list of dials a caller actually has is the tool's own JSON Schema, so that is
/// what this type reads. Every failure path - a null tool, an absent or malformed schema, a schema
/// with no <c>properties</c> object - falls open to <see cref="ToolOutputBudget.NarrowingGuidance"/>
/// rather than throwing or emitting an empty remedy: accurate-but-vague beats confidently wrong,
/// and a size backstop must never be the thing that throws.
/// </para>
/// </remarks>
internal static class ToolOutputRemedy
{
    /// <summary>
    /// Parameter names that move a window's START. Matched case-insensitively against the tool's
    /// declared property names; only names the tool really declares are ever quoted back.
    /// </summary>
    private static readonly string[] OffsetNames =
    [
        "offset", "start_index", "startIndex", "skip", "$skip", "cursor", "page", "start",
        "continuationToken", "continuation_token", "after",
    ];

    /// <summary>
    /// Parameter names that bound a window's SIZE.
    /// </summary>
    private static readonly string[] LimitNames =
    [
        "limit", "max_length", "maxLength", "top", "$top", "count", "per_page", "page_size",
        "pageSize", "maxResults", "max_results", "tail", "first", "take",
    ];

    /// <summary>
    /// Parameter names that narrow WHICH data comes back rather than how much of it.
    /// </summary>
    private static readonly string[] NarrowingNames =
    [
        "select", "$select", "filter", "$filter", "fields", "query", "pattern", "glob", "path",
    ];

    /// <summary>
    /// Selects the remedy sentence for a truncated result produced by <paramref name="tool"/>.
    /// </summary>
    /// <param name="tool">
    /// The resolved tool that produced the result, or <c>null</c> when the executor had no resolved
    /// tool (an argument-validation failure truncated before dispatch, for instance).
    /// </param>
    /// <returns>
    /// A sentence naming only parameters the tool genuinely declares, or
    /// <see cref="ToolOutputBudget.NarrowingGuidance"/> when nothing better can be established.
    /// </returns>
    public static string ForTool(IAgentTool? tool)
    {
        if (tool is null)
        {
            return ToolOutputBudget.NarrowingGuidance;
        }

        IReadOnlyCollection<string>? declared;
        string toolName;
        try
        {
            toolName = tool.Name;
            declared = ReadDeclaredParameterNames(tool.Definition?.Parameters);
        }
        catch (Exception)
        {
            // A third-party or MCP-bridged tool can throw from its own property getters. The budget
            // is a backstop; it fails open rather than converting an oversized result into a crash.
            return ToolOutputBudget.NarrowingGuidance;
        }

        if (declared is null)
        {
            return ToolOutputBudget.NarrowingGuidance;
        }

        return Compose(toolName, declared);
    }

    /// <summary>
    /// Composes the sentence from an already-resolved set of declared parameter names. Exposed for
    /// tests so the selection rule can be pinned without constructing a whole tool.
    /// </summary>
    internal static string Compose(string toolName, IReadOnlyCollection<string> declaredParameters)
    {
        var offsets = Matching(declaredParameters, OffsetNames);
        var limits = Matching(declaredParameters, LimitNames);
        var narrowers = Matching(declaredParameters, NarrowingNames);

        var paging = offsets.Concat(limits).ToArray();
        if (paging.Length > 0)
        {
            return $"This result succeeded but was too large to return in full - rerun `{toolName}` with {Quote(paging)} to page through it.";
        }

        if (narrowers.Length > 0)
        {
            return $"This result succeeded but was too large to return in full - rerun `{toolName}` with a narrower {Quote(narrowers)} to return less data.";
        }

        return $"This result succeeded but was too large to return in full - `{toolName}` declares no pagination parameters, so narrowing the request will not help; retrieve the remainder through the continuation handle below, or have the command write its output to a file and read it back in slices.";
    }

    /// <summary>
    /// Reads the property names from a JSON Schema <c>parameters</c> element.
    /// </summary>
    /// <returns>
    /// The declared names, or <c>null</c> when the schema is absent or not shaped like an object
    /// schema - the signal to fall open to the generic sentence (AC4).
    /// </returns>
    private static IReadOnlyCollection<string>? ReadDeclaredParameterNames(JsonElement? parameters)
    {
        if (parameters is not { } schema || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names.Count == 0 ? null : names;
    }

    private static string[] Matching(IReadOnlyCollection<string> declared, string[] candidates)
        => declared
            .Where(name => candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    private static string Quote(IReadOnlyList<string> names)
    {
        var quoted = names.Select(name => $"`{name}`").ToArray();
        return quoted.Length switch
        {
            1 => quoted[0],
            2 => $"{quoted[0]}/{quoted[1]}",
            _ => string.Join(", ", quoted[..^1]) + " and " + quoted[^1],
        };
    }
}
