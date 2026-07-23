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
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Tool that creates a new agent and registers it in the gateway.
/// </summary>
public sealed class CreateAgentTool(
    IAgentRegistry agentRegistry,
    IAgentConfigurationWriter configurationWriter,
    IEnumerable<IAgentChangeNotifier> changeNotifiers,
    BotNexusHome botNexusHome,
    IOptions<PlatformConfig>? platformConfigOptions = null,
    ApiProviderRegistry? apiProviderRegistry = null,
    ModelRegistry? modelRegistry = null) : IAgentTool
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
                },
                "thinking": {
                  "type": "string",
                  "description": "Optional default thinking level (minimal, low, medium, high, xhigh, max). Must be supported by the model.",
                  "enum": ["minimal", "low", "medium", "high", "xhigh", "max"]
                },
                "contextWindow": {
                  "type": "integer",
                  "description": "Optional default context-window size in tokens. Must be a size the model supports."
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

        // #2136: the six worker archetype ids are reserved for spawn_subagent(archetype:...) and may
        // not be created as real named agents.
        if (BotNexus.Gateway.Agents.BuiltInArchetypes.IsReserved(id))
            return Error($"Agent ID '{id}' is a reserved sub-agent archetype and cannot be created as a named agent. Use spawn_subagent(archetype: \"{id}\") instead.");

        if (string.IsNullOrWhiteSpace(displayName))
            return Error("Parameter 'displayName' is required.");

        if (string.IsNullOrWhiteSpace(modelId))
            return Error("Parameter 'modelId' is required.");

        if (string.IsNullOrWhiteSpace(apiProvider))
            return Error("Parameter 'apiProvider' is required.");

        if (apiProviderRegistry is not null && apiProviderRegistry.Get(apiProvider) is null)
            return Error($"Unknown API provider '{apiProvider}'. Available providers: {string.Join(", ", apiProviderRegistry.GetAll().Select(p => p.Api))}.");

        var agentId = AgentId.From(id);
        if (agentRegistry.Contains(agentId))
            return Error($"An agent with ID '{id}' is already registered.");

        var toolIds = ParseToolIds(ReadString(arguments, "toolIds"));

        var thinking = ReadString(arguments, "thinking");
        int? contextWindow = ReadInt(arguments, "contextWindow");

        var platformConfig = platformConfigOptions?.Value;
        var memory = BuildMemoryConfig(platformConfig);
        var soul = BuildSoulConfig(platformConfig);
        var extensionConfig = BuildExtensionConfig(platformConfig);

        var descriptor = new AgentDescriptor
        {
            AgentId = agentId,
            DisplayName = displayName,
            Description = ReadString(arguments, "description"),
            Emoji = ReadString(arguments, "emoji"),
            ModelId = modelId,
            ApiProvider = apiProvider,
            SystemPrompt = ReadString(arguments, "systemPrompt"),
            ToolIds = toolIds,
            Thinking = thinking,
            ContextWindow = contextWindow,
            Memory = memory,
            Soul = soul,
            ExtensionConfig = extensionConfig
        };

        // #1705: reject thinking/context defaults the selected model cannot honour before the
        // agent is persisted, mirroring the config-load and REST guards.
        var capabilityErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateModelCapabilities(descriptor, modelRegistry);
        if (capabilityErrors.Count > 0)
            return Error(string.Join(" ", capabilityErrors));

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

    private static MemoryAgentConfig BuildMemoryConfig(PlatformConfig? platformConfig)
    {
        if (platformConfig?.AgentDefaults?.Memory is { Enabled: true } defaultMemory)
            return defaultMemory;

        return new MemoryAgentConfig
        {
            Enabled = true,
            Indexing = "auto",
            PromptInjection = "full",
            Search = new MemorySearchAgentConfig
            {
                DefaultTopK = 10,
                TemporalDecay = new TemporalDecayAgentConfig { Enabled = true, HalfLifeDays = 30 }
            }
        };
    }

    private static SoulAgentConfig BuildSoulConfig(PlatformConfig? platformConfig)
    {
        return new SoulAgentConfig
        {
            Enabled = true,
            Timezone = platformConfig?.Gateway?.DefaultTimezone ?? "UTC",
            DayBoundary = "00:00",
            ReflectionOnSeal = false
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildExtensionConfig(PlatformConfig? platformConfig)
    {
        var config = new Dictionary<string, JsonElement>();

        var extensionDefaults = platformConfig?.Gateway?.Extensions?.Defaults;
        if (extensionDefaults is not null &&
            extensionDefaults.TryGetValue("botnexus-skills", out var skillsDefault) &&
            skillsDefault.ValueKind == JsonValueKind.Object &&
            skillsDefault.TryGetProperty("enabled", out var enabledProp) &&
            enabledProp.ValueKind == JsonValueKind.True)
        {
            config["botnexus-skills"] = JsonSerializer.SerializeToElement(
                new { enabled = true, maxLoadedSkills = 20, allowSkillCreation = false, allowSkillDeletion = false });
        }

        return config;
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
