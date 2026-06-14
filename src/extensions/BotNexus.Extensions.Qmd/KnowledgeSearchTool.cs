using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Agent-facing tool for searching the QMD knowledge base.
/// Supports keyword, semantic, and hybrid search modes.
/// </summary>
public sealed class KnowledgeSearchTool(IQmdBackend backend, QmdConfig config) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Name => "knowledge_search";

    /// <inheritdoc />
    public string Label => "Knowledge Search";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Search the knowledge base using keyword, semantic, or hybrid search.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "Search query (natural language or keywords)."
                },
                "store": {
                  "type": "string",
                  "description": "Target store name. Omit to search all stores."
                },
                "mode": {
                  "type": "string",
                  "enum": ["keyword", "semantic", "hybrid"],
                  "description": "Search mode. Default from config."
                },
                "limit": {
                  "type": "integer",
                  "description": "Maximum results to return (1-50). Default from config."
                }
              },
              "required": ["query"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return TextResult("Error: The 'query' parameter is required.");

        var store = GetString(arguments, "store");

        if (!string.IsNullOrWhiteSpace(store) && !config.IsStoreAllowed(store))
            return TextResult($"Error: Access denied. Store '{store}' is not in your allowed stores.");

        var modeStr = GetString(arguments, "mode") ?? config.DefaultSearchMode;
        var limit = GetInt(arguments, "limit") ?? config.MaxResults;

        if (limit < 1) limit = 1;
        if (limit > 50) limit = 50;

        var mode = modeStr?.ToLowerInvariant() switch
        {
            "keyword" => QmdSearchMode.Keyword,
            "semantic" => QmdSearchMode.Semantic,
            "hybrid" => QmdSearchMode.Hybrid,
            _ => QmdSearchMode.Hybrid
        };

        try
        {
            var results = await backend.SearchAsync(query, store, mode, limit, cancellationToken);
            var json = JsonSerializer.Serialize(results, JsonOptions);
            return TextResult(json);
        }
        catch (QmdBinaryNotFoundException ex)
        {
            return TextResult($"Error: {ex.Message}");
        }
        catch (QmdCliException ex)
        {
            return TextResult($"Error: Search failed: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            return TextResult($"Error: {ex.Message}");
        }
    }

    private static AgentToolResult TextResult(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static string? GetString(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static int? GetInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is int i) return i;
        if (val is long l) return SaturateToInt32(l);
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return ReadNumberElement(je);
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    /// <summary>
    /// Reads a JSON number element without throwing on values that overflow <see cref="int"/>
    /// or carry a fractional component. Out-of-range and non-integer numbers (e.g.
    /// <c>9999999999</c> or <c>1.5</c>) saturate/truncate to a representable <see cref="int"/>
    /// instead of propagating a <see cref="FormatException"/>/<see cref="InvalidOperationException"/>
    /// out of the tool call. The caller still clamps the result to the configured <c>[1, 50]</c> range,
    /// so saturating here lets a too-large value resolve to the clamped maximum rather than crashing.
    /// </summary>
    private static int? ReadNumberElement(JsonElement je)
    {
        if (je.TryGetInt32(out var i)) return i;
        if (je.TryGetInt64(out var l)) return SaturateToInt32(l);
        if (je.TryGetDouble(out var d)) return SaturateToInt32(d);
        return null;
    }

    private static int SaturateToInt32(long value) =>
        value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private static int SaturateToInt32(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value >= int.MaxValue) return int.MaxValue;
        if (value <= int.MinValue) return int.MinValue;
        return (int)value;
    }
}
