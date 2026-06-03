using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Tool that creates a new agent and registers it in the gateway.
/// </summary>
public sealed class CreateAgentTool(
    IAgentRegistry agentRegistry,
    IAgentConfigurationWriter configurationWriter,
    IEnumerable<IAgentChangeNotifier> changeNotifiers,
    BotNexusHome botNexusHome) : IAgentTool
{
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]*[a-z0-9]$", RegexOptions.Compiled);

    public string Name => "create_agent";
    public string Label => "Create Agent";

    public Tool Definition => new(
        Name,
        "Create and register a new agent in the gateway. The agent will be available immediately after creation.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "Agent slug identifier. Must match ^[a-z0-9][a-z0-9-]*[a-z0-9]$ and be 2-64 characters.",
                  "minLength": 2,
                  "maxLength": 64
                },
                "displayName": {
                  "type": "string",
                  "description": "Human-readable display name for the agent."
                },
                "description": {
                  "type": "string",
                  "description": "Optional description of the agent's purpose and capabilities."
                },
                "emoji": {
                  "type": "string",
                  "description": "Optional emoji that visually identifies this agent."
                },
                "modelId": {
                  "type": "string",
                  "description": "The LLM model identifier (e.g., 'claude-sonnet-4-20250514')."
                },
                "apiProvider": {
                  "type": "string",
                  "description": "The API provider key (e.g., 'anthropic', 'openai', 'copilot')."
                },
                "systemPrompt": {
                  "type": "string",
                  "description": "Optional system prompt for the agent."
                },
                "toolIds": {
                  "type": "string",
                  "description": "Optional JSON array string of tool IDs the agent has access to (e.g., '[\"read\",\"write\"]')."
                }
              },
              "required": ["id", "displayName", "modelId", "apiProvider"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var id = ReadString(arguments, "id");
        var displayName = ReadString(arguments, "displayName");
        var modelId = ReadString(arguments, "modelId");
        var apiProvider = ReadString(arguments, "apiProvider");

        if (string.IsNullOrWhiteSpace(id))
            return Error("Parameter 'id' is required.");

        if (id.Length < 2 || id.Length > 64 || !IdPattern.IsMatch(id))
            return Error($"Invalid agent ID '{id}'. Must match ^[a-z0-9][a-z0-9-]*[a-z0-9]$, 2-64 chars.");

        if (string.IsNullOrWhiteSpace(displayName))
            return Error("Parameter 'displayName' is required.");

        if (string.IsNullOrWhiteSpace(modelId))
            return Error("Parameter 'modelId' is required.");

        if (string.IsNullOrWhiteSpace(apiProvider))
            return Error("Parameter 'apiProvider' is required.");

        var agentId = AgentId.From(id);
        if (agentRegistry.Contains(agentId))
            return Error($"An agent with ID '{id}' is already registered.");

        var toolIds = ParseToolIds(ReadString(arguments, "toolIds"));

        var descriptor = new AgentDescriptor
        {
            AgentId = agentId,
            DisplayName = displayName,
            Description = ReadString(arguments, "description"),
            Emoji = ReadString(arguments, "emoji"),
            ModelId = modelId,
            ApiProvider = apiProvider,
            SystemPrompt = ReadString(arguments, "systemPrompt"),
            ToolIds = toolIds
        };

        agentRegistry.Register(descriptor);
        await configurationWriter.SaveAsync(descriptor, cancellationToken).ConfigureAwait(false);
        botNexusHome.GetAgentDirectory(id);

        foreach (var notifier in changeNotifiers)
        {
            try
            {
                await notifier.NotifyAgentsChangedAsync("added", id, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        var result = new AgentCreatedResult(
            AgentId: id,
            DisplayName: displayName,
            ModelId: modelId,
            ApiProvider: apiProvider,
            Status: "created");

        return new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, JsonSerializer.Serialize(result, JsonOptions))]);
    }

    private static IReadOnlyList<string> ParseToolIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw);
            return parsed ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static AgentToolResult Error(string message)
    {
        var payload = JsonSerializer.Serialize(new { error = message }, JsonOptions);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, payload)]);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AgentCreatedResult(
        string AgentId,
        string DisplayName,
        string ModelId,
        string ApiProvider,
        string Status);
}
