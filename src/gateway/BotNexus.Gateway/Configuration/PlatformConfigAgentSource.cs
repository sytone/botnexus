using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
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
    IOptionsMonitor<PlatformConfig> configOptions,
    string configDirectory,
    ILogger<PlatformConfigAgentSource> logger,
    ILocationResolver? locationResolver = null) : IAgentConfigurationSource
{
    private readonly IOptionsMonitor<PlatformConfig> _configOptions = configOptions;
    private readonly string _configDirectory = Path.GetFullPath(configDirectory);
    private readonly ILogger<PlatformConfigAgentSource> _logger = logger;
    private readonly ILocationResolver? _locationResolver = locationResolver;

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(LoadFromConfig(_configOptions.CurrentValue, cancellationToken));
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

        var agentDefaults = platformConfig.AgentDefaults;
        var agentRawElements = platformConfig.AgentRawElements;

        foreach (var (agentId, agentConfig) in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip the reserved defaults pseudo-agent (safety guard in case it wasn't stripped on load)
            if (string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!agentConfig.Enabled)
                continue;

            // Merge world-level agent defaults into this agent's config
            JsonElement? rawElement = null;
            if (agentRawElements is not null && agentRawElements.TryGetValue(agentId, out var rawEl))
                rawElement = rawEl;
            var effectiveConfig = AgentConfigMerger.Merge(agentDefaults, agentConfig, rawElement);

            var descriptor = new AgentDescriptor
            {
                AgentId = AgentId.From(agentId),
                DisplayName = effectiveConfig.DisplayName ?? agentId,
                Description = effectiveConfig.Description,
                ModelId = effectiveConfig.Model ?? string.Empty,
                ApiProvider = effectiveConfig.Provider ?? string.Empty,
                SystemPromptFile = effectiveConfig.SystemPromptFile,
                SystemPromptFiles = ResolveSystemPromptFiles(effectiveConfig),
                ToolIds = effectiveConfig.ToolIds?.ToArray() ?? [],
                AllowedModelIds = effectiveConfig.AllowedModels?.ToArray() ?? [],
                SubAgentIds = effectiveConfig.SubAgents?.ToArray() ?? [],
                IsolationStrategy = string.IsNullOrWhiteSpace(effectiveConfig.IsolationStrategy)
                    ? "in-process"
                    : effectiveConfig.IsolationStrategy,
                MaxConcurrentSessions = effectiveConfig.MaxConcurrentSessions ?? 0,
                Metadata = ConvertObject(effectiveConfig.Metadata),
                IsolationOptions = ConvertObject(effectiveConfig.IsolationOptions),
                Memory = CloneMemoryConfig(effectiveConfig.Memory),
                Soul = CloneSoulConfig(effectiveConfig.Soul),
                Heartbeat = CloneHeartbeatConfig(effectiveConfig.Heartbeat),
                SessionAccessLevel = effectiveConfig.SessionAccess?.Level ?? "own",
                SessionAllowedAgents = effectiveConfig.SessionAccess?.AllowedAgents?.ToArray() ?? [],
                FileAccess = MapFileAccessPolicy(effectiveConfig.FileAccess, platformConfig.Gateway?.FileAccess),
                ExtensionConfig = ExtensionConfigMerger.Merge(
                    platformConfig.Gateway?.Extensions?.Defaults,
                    effectiveConfig.Extensions)
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

    private static SoulAgentConfig? CloneSoulConfig(SoulAgentConfig? soulConfig)
    {
        if (soulConfig is null)
            return null;

        return new SoulAgentConfig
        {
            Enabled = soulConfig.Enabled,
            Timezone = soulConfig.Timezone,
            DayBoundary = soulConfig.DayBoundary,
            ReflectionOnSeal = soulConfig.ReflectionOnSeal,
            ReflectionPrompt = soulConfig.ReflectionPrompt
        };
    }

    private static HeartbeatAgentConfig? CloneHeartbeatConfig(HeartbeatAgentConfig? heartbeatConfig)
    {
        if (heartbeatConfig is null)
            return null;

        return new HeartbeatAgentConfig
        {
            Enabled = heartbeatConfig.Enabled,
            IntervalMinutes = heartbeatConfig.IntervalMinutes,
            Prompt = heartbeatConfig.Prompt,
            QuietHours = heartbeatConfig.QuietHours is null
                ? null
                : new QuietHoursConfig
                {
                    Enabled = heartbeatConfig.QuietHours.Enabled,
                    Start = heartbeatConfig.QuietHours.Start,
                    End = heartbeatConfig.QuietHours.End,
                    Timezone = heartbeatConfig.QuietHours.Timezone
                }
        };
    }

    private FileAccessPolicy? MapFileAccessPolicy(FileAccessPolicyConfig? agentLevel, FileAccessPolicyConfig? worldLevel)
    {
        // Field-level merge: agent-level fields win over world-level where explicitly set
        var effective = AgentConfigMerger.MergeFileAccess(worldLevel, agentLevel, null);
        if (effective is null)
            return null;

        return new FileAccessPolicy
        {
            AllowedReadPaths = ResolvePolicyPaths(effective.AllowedReadPaths, nameof(FileAccessPolicy.AllowedReadPaths)),
            AllowedWritePaths = ResolvePolicyPaths(effective.AllowedWritePaths, nameof(FileAccessPolicy.AllowedWritePaths)),
            DeniedPaths = ResolvePolicyPaths(effective.DeniedPaths, nameof(FileAccessPolicy.DeniedPaths))
        };
    }

    private string[] ResolvePolicyPaths(IReadOnlyList<string>? paths, string policyField)
    {
        if (paths is null || paths.Count == 0)
            return [];

        List<string> resolvedPaths = new(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('@'))
            {
                resolvedPaths.Add(path);
                continue;
            }

            var resolvedPath = ResolveLocationReference(path);
            if (resolvedPath is not null)
            {
                resolvedPaths.Add(resolvedPath);
                continue;
            }

            _logger.LogWarning(
                "Skipping unresolved location reference '{LocationReference}' in file access policy field '{PolicyField}'.",
                path,
                policyField);
        }

        return resolvedPaths.ToArray();
    }

    private string? ResolveLocationReference(string path)
    {
        if (_locationResolver is null)
            return null;

        var reference = path[1..];
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var separatorIndex = reference.IndexOfAny(['/', '\\']);
        var locationName = separatorIndex >= 0 ? reference[..separatorIndex] : reference;
        if (string.IsNullOrWhiteSpace(locationName))
            return null;

        var basePath = _locationResolver.ResolvePath(locationName);
        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        if (separatorIndex < 0 || separatorIndex == reference.Length - 1)
            return Path.GetFullPath(basePath);

        var subPath = reference[(separatorIndex + 1)..];
        if (string.IsNullOrWhiteSpace(subPath))
            return Path.GetFullPath(basePath);

        var normalizedSubPath = subPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(basePath, normalizedSubPath));
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
