using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Abstractions;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Loads agent descriptors from JSON files in a directory.
/// </summary>
public sealed class FileAgentConfigurationSource(string directoryPath, ILogger<FileAgentConfigurationSource> logger, IFileSystem fileSystem)
    : IAgentConfigurationSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directoryPath = Path.GetFullPath(directoryPath);
    private readonly ILogger<FileAgentConfigurationSource> _logger = logger;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!_fileSystem.Directory.Exists(_directoryPath))
            return [];

        List<AgentDescriptor> descriptors = [];
        foreach (var configPath in _fileSystem.Directory.EnumerateFiles(_directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = await TryLoadDescriptorAsync(configPath, cancellationToken);
            if (descriptor is not null)
                descriptors.Add(descriptor);
        }

        return descriptors;
    }

    /// <inheritdoc />
    public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        _fileSystem.Directory.CreateDirectory(_directoryPath);

        return new FileConfigurationWatcher(_directoryPath, _fileSystem, onChanged, LoadAsync, _logger, reloadDebounceMs: 2000);
    }

    private async Task<AgentDescriptor?> TryLoadDescriptorAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = _fileSystem.File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var jsonConfig = await JsonSerializer.DeserializeAsync<AgentConfigurationFile>(stream, JsonOptions, cancellationToken);
            if (jsonConfig is null)
            {
                _logger.LogWarning("Agent config file '{ConfigPath}' is empty or invalid JSON.", configPath);
                return null;
            }

            var descriptor = BuildDescriptor(jsonConfig);
            var validationErrors = AgentDescriptorValidator.ValidateForConfig(descriptor);
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "Skipping agent config '{ConfigPath}' due to validation errors: {Errors}",
                    configPath,
                    string.Join("; ", validationErrors));
                return null;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.SystemPromptFile))
            {
                var systemPrompt = await TryLoadSystemPromptFromFileAsync(configPath, descriptor.SystemPromptFile, cancellationToken);
                if (systemPrompt is null)
                    return null;

                descriptor = descriptor with
                {
                    SystemPrompt = systemPrompt,
                    SystemPromptFile = null
                };
            }

            return descriptor;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Skipping malformed agent config file '{ConfigPath}'.", configPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load agent config file '{ConfigPath}'.", configPath);
            return null;
        }
    }

    private async Task<string?> TryLoadSystemPromptFromFileAsync(
        string configPath,
        string systemPromptFile,
        CancellationToken cancellationToken)
    {
        var configDirectory = Path.GetFullPath(Path.GetDirectoryName(configPath) ?? _directoryPath);
        var resolvedPath = Path.GetFullPath(Path.Combine(configDirectory, systemPromptFile));
        var configDirectoryPrefix = configDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!resolvedPath.StartsWith(configDirectoryPrefix, StringComparison.OrdinalIgnoreCase) &&
            !resolvedPath.Equals(configDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "System prompt file '{SystemPromptFile}' for agent config '{ConfigPath}' resolves outside the configuration directory. Path traversal blocked.",
                systemPromptFile,
                configPath);
            return null;
        }

        if (!_fileSystem.File.Exists(resolvedPath))
        {
            _logger.LogWarning(
                "System prompt file '{SystemPromptFile}' was not found for agent config '{ConfigPath}'.",
                resolvedPath,
                configPath);
            return null;
        }

        return await _fileSystem.File.ReadAllTextAsync(resolvedPath, cancellationToken);
    }

    private static AgentDescriptor BuildDescriptor(AgentConfigurationFile config)
    {
        var subAgentIds = config.SubAgents ?? config.SubAgentIds ?? [];
        if (config.SubAgents is not null && config.SubAgentIds is not null)
            subAgentIds = [.. config.SubAgents.Concat(config.SubAgentIds).Distinct(StringComparer.OrdinalIgnoreCase)];

        return new AgentDescriptor
        {
            AgentId = AgentId.From(config.AgentId ?? string.Empty),
            DisplayName = config.DisplayName ?? string.Empty,
            Emoji = config.Emoji,
            Description = config.Description,
            ModelId = config.ModelId ?? string.Empty,
            ApiProvider = config.ApiProvider ?? string.Empty,
            SystemPrompt = config.SystemPrompt,
            SystemPromptFile = config.SystemPromptFile,
            ToolIds = config.ToolIds ?? [],
            SubAgentIds = subAgentIds,
            SubAgentRoles = config.SubAgentRoles ?? [],
            IsolationStrategy = string.IsNullOrWhiteSpace(config.IsolationStrategy) ? "in-process" : config.IsolationStrategy,
            MaxConcurrentSessions = config.MaxConcurrentSessions ?? 0,
            Metadata = ConvertObject(config.Metadata),
            IsolationOptions = ConvertObject(config.IsolationOptions),
            Soul = CloneSoulConfig(config.Soul),
            Heartbeat = CloneHeartbeatConfig(config.Heartbeat),
            FileAccess = MapFileAccessPolicy(config.FileAccess),
            Kind = config.Kind ?? AgentKind.Named
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

    private static FileAccessPolicy? MapFileAccessPolicy(FileAccessPolicyConfig? fileAccess)
    {
        if (fileAccess is null)
            return null;

        return new FileAccessPolicy
        {
            AllowedReadPaths = fileAccess.AllowedReadPaths?.ToArray() ?? [],
            AllowedWritePaths = fileAccess.AllowedWritePaths?.ToArray() ?? [],
            DeniedPaths = fileAccess.DeniedPaths?.ToArray() ?? []
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

    internal sealed class FileConfigurationWatcher : IDisposable
    {
        private readonly IFileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly string _directoryPath;
        private readonly Action<IReadOnlyList<AgentDescriptor>> _onChanged;
        private readonly Func<CancellationToken, Task<IReadOnlyList<AgentDescriptor>>> _loadAsync;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _reloadGate = new(1, 1);
        private readonly int _reloadDebounceMs;
        private int _pendingEventCount;
        private bool _disposed;

        public FileConfigurationWatcher(
            string directoryPath,
            IFileSystem fileSystem,
            Action<IReadOnlyList<AgentDescriptor>> onChanged,
            Func<CancellationToken, Task<IReadOnlyList<AgentDescriptor>>> loadAsync,
            ILogger logger,
            int reloadDebounceMs = 2000)
        {
            _directoryPath = directoryPath;
            _onChanged = onChanged;
            _loadAsync = loadAsync;
            _logger = logger;
            _reloadDebounceMs = reloadDebounceMs;

            _watcher = fileSystem.FileSystemWatcher.New(directoryPath, "*.*");
            _watcher.IncludeSubdirectories = true;
            _watcher.NotifyFilter = NotifyFilters.FileName
                                    | NotifyFilters.LastWrite
                                    | NotifyFilters.CreationTime
                                    | NotifyFilters.Size;
            _timer = new Timer(OnTimerTick, this, Timeout.Infinite, Timeout.Infinite);

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _watcher.Dispose();
            _timer.Dispose();
            _reloadGate.Dispose();
        }

        private static void OnTimerTick(object? state)
            => _ = ((FileConfigurationWatcher)state!).ReloadAsync();

        private void OnChanged(object sender, FileSystemEventArgs args)
        {
            if (!IsAgentDefinitionFile(_directoryPath, args.FullPath))
                return;

            QueueReload();
        }

        private void OnRenamed(object sender, RenamedEventArgs args)
        {
            if (!IsAgentDefinitionFile(_directoryPath, args.FullPath) && !IsAgentDefinitionFile(_directoryPath, args.OldFullPath))
                return;

            QueueReload();
        }

        private void OnError(object sender, ErrorEventArgs args)
        {
            _logger.LogWarning(args.GetException(), "Agent config file watcher reported an error. Reloading descriptors.");
            QueueReload();
        }

        /// <summary>
        /// Returns true if the file is an agent definition file (*.json or *.md)
        /// directly in an agent subdirectory or the root config dir.
        /// Workspace subdirectory writes (e.g. workspace/, tmp/, memory/) are excluded.
        /// </summary>
        internal static bool IsAgentDefinitionFile(string directoryPath, string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return false;

            var ext = Path.GetExtension(fullPath);
            if (!ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
                return false;

            // Accept files at depth 0 (directly in root config dir) or depth 1
            // (directly in an agent subdirectory, e.g. agents/farnsworth/config.json)
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is null)
                return false;

            var normalRoot = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalDir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Depth 0: file is directly in directoryPath
            if (string.Equals(normalDir, normalRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            // Depth 1: file is in an immediate subdirectory of directoryPath
            var parent = Path.GetDirectoryName(normalDir);
            return parent is not null &&
                   string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       normalRoot, StringComparison.OrdinalIgnoreCase);
        }

        private void QueueReload()
        {
            if (_disposed)
                return;

            var count = Interlocked.Increment(ref _pendingEventCount);
            if (count > 1)
            {
                _logger.LogDebug("Agent config watcher debounced {Count} events into 1 reload.", count);
            }

            _timer.Change(TimeSpan.FromMilliseconds(_reloadDebounceMs), Timeout.InfiniteTimeSpan);
        }

        private async Task ReloadAsync()
        {
            if (_disposed)
                return;

            if (!await _reloadGate.WaitAsync(0))
                return;

            Interlocked.Exchange(ref _pendingEventCount, 0);

            try
            {
                var descriptors = await _loadAsync(CancellationToken.None);
                _onChanged(descriptors);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to hot-reload agent descriptors from '{DirectoryPath}'.", _directoryPath);
            }
            finally
            {
                _reloadGate.Release();
            }
        }
    }

    private sealed record AgentConfigurationFile
    {
        [JsonPropertyName("$schema")]
        public string? Schema { get; init; }

        public string? AgentId { get; init; }

        public string? DisplayName { get; init; }

        public string? Emoji { get; init; }

        public string? Description { get; init; }

        public string? ModelId { get; init; }

        public string? ApiProvider { get; init; }

        public string? SystemPrompt { get; init; }

        public string? SystemPromptFile { get; init; }

        public IReadOnlyList<string>? ToolIds { get; init; }

        public string? IsolationStrategy { get; init; }

        public int? MaxConcurrentSessions { get; init; }

        public JsonElement? Metadata { get; init; }

        public JsonElement? IsolationOptions { get; init; }

        public SoulAgentConfig? Soul { get; init; }

        public HeartbeatAgentConfig? Heartbeat { get; init; }

        public IReadOnlyList<string>? SubAgents { get; init; }

        public IReadOnlyList<string>? SubAgentIds { get; init; }

        public IReadOnlyList<string>? SubAgentRoles { get; init; }

        public FileAccessPolicyConfig? FileAccess { get; init; }

        /// <summary>
        /// Optional. Kind of agent — currently only <c>"Named"</c> is accepted from config.
        /// <c>"SubAgent"</c> is rejected by <see cref="AgentDescriptorValidator.ValidateForConfig"/>;
        /// sub-agents are runtime-only and produced exclusively by
        /// <c>DefaultSubAgentManager.SpawnAsync</c>. Omit the field entirely on existing
        /// configs; the default is <c>Named</c>.
        /// </summary>
        public AgentKind? Kind { get; init; }
    }
}
