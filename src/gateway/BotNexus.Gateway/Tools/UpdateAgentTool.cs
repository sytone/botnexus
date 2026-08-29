using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Tool that updates fields on an existing registered agent.
/// </summary>
public sealed class UpdateAgentTool(
    IAgentRegistry agentRegistry,
    IAgentConfigurationWriter configurationWriter,
    IEnumerable<IAgentChangeNotifier> changeNotifiers,
    ModelRegistry? modelRegistry = null,
    AgentId? callerAgentId = null,
    AgentSummaryOptions? summaryOptions = null) : IAgentTool
{
    public string Name => "update_agent";
    public string Label => "Update Agent";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Returns a locally generated update confirmation.</summary>
    public string ContentSource => ToolContentSource.Local;

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
                "summary": {
                  "type": "string",
                  "description": "Agent-maintained summary of what you are currently doing. You may set this only on YOUR OWN agent id; setting it on another agent is refused. Pass an empty string to clear it."
                },
                "emoji": {
                  "type": "string",
                  "description": "New emoji."
                },
                "modelId": {
                  "type": "string",
                  "description": "New LLM model identifier. Must be registered for the agent's apiProvider. Persisted as 'model' in config.json."
                },
                "apiProvider": {
                  "type": "string",
                  "description": "New provider instance key as registered in the model registry (e.g., 'github-copilot', 'anthropic') - NOT an API contract name such as 'github-copilot-messages'. Persisted as 'provider' in config.json."
                },
                "systemPrompt": {
                  "type": "string",
                  "description": "New system prompt."
                },
                "toolIds": {
                  "type": "string",
                  "description": "Optional JSON array string of tool IDs (replaces existing list)."
                },
                "thinking": {
                  "type": "string",
                  "description": "New default thinking level (minimal, low, medium, high, xhigh, max), or empty string to clear. Must be supported by the model.",
                  "enum": ["", "minimal", "low", "medium", "high", "xhigh", "max"]
                },
                "contextWindow": {
                  "type": "integer",
                  "description": "New default context-window size in tokens, or 0 to clear. Must be a size the model supports."
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

        // #2136: reserved sub-agent archetype ids are not real named agents and cannot be updated.
        if (BotNexus.Gateway.Agents.BuiltInArchetypes.IsReserved(id))
            return Error($"Agent ID '{id}' is a reserved sub-agent archetype and cannot be updated as a named agent.");

        var agentId = AgentId.From(id);
        var existing = agentRegistry.Get(agentId);
        if (existing is null)
            return Error($"Agent '{id}' is not registered.");

        var updated = existing;

        if (arguments.ContainsKey("displayName") && ReadString(arguments, "displayName") is { } dn)
            updated = updated with { DisplayName = dn };
        if (arguments.ContainsKey("description"))
            updated = updated with { Description = ReadString(arguments, "description") };

        // #3596: the summary is agent-owned, so writes are self-only. The check is a policy denial
        // on the ARGUMENT, not on the whole call - every other field keeps its existing reach, and
        // an unattributed caller (no caller id supplied, i.e. a non-agent host) cannot use this
        // field to reach any agent at all.
        if (arguments.ContainsKey("summary"))
        {
            if (callerAgentId is not { } caller
                || !string.Equals(caller.Value, agentId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Error(
                    $"Policy denial: 'summary' may only be updated by the agent it belongs to. "
                    + $"Caller '{callerAgentId?.Value ?? "(unattributed)"}' cannot write the summary of agent '{id}'.");
            }

            var rawSummary = ReadString(arguments, "summary");
            var maxLength = summaryOptions?.MaxLength ?? AgentSummaryOptions.DefaultMaxLength;
            if (rawSummary is not null && rawSummary.Length > maxLength)
            {
                // Refused, never truncated: a silently cut summary reads as a complete statement
                // and the agent has no signal that its words were altered.
                return Error(
                    $"Summary is {rawSummary.Length} characters; the maximum is {maxLength}. "
                    + "Shorten it and call again; it was not saved.");
            }

            // `with` (not in-place assignment) because `updated` may still alias the registry's
            // live descriptor here, and #2065 requires persistence to succeed before any runtime
            // state changes.
            updated = updated with { Summary = string.IsNullOrWhiteSpace(rawSummary) ? null : rawSummary };
        }
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
        if (arguments.ContainsKey("thinking"))
        {
            var raw = ReadString(arguments, "thinking");
            updated = updated with { Thinking = string.IsNullOrWhiteSpace(raw) ? null : raw };
        }
        if (arguments.ContainsKey("contextWindow"))
        {
            var cw = ReadInt(arguments, "contextWindow");
            updated = updated with { ContextWindow = cw is > 0 ? cw : null };
        }

        // #2649: the same shared preflight create_agent uses, applied to the MERGED descriptor so a
        // provider-only or model-only edit that breaks the pair is caught too. One routine, two
        // tools - they cannot drift into different notions of a valid provider.
        if (BotNexus.Gateway.Agents.AgentModelPreflight.ValidateResolvable(updated, modelRegistry) is { } preflightError)
            return Error(preflightError);

        // #1705: validate the resulting thinking/context defaults against the (possibly newly
        // selected) model before persisting; reject unsupported combinations.
        var capabilityErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateModelCapabilities(updated, modelRegistry);
        if (capabilityErrors.Count > 0)
            return Error(string.Join(" ", capabilityErrors));

        // #2065: persist the candidate config BEFORE mutating the runtime registry so a disk
        // failure cannot leave runtime state inconsistent with config.json.
        try
        {
            await configurationWriter.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Error($"Failed to persist agent configuration; agent was not updated: {ex.Message}");
        }
        agentRegistry.Update(agentId, updated);

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

    private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i):
                return i;
            case JsonElement { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), out var i):
                return i;
            case int i:
                return i;
            case long l:
                return (int)l;
            default:
                return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }
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
