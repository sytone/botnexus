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
                "name": { "type": "string", "description": "Optional friendly label for this sub-agent RUN. Accepted in every mode, including alongside targetAgentId - it titles the run, it does not customise the agent's descriptor." },
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
                "targetAgentId": { "type": "string", "description": "Optional registered agent ID to use as the sub-agent identity. When set, the sub-agent runs as this agent's descriptor instead of cloning the parent. Descriptor overrides (model, apiProvider, tools, systemPrompt, archetype) are refused alongside it; the run-scoped name, maxTurns and timeoutSeconds are accepted." },
                "shareWorkspace": { "type": "boolean", "description": "When true, grant the sub-agent read/write access to the parent agent's workspace. Default: false (isolated)." },
                "grantedPaths": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional list of absolute paths the sub-agent is granted READ-ONLY access to beyond its own workspace. Writes and edits to these paths are refused. Use grantedWritePaths for a specific writable directory, or shareWorkspace for the whole parent workspace."
                },
                "grantedWritePaths": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional list of absolute paths the sub-agent is granted READ AND WRITE access to beyond its own workspace. Use this when the sub-agent must produce files in a specific directory (for example a git worktree) without granting the entire parent workspace."
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
        var grantedWritePaths = ReadStringArray(arguments, "grantedWritePaths");

        // Phase 5 / F-6 step 3 (#562): Mode rejects mode-mixing.
        // When the caller asks to mirror an existing named agent, none of the
        // embody-only DESCRIPTOR fields may be supplied — Mirror is strict
        // pass-through of the target's full descriptor. Build the Mode union
        // here so DefaultSubAgentManager can prefer it over the legacy bag.
        // #3570: `name` is deliberately NOT in that set — it labels the run, not
        // the descriptor, exactly like maxTurns/timeoutSeconds which were always
        // accepted here.
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
            // #2338: binds the child conversation back to the exact spawn_subagent call so a channel
            // can render the run as an expandable card in place of it, instead of guessing the
            // association by timestamp.
            SpawningToolCallId = toolCallId,
            Mode = mode,
            ShareWorkspace = shareWorkspace,
            GrantedPaths = grantedPaths,
            GrantedWritePaths = grantedWritePaths
        };

        SubAgentInfo spawned;
        try
        {
            spawned = await subAgentManager.SpawnAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // #2633: cancellation is turn control flow, not a tool error. It must keep propagating
            // so the executor can unwind the turn rather than reporting a spurious failure.
            throw;
        }
        catch (Exception ex)
        {
            // #2633: a spawn that fails for a configuration reason (e.g. the descriptor names a
            // model that is not registered for its provider) is the caller's problem to correct,
            // not a host fault. Return it as a tool error carrying the underlying message - which
            // names the model and the provider - so the requesting agent can act on it, and so the
            // exception never escapes to become an unobserved task fault.
            return TextResult(JsonSerializer.Serialize(new
            {
                error = ex.Message,
                Status = "failed"
            }, JsonOptions));
        }
        // #2789: the clamp field is emitted ONLY when a ceiling actually reduced the request, so
        // its presence is the signal to re-scope. Serialized through two shapes rather than a
        // nullable property because a field that is always there (even as null) is boilerplate a
        // calling model stops reading, which is the failure this issue exists to fix.
        var result = spawned.BudgetClamp is { } clamp
            ? JsonSerializer.Serialize(new
            {
                spawned.SubAgentId,
                SessionId = spawned.ChildSessionId,
                ConversationId = spawned.ChildConversationId,
                spawned.Status,
                spawned.Name,
                BudgetClamp = new
                {
                    clamp.PolicyTier,
                    clamp.MaxTurnsClamped,
                    clamp.RequestedMaxTurns,
                    clamp.EffectiveMaxTurns,
                    clamp.TimeoutSecondsClamped,
                    clamp.RequestedTimeoutSeconds,
                    clamp.EffectiveTimeoutSeconds,
                    Notice = "Your requested budget exceeded a configured ceiling and was reduced. "
                        + "Scope the delegated task to the effective values above, not the requested ones."
                }
            }, JsonOptions)
            : JsonSerializer.Serialize(new
            {
                spawned.SubAgentId,
                SessionId = spawned.ChildSessionId,
                // #2338: the run's own conversation id. This is what a channel expands to load the
                // child's transcript on demand; it is deliberately NOT the caller's conversation id.
                ConversationId = spawned.ChildConversationId,
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
            var conflicts = new List<string>(5);
            // #3570: `name` is absent by design. It is a run label, not a descriptor
            // customisation, and rejecting it broke an automated PR-review workflow on
            // 100% of its invocations. Only genuine descriptor fields belong here.
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
                    + "or omit it and customise via embody fields. The run-scoped name, maxTurns and "
                    + "timeoutSeconds are accepted alongside targetAgentId.");
            }

            return new Mirror(AgentId.From(targetAgentId), string.IsNullOrWhiteSpace(name) ? null : name);
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
