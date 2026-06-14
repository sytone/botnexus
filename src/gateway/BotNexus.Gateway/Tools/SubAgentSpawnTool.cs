using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class SubAgentSpawnTool(
    ISubAgentManager subAgentManager,
    AgentId agentId,
    SessionId sessionId,
    ConversationId conversationId) : IAgentTool
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
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Optional timeout in seconds. Values above the configured ceiling are clamped down." }
                ,
                "archetype": {
                  "type": "string",
                  "enum": ["researcher", "coder", "planner", "reviewer", "writer", "general"],
                  "description": "Optional behavioral archetype for the sub-agent."
                },
                "targetAgentId": { "type": "string", "description": "Optional registered agent ID to use as the sub-agent identity. When set, the sub-agent runs as this agent's descriptor instead of cloning the parent." },
                "shareWorkspace": { "type": "boolean", "description": "When true, grant the sub-agent read/write access to the parent agent's workspace. Default: false (isolated)." },
                "grantedPaths": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional list of absolute paths the sub-agent is granted read access to beyond its own workspace."
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

        var name = ReadString(arguments, "name");
        var modelOverride = ReadString(arguments, "model");
        var apiProviderOverride = ReadString(arguments, "apiProvider");
        var toolIds = ReadStringArray(arguments, "tools");
        var systemPromptOverride = ReadString(arguments, "systemPrompt");
        var archetypeRaw = ReadString(arguments, "archetype");
        var targetAgentId = ReadString(arguments, "targetAgentId");
        var shareWorkspace = ReadBool(arguments, "shareWorkspace");
        var grantedPaths = ReadStringArray(arguments, "grantedPaths");

        // Phase 5 / F-6 step 3 (#562): Mode rejects mode-mixing.
        // When the caller asks to mirror an existing named agent, none of the
        // embody-only customisation fields may be supplied — Mirror is strict
        // pass-through of the target's full descriptor. Build the Mode union
        // here so DefaultSubAgentManager can prefer it over the legacy bag.
        var mode = BuildSpawnMode(
            targetAgentId: targetAgentId,
            name: name,
            modelOverride: modelOverride,
            apiProviderOverride: apiProviderOverride,
            toolIds: toolIds,
            systemPromptOverride: systemPromptOverride,
            archetypeRaw: archetypeRaw);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = agentId,
            ParentSessionId = sessionId,
            Task = task,
            MaxTurns = ReadInt(arguments, "maxTurns", 30),
            TimeoutSeconds = ReadInt(arguments, "timeoutSeconds", 600),
            InheritedConversationId = conversationId,
            Mode = mode,
            ShareWorkspace = shareWorkspace,
            GrantedPaths = grantedPaths
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

    private static SubAgentSpawnMode BuildSpawnMode(
        string? targetAgentId,
        string? name,
        string? modelOverride,
        string? apiProviderOverride,
        IReadOnlyList<string>? toolIds,
        string? systemPromptOverride,
        string? archetypeRaw)
    {
        if (!string.IsNullOrWhiteSpace(targetAgentId))
        {
            var conflicts = new List<string>(6);
            if (!string.IsNullOrWhiteSpace(name)) conflicts.Add("name");
            if (!string.IsNullOrWhiteSpace(modelOverride)) conflicts.Add("model");
            if (!string.IsNullOrWhiteSpace(apiProviderOverride)) conflicts.Add("apiProvider");
            if (toolIds is { Count: > 0 }) conflicts.Add("tools");
            if (!string.IsNullOrWhiteSpace(systemPromptOverride)) conflicts.Add("systemPrompt");
            if (!string.IsNullOrWhiteSpace(archetypeRaw)) conflicts.Add("archetype");

            if (conflicts.Count > 0)
            {
                throw new ArgumentException(
                    $"targetAgentId is incompatible with embody-only fields: {string.Join(", ", conflicts)}. "
                    + "Mirror mode runs the target agent's full descriptor verbatim — supply targetAgentId alone, "
                    + "or omit it and customise via embody fields.");
            }

            return new Mirror(AgentId.From(targetAgentId));
        }

        var archetype = ResolveArchetype(archetypeRaw);
        var customizations = HasAnyEmbodyCustomization(name, modelOverride, apiProviderOverride, toolIds, systemPromptOverride)
            ? new EmbodyCustomizations
            {
                Name = name,
                ModelOverride = modelOverride,
                ApiProviderOverride = apiProviderOverride,
                ToolIds = toolIds,
                SystemPromptOverride = systemPromptOverride
            }
            : EmbodyCustomizations.Default;

        return new Embody(archetype, customizations);
    }

    private static bool HasAnyEmbodyCustomization(
        string? name,
        string? modelOverride,
        string? apiProviderOverride,
        IReadOnlyList<string>? toolIds,
        string? systemPromptOverride)
        => !string.IsNullOrWhiteSpace(name)
        || !string.IsNullOrWhiteSpace(modelOverride)
        || !string.IsNullOrWhiteSpace(apiProviderOverride)
        || toolIds is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(systemPromptOverride);

    private static SubAgentArchetype ResolveArchetype(string? archetypeRaw)
        => string.IsNullOrWhiteSpace(archetypeRaw)
            ? SubAgentArchetype.General
            : SubAgentArchetype.FromString(archetypeRaw);

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

    private static bool ReadBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return false;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            bool b => b,
            _ => false
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
