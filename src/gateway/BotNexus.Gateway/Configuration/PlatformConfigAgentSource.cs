using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging;
using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Loads agent descriptors from <see cref="PlatformConfig"/> agent definitions.
/// </summary>
public sealed class PlatformConfigAgentSource(
    IOptionsMonitor<PlatformConfig> configOptions,
    string configDirectory,
    ILogger<PlatformConfigAgentSource> logger,
    ILocationResolver? locationResolver = null,
    ModelRegistry? modelRegistry = null,
    IMetrics? metrics = null) : IAgentConfigurationSource
{
    private readonly IOptionsMonitor<PlatformConfig> _configOptions = configOptions;
    private readonly string _configDirectory = Path.GetFullPath(configDirectory);
    private readonly ILogger<PlatformConfigAgentSource> _logger = logger;
    private readonly ILocationResolver? _locationResolver = locationResolver;
    private readonly ModelRegistry? _modelRegistry = modelRegistry;

    // Issue #2114: stable effective fingerprint of the last descriptors we propagated,
    // used to suppress unchanged IOptionsMonitor callbacks before scheduling a registry
    // apply (pre-debounce). Guarded by _fingerprintGate.
    private readonly object _fingerprintGate = new();
    private string? _lastEffectiveFingerprint;

    private readonly System.Diagnostics.Metrics.Counter<long>? _notificationsCounter =
        metrics?.CreateCounter<long>(
            BotNexusMeters.InstrumentName("config_reload", "notifications"),
            unit: "{notification}",
            description: "Platform-config agent-source change notifications received.");
    private readonly System.Diagnostics.Metrics.Counter<long>? _suppressedCounter =
        metrics?.CreateCounter<long>(
            BotNexusMeters.InstrumentName("config_reload", "suppressed"),
            unit: "{notification}",
            description: "Platform-config change notifications suppressed because effective descriptors were unchanged.");
    private readonly System.Diagnostics.Metrics.Counter<long>? _appliesCounter =
        metrics?.CreateCounter<long>(
            BotNexusMeters.InstrumentName("config_reload", "applies"),
            unit: "{apply}",
            description: "Platform-config effective descriptor changes propagated for a registry apply.");

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(LoadFromConfig(_configOptions.CurrentValue, cancellationToken));
    }

    /// <inheritdoc />
    public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        // Seed the fingerprint from the current effective descriptors so the first
        // spurious IOptionsMonitor callback that carries no effective change is suppressed.
        try
        {
            lock (_fingerprintGate)
            {
                _lastEffectiveFingerprint ??= ComputeEffectiveFingerprint(
                    LoadFromConfig(_configOptions.CurrentValue, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to seed platform-config effective fingerprint for config directory '{ConfigDirectory}'.",
                _configDirectory);
        }

        return _configOptions.OnChange(config =>
        {
            try
            {
                _notificationsCounter?.Add(1);

                var descriptors = LoadFromConfig(config, CancellationToken.None);
                var fingerprint = ComputeEffectiveFingerprint(descriptors);

                string? previous;
                bool unchanged;
                lock (_fingerprintGate)
                {
                    previous = _lastEffectiveFingerprint;
                    unchanged = string.Equals(previous, fingerprint, StringComparison.Ordinal);
                    if (!unchanged)
                        _lastEffectiveFingerprint = fingerprint;
                }

                if (unchanged)
                {
                    _suppressedCounter?.Add(1);
                    _logger.LogDebug(
                        "Suppressed unchanged platform-config reload notification. Source='{Source}', ConfigDirectory='{ConfigDirectory}', EffectiveHash='{EffectiveHash}', Reason='unchanged'.",
                        nameof(PlatformConfigAgentSource),
                        _configDirectory,
                        fingerprint);
                    return;
                }

                _appliesCounter?.Add(1);
                _logger.LogInformation(
                    "Platform-config effective descriptors changed; scheduling registry apply. Source='{Source}', ConfigDirectory='{ConfigDirectory}', EffectiveHash='{EffectiveHash}', PreviousHash='{PreviousHash}', AgentCount={AgentCount}, Reason='effective-change'.",
                    nameof(PlatformConfigAgentSource),
                    _configDirectory,
                    fingerprint,
                    previous ?? "(none)",
                    descriptors.Count);

                onChanged(descriptors);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to reload platform-config agents after config change notification for config directory '{ConfigDirectory}'.",
                    _configDirectory);
            }
        });
    }

    private IReadOnlyList<AgentDescriptor> LoadFromConfig(PlatformConfig platformConfig, CancellationToken cancellationToken)
    {
        List<AgentDescriptor> descriptors = [];
        var agents = platformConfig.Agents;
        if (agents is null || agents.Count == 0)
            return descriptors;

        var agentDefaults = platformConfig.AgentDefaults ?? ExtractInlineAgentDefaults(agents);
        var agentRawElements = platformConfig.AgentRawElements;

        foreach (var (agentId, agentConfig) in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip the reserved defaults pseudo-agent (safety guard in case it wasn't stripped on load)
            if (string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!agentConfig.Enabled)
                continue;

            try
            {
                // Merge world-level agent defaults into this agent's config
                JsonElement? rawElement = null;
                if (agentRawElements is not null && agentRawElements.TryGetValue(agentId, out var rawEl))
                    rawElement = rawEl;
                var effectiveConfig = AgentConfigMerger.Merge(agentDefaults, agentConfig, rawElement);
                var metadata = new Dictionary<string, object?>(ConvertObject(effectiveConfig.Metadata), StringComparer.OrdinalIgnoreCase);
                if (effectiveConfig.ToolTimeoutSeconds is int toolTimeoutSeconds)
                    metadata["toolTimeoutSeconds"] = toolTimeoutSeconds;

                var descriptor = new AgentDescriptor
                {
                    AgentId = AgentId.From(agentId),
                    DisplayName = effectiveConfig.DisplayName ?? agentId,
                    Emoji = effectiveConfig.Emoji,
                    Description = effectiveConfig.Description,
                    ModelId = effectiveConfig.Model ?? string.Empty,
                    ApiProvider = effectiveConfig.Provider ?? string.Empty,
                    SystemPromptFile = effectiveConfig.SystemPromptFile,
                    SystemPromptFiles = ResolveSystemPromptFiles(effectiveConfig),
                    ToolIds = effectiveConfig.ToolIds?.ToArray() ?? [],
                    AllowedModelIds = effectiveConfig.AllowedModels?.ToArray() ?? [],
                    SubAgentIds = effectiveConfig.SubAgents?.ToArray() ?? [],
                    SubAgentRoles = effectiveConfig.SubAgentRoles?.ToArray() ?? [],
                    IsolationStrategy = string.IsNullOrWhiteSpace(effectiveConfig.IsolationStrategy)
                    ? "in-process"
                    : effectiveConfig.IsolationStrategy,
                    CacheRetentionMode = effectiveConfig.CacheRetention.HasValue
                    ? effectiveConfig.CacheRetention.Value.ToString().ToLowerInvariant()
                    : null,
                    Thinking = effectiveConfig.Thinking,
                    ContextWindow = effectiveConfig.ContextWindow,
                    MaxConcurrentSessions = effectiveConfig.MaxConcurrentSessions ?? 0,
                    Metadata = metadata,
                    IsolationOptions = ConvertObject(effectiveConfig.IsolationOptions),
                    Memory = CloneMemoryConfig(effectiveConfig.Memory),
                    Soul = CloneSoulConfig(effectiveConfig.Soul),
                    Heartbeat = CloneHeartbeatConfig(effectiveConfig.Heartbeat),
                    DateTimeInjection = effectiveConfig.DateTimeInjection,
                    SessionAccessLevel = effectiveConfig.SessionAccess?.Level ?? "own",
                    SessionAllowedAgents = effectiveConfig.SessionAccess?.AllowedAgents?.ToArray() ?? [],
                    ConversationAccessLevel = effectiveConfig.ConversationAccess?.Level ?? effectiveConfig.SessionAccess?.Level ?? "own",
                    ConversationAllowedAgents = effectiveConfig.ConversationAccess?.AllowedAgents?.ToArray()
                    ?? effectiveConfig.SessionAccess?.AllowedAgents?.ToArray()
                    ?? [],
                    FileAccess = MapFileAccessPolicy(effectiveConfig.FileAccess, platformConfig.Gateway?.FileAccess),
                    ExtensionConfig = ExtensionConfigMerger.Merge(
                    platformConfig.Gateway?.Extensions?.Defaults,
                    effectiveConfig.Extensions),
                    Kind = effectiveConfig.Kind ?? AgentKind.Named,
                    ShellCommand = effectiveConfig.ShellCommand
                };

                var validationErrors = AgentDescriptorValidator.ValidateForConfig(descriptor, modelRegistry: _modelRegistry);
                if (validationErrors.Count > 0)
                {
                    _logger.LogError(
                        "Skipping platform-config agent '{AgentId}' due to validation errors. Correct the agent configuration to retry on the next config reload: {Errors}",
                        agentId,
                        string.Join("; ", validationErrors));
                    continue;
                }

                descriptors.Add(descriptor);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Skipping invalid platform-config agent '{AgentId}'. Correct the agent configuration to retry on the next config reload.",
                    agentId);
            }
        }

        return descriptors;
    }

    private static AgentDefaultsConfig? ExtractInlineAgentDefaults(IReadOnlyDictionary<string, AgentDefinitionConfig> agents)
    {
        foreach (var (agentId, config) in agents)
        {
            if (!string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
                continue;

            return new AgentDefaultsConfig
            {
                ToolIds = config.ToolIds,
                ToolTimeoutSeconds = config.ToolTimeoutSeconds,
                Memory = config.Memory,
                Heartbeat = config.Heartbeat,
                FileAccess = config.FileAccess
            };
        }

        return null;
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
            Path = memoryConfig.Path,
            Indexing = memoryConfig.Indexing,
            PromptInjection = memoryConfig.PromptInjection,
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


    /// <summary>
    /// Computes a stable, order-independent fingerprint (SHA-256, hex) of the effective
    /// agent descriptors. Two descriptor sets that are semantically equal produce the same
    /// fingerprint even though <see cref="LoadFromConfig"/> mints fresh instances each call,
    /// so unchanged IOptionsMonitor callbacks can be suppressed before a registry apply.
    /// </summary>
    private static string ComputeEffectiveFingerprint(IReadOnlyList<AgentDescriptor> descriptors)
    {
        var builder = new StringBuilder();
        foreach (var descriptor in descriptors.OrderBy(d => d.AgentId.Value, StringComparer.Ordinal))
            AppendDescriptor(builder, descriptor);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static void AppendDescriptor(StringBuilder builder, AgentDescriptor d)
    {
        builder.Append(d.AgentId.Value).Append('\u001f');
        builder.Append(d.DisplayName).Append('\u001f');
        builder.Append(d.Kind).Append('\u001f');
        builder.Append(d.Emoji).Append('\u001f');
        builder.Append(d.Description).Append('\u001f');
        builder.Append(d.ModelId).Append('\u001f');
        builder.Append(d.ApiProvider).Append('\u001f');
        builder.Append(d.SystemPromptFile).Append('\u001f');
        builder.Append(d.IsolationStrategy).Append('\u001f');
        builder.Append(d.CacheRetentionMode).Append('\u001f');
        builder.Append(d.Thinking).Append('\u001f');
        builder.Append(d.ContextWindow).Append('\u001f');
        builder.Append(d.MaxConcurrentSessions).Append('\u001f');
        builder.Append(d.SessionAccessLevel).Append('\u001f');
        builder.Append(d.ConversationAccessLevel).Append('\u001f');
        AppendList(builder, d.ToolIds);
        AppendList(builder, d.AllowedModelIds);
        AppendList(builder, d.SubAgentIds);
        AppendList(builder, d.SubAgentRoles);
        AppendList(builder, d.SystemPromptFiles);
        AppendList(builder, d.SessionAllowedAgents);
        AppendList(builder, d.ConversationAllowedAgents);
        AppendList(builder, d.ShellCommand);
        // Metadata, isolation options and extension config are serialized deterministically so
        // that inline config edits (e.g. metadata, extensions, memory) are also reflected.
        builder.Append(SerializeStable(d.Metadata)).Append('\u001f');
        builder.Append(SerializeStable(d.IsolationOptions)).Append('\u001f');
        builder.Append(SerializeExtensions(d.ExtensionConfig)).Append('\u001f');
        builder.Append(SerializeStable(d.Memory)).Append('\u001f');
        builder.Append(SerializeStable(d.Soul)).Append('\u001f');
        builder.Append(SerializeStable(d.Heartbeat)).Append('\u001f');
        builder.Append(SerializeStable(d.FileAccess)).Append('\u001e');
    }

    private static readonly JsonSerializerOptions s_fingerprintJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static void AppendList(StringBuilder builder, IReadOnlyList<string>? values)
    {
        if (values is not null)
        {
            foreach (var value in values)
                builder.Append(value).Append('\u001d');
        }
        builder.Append('\u001f');
    }

    private static string SerializeStable(object? value)
        => value is null ? string.Empty : JsonSerializer.Serialize(value, s_fingerprintJsonOptions);

    private static string SerializeExtensions(IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (extensions is null || extensions.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var kvp in extensions.OrderBy(e => e.Key, StringComparer.Ordinal))
            builder.Append(kvp.Key).Append('=').Append(kvp.Value.GetRawText()).Append(';');
        return builder.ToString();
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

}
