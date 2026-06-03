using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Tool that updates fields on an existing registered agent.
/// </summary>
public sealed class UpdateAgentTool(
    IAgentRegistry agentRegistry,
    IAgentConfigurationWriter configurationWriter,
    IEnumerable<IAgentChangeNotifier> changeNotifiers) : IAgentTool
{
    public string Name => "update_agent";
    public string Label => "Update Agent";

    public Tool Definition => new(
        Name,
        "Update fields on an existing registered agent. Only provided fields are changed; omitted fields are preserved.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "Agent ID to update."
                },
                "displayName": {
                  "type": "string",
                  "description": "New human-readable display name."
                },
                "description": {
                  "type": "string",
                  "description": "New description."
                },
                "emoji": {
                  "type": "string",
                  "description": "New emoji."
                },
                "modelId": {
                  "type": "string",
                  "description": "New LLM model identifier."
                },
                "apiProvider": {
                  "type": "string",
                  "description": "New API provider key."
                },
                "systemPrompt": {
                  "type": "string",
                  "description": "New system prompt."
                },
                "toolIds": {
                  "type": "string",
                  "description": "Optional JSON array string of tool IDs (replaces existing list)."
                }
              },
              "required": ["id"]
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
        if (string.IsNullOrWhiteSpace(id))
            return Error("Parameter 'id' is required.");

        var agentId = AgentId.From(id);
        var existing = agentRegistry.Get(agentId);
        if (existing is null)
            return Error($"Agent '{id}' is not registered.");

        var updated = existing;

        if (arguments.ContainsKey("displayName") && ReadString(arguments, "displayName") is { } dn)
            updated = updated with { DisplayName = dn };
        if (arguments.ContainsKey("description"))
            updated = updated with { Description = ReadString(arguments, "description") };
        if (arguments.ContainsKey("emoji"))
            updated = updated with { Emoji = ReadString(arguments, "emoji") };
        if (arguments.ContainsKey("modelId") && ReadString(arguments, "modelId") is { } mid)
            updated = updated with { ModelId = mid };
        if (arguments.ContainsKey("apiProvider") && ReadString(arguments, "apiProvider") is { } ap)
            updated = updated with { ApiProvider = ap };
        if (arguments.ContainsKey("systemPrompt"))
            updated = updated with { SystemPrompt = ReadString(arguments, "systemPrompt") };
        if (arguments.ContainsKey("toolIds"))
            updated = updated with { ToolIds = ParseToolIds(ReadString(arguments, "toolIds")) };

        agentRegistry.Update(agentId, updated);
        await configurationWriter.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        foreach (var notifier in changeNotifiers)
        {
            try
            {
                await notifier.NotifyAgentsChangedAsync("updated", id, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        return new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, JsonSerializer.Serialize(updated, JsonOptions))]);
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
}
