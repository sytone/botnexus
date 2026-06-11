using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Agent-facing tool for retrieving a specific document from the knowledge base by ID or path.
/// </summary>
public sealed class KnowledgeGetTool(IQmdBackend backend) : IAgentTool
{
    private const int MaxContentChars = 50_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Name => "knowledge_get";

    /// <inheritdoc />
    public string Label => "Knowledge Get";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Retrieve a specific document from the knowledge base by ID or path.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "Document ID (#abc123 format) or path (store/path/to/file.md)."
                }
              },
              "required": ["id"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("id", out var idValue) || string.IsNullOrWhiteSpace(ToStringValue(idValue)))
            throw new ArgumentException("Missing required argument: id.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = ToStringValue(idValue)! });
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var id = ToStringValue(arguments["id"]) ?? string.Empty;

        QmdDocument? document;
        try
        {
            document = await backend.GetDocumentAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                $"Failed to retrieve document: {ex.Message}")]);
        }

        if (document is null)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                $"Document not found: '{id}'")]);

        var content = document.Content;
        var truncated = false;
        if (content.Length > MaxContentChars)
        {
            content = content[..MaxContentChars];
            truncated = true;
        }

        var output = new
        {
            document.Id,
            document.Store,
            document.Path,
            document.Title,
            Content = content + (truncated ? "\n\n[truncated — document exceeds 50K characters]" : string.Empty)
        };

        var json = JsonSerializer.Serialize(output, JsonOptions);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, json)]);
    }

    private static string? ToStringValue(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
}
