using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class SubAgentSpawnTool(
    ISubAgentManager subAgentManager,
    AgentId agentId,
    SessionId sessionId) : IAgentTool
{
    public string Name => "spawn_subagent";
    public string Label => "Spawn Sub-Agent";

    public Tool Definition => new(
        Name,
        "Spawn a background sub-agent to work on a delegated task.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "task": { "type": "string", "description": "Task prompt for the sub-agent." },
                "name": { "type": "string", "description": "Optional friendly name for this sub-agent run." },
                "model": { "type": "string", "description": "Optional model override for the sub-agent run." },
                "apiProvider": { "type": "string", "description": "Optional API provider override for the sub-agent run." },
                "tools": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional allowlist of tool names for the sub-agent."
                },
                "systemPrompt": { "type": "string", "description": "Optional system prompt override." },
                "maxTurns": { "type": "integer", "minimum": 1, "description": "Optional max turn budget." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Optional timeout in seconds." }
                ,
                "archetype": {
                  "type": "string",
                  "enum": ["researcher", "coder", "planner", "reviewer", "writer", "general"],
                  "description": "Optional behavioral archetype for the sub-agent."
                }
              },
              "required": ["task"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = ReadString(arguments, "task");
        if (string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("Missing required argument: task.");

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var task = ReadString(arguments, "task")
            ?? throw new ArgumentException("Missing required argument: task.");

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = agentId,
            ParentSessionId = sessionId,
            Task = task,
            Name = ReadString(arguments, "name"),
            ModelOverride = ReadString(arguments, "model"),
            ApiProviderOverride = ReadString(arguments, "apiProvider"),
            ToolIds = ReadStringArray(arguments, "tools"),
            SystemPromptOverride = ReadString(arguments, "systemPrompt"),
            MaxTurns = ReadInt(arguments, "maxTurns", 30),
            TimeoutSeconds = ReadInt(arguments, "timeoutSeconds", 600),
            Archetype = ReadArchetype(arguments)
        };

        var spawned = await subAgentManager.SpawnAsync(request, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Serialize(new
        {
            spawned.SubAgentId,
            SessionId = spawned.ChildSessionId,
            spawned.Status,
            spawned.Name
        }, JsonOptions);

        return TextResult(result);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } el when int.TryParse(el.GetString(), out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            int number => number,
            double d => (int)d,
            string text when int.TryParse(text, out var number) => number,
            _ => defaultValue
        };
    }

    private static IReadOnlyList<string>? ReadStringArray(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            var items = array
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
            return items.Length == 0 ? null : items;
        }

        if (value is IEnumerable<string> enumerable)
        {
            var items = enumerable.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return items.Length == 0 ? null : items;
        }

        return null;
    }

    private static SubAgentArchetype ReadArchetype(IReadOnlyDictionary<string, object?> args)
    {
        var archetype = ReadString(args, "archetype");
        return string.IsNullOrWhiteSpace(archetype)
            ? SubAgentArchetype.General
            : SubAgentArchetype.FromString(archetype);
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
