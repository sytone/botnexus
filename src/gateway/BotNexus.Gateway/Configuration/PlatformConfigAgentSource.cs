using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Loads agent descriptors from <see cref="PlatformConfig"/> agent definitions.
/// </summary>
public sealed class PlatformConfigAgentSource(
    IOptions<PlatformConfig> configOptions,
    string configDirectory,
    ILogger<PlatformConfigAgentSource> logger) : IAgentConfigurationSource
{
    private readonly IOptions<PlatformConfig> _configOptions = configOptions;
    private readonly string _configDirectory = Path.GetFullPath(configDirectory);
    private readonly ILogger<PlatformConfigAgentSource> _logger = logger;

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(LoadFromConfig(_configOptions.Value, cancellationToken));
    }

    /// <inheritdoc />
    public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        Action<PlatformConfig> onPlatformConfigChanged = config =>
        {
            try
            {
                var descriptors = LoadFromConfig(config, CancellationToken.None);
                onChanged(descriptors);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to reload platform-config agents after config change notification for config directory '{ConfigDirectory}'.",
                    _configDirectory);
            }
        };

        PlatformConfigLoader.ConfigChanged += onPlatformConfigChanged;
        return new Subscription(() => PlatformConfigLoader.ConfigChanged -= onPlatformConfigChanged);
    }

    private IReadOnlyList<AgentDescriptor> LoadFromConfig(PlatformConfig platformConfig, CancellationToken cancellationToken)
    {
        List<AgentDescriptor> descriptors = [];
        var agents = platformConfig.Agents;
        if (agents is null || agents.Count == 0)
            return descriptors;

        foreach (var (agentId, agentConfig) in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!agentConfig.Enabled)
                continue;

            var descriptor = new AgentDescriptor
            {
                AgentId = AgentId.From(agentId),
                DisplayName = agentConfig.DisplayName ?? agentId,
                Description = agentConfig.Description,
                ModelId = agentConfig.Model ?? string.Empty,
                ApiProvider = agentConfig.Provider ?? string.Empty,
                SystemPromptFile = agentConfig.SystemPromptFile,
                SystemPromptFiles = ResolveSystemPromptFiles(agentConfig),
                ToolIds = agentConfig.ToolIds?.ToArray() ?? [],
                AllowedModelIds = agentConfig.AllowedModels?.ToArray() ?? [],
                SubAgentIds = agentConfig.SubAgents?.ToArray() ?? [],
                IsolationStrategy = string.IsNullOrWhiteSpace(agentConfig.IsolationStrategy)
                    ? "in-process"
                    : agentConfig.IsolationStrategy,
                MaxConcurrentSessions = agentConfig.MaxConcurrentSessions ?? 0,
                Metadata = ConvertObject(agentConfig.Metadata),
                IsolationOptions = ConvertObject(agentConfig.IsolationOptions),
                Memory = CloneMemoryConfig(agentConfig.Memory),
                SessionAccessLevel = agentConfig.SessionAccess?.Level ?? "own",
                SessionAllowedAgents = agentConfig.SessionAccess?.AllowedAgents?.ToArray() ?? [],
                ExtensionConfig = agentConfig.Extensions ?? new Dictionary<string, JsonElement>()
            };

            var validationErrors = AgentDescriptorValidator.Validate(descriptor);
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "Skipping platform-config agent '{AgentId}' due to validation errors: {Errors}",
                    agentId,
                    string.Join("; ", validationErrors));
                continue;
            }

            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    private static IReadOnlyList<string> ResolveSystemPromptFiles(AgentDefinitionConfig agentConfig)
    {
        if (agentConfig.SystemPromptFiles is { Count: > 0 })
            return agentConfig.SystemPromptFiles.ToArray();

        if (!string.IsNullOrWhiteSpace(agentConfig.SystemPromptFile))
            return [agentConfig.SystemPromptFile];

        return [];
    }

    private static MemoryAgentConfig? CloneMemoryConfig(MemoryAgentConfig? memoryConfig)
    {
        if (memoryConfig is null)
            return null;

        return new MemoryAgentConfig
        {
            Enabled = memoryConfig.Enabled,
            Indexing = memoryConfig.Indexing,
            Search = memoryConfig.Search is null
                ? null
                : new MemorySearchAgentConfig
                {
                    DefaultTopK = memoryConfig.Search.DefaultTopK,
                    TemporalDecay = memoryConfig.Search.TemporalDecay is null
                        ? null
                        : new TemporalDecayAgentConfig
                        {
                            Enabled = memoryConfig.Search.TemporalDecay.Enabled,
                            HalfLifeDays = memoryConfig.Search.TemporalDecay.HalfLifeDays
                        }
                }
        };
    }

    private static IReadOnlyDictionary<string, object?> ConvertObject(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new Dictionary<string, object?>();

        if (element.Value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>();

        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.Value.EnumerateObject())
            result[property.Name] = ConvertElement(property.Value);

        return result;
    }

    private static object? ConvertElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertElement(p.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var @double) => @double,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };

    private sealed class Subscription(Action disposeAction) : IDisposable
    {
        private readonly Action _disposeAction = disposeAction;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _disposeAction();
        }
    }
}
