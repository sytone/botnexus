using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Agent-facing tool for listing available knowledge stores with metadata.
/// </summary>
public sealed class KnowledgeStoresTool(IQmdBackend backend, QmdConfig config) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Name => "knowledge_stores";

    /// <inheritdoc />
    public string Label => "Knowledge Stores";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "List available knowledge stores with descriptions, document counts, and health status.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        QmdStoreInfo[] stores;
        try
        {
            stores = await backend.GetStoresAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                $"Failed to retrieve knowledge stores: {ex.Message}")]);
        }

        // Enrich with descriptions from config
        var configDescriptions = config.Stores
            .Where(s => !string.IsNullOrWhiteSpace(s.Description))
            .ToDictionary(s => s.Name, s => s.Description!, StringComparer.OrdinalIgnoreCase);

        var output = stores.Select(s => new
        {
            s.Name,
            Description = configDescriptions.TryGetValue(s.Name, out var desc) ? desc : s.Description,
            s.DocumentCount,
            LastUpdated = s.LastUpdated?.ToString("O"),
            s.Healthy
        }).ToArray();

        if (output.Length == 0)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                "No knowledge stores are configured or available.")]);

        var json = JsonSerializer.Serialize(output, JsonOptions);
        var hint = "\n\nUse knowledge_search with store='<name>' to search a specific store, or omit store to search all.";
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, json + hint)]);
    }
}
