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
        if (val is long l) return (int)l;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetInt32();
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }
}
