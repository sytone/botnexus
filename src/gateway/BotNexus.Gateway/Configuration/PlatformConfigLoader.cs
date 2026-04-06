using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Loads and validates platform configuration from ~/.botnexus/config.json.
/// </summary>
public static class PlatformConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(500);

    public static event Action<PlatformConfig>? ConfigChanged;

    /// <summary>The default platform configuration directory.</summary>
    public static string DefaultConfigDirectory => new BotNexusHome().RootPath;

    /// <summary>The default BotNexus home path (~/.botnexus).</summary>
    public static string DefaultHomePath => DefaultConfigDirectory;

    /// <summary>The default configuration file path.</summary>
    public static string DefaultConfigPath =>
        Path.Combine(DefaultConfigDirectory, "config.json");

    /// <summary>Loads config from disk and optionally validates it.</summary>
    public static PlatformConfig Load(
        string? configPath = null,
        bool validateOnLoad = true)
    {
        var path = configPath ?? DefaultConfigPath;
        if (!File.Exists(path))
            return new PlatformConfig();

        PlatformConfig config;
        string rawJson;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            rawJson = reader.ReadToEnd();
            config = JsonSerializer.Deserialize<PlatformConfig>(rawJson, JsonOptions)
                ?? new PlatformConfig();
        }
        catch (JsonException ex)
        {
            throw new OptionsValidationException(
                nameof(PlatformConfig),
                typeof(PlatformConfig),
                [$"Invalid JSON in '{path}'. {ex.Message}"]);
        }

        if (!validateOnLoad)
            return config;

        var errors = new List<string>(PlatformConfigSchema.ValidateJson(rawJson));
        errors.AddRange(Validate(config));
        if (errors.Count > 0)
            throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), errors);

        return config;
    }

    /// <summary>Loads config from disk and optionally validates it.</summary>
    public static async Task<PlatformConfig> LoadAsync(
        string? configPath = null,
        CancellationToken cancellationToken = default,
        bool validateOnLoad = true)
    {
        var path = configPath ?? DefaultConfigPath;
        if (!File.Exists(path))
            return new PlatformConfig();

        PlatformConfig config;
        string rawJson;
        try
        {
            await using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            rawJson = await reader.ReadToEndAsync(cancellationToken);
            config = JsonSerializer.Deserialize<PlatformConfig>(rawJson, JsonOptions)
                ?? new PlatformConfig();
        }
        catch (JsonException ex)
        {
            throw new OptionsValidationException(
                nameof(PlatformConfig),
                typeof(PlatformConfig),
                [$"Invalid JSON in '{path}'. {ex.Message}"]);
        }

        if (!validateOnLoad)
            return config;

        var errors = new List<string>(PlatformConfigSchema.ValidateJson(rawJson));
        errors.AddRange(Validate(config));
        if (errors.Count > 0)
            throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), errors);

        return config;
    }

    /// <summary>Validates the configuration and returns any errors.</summary>
    public static IReadOnlyList<string> Validate(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> errors = [];
        Uri? listenUri = null;
        var listenUrl = config.GetListenUrl();

        if (!string.IsNullOrWhiteSpace(listenUrl) &&
            !Uri.TryCreate(listenUrl, UriKind.Absolute, out listenUri))
        {
            errors.Add("gateway.listenUrl must be a valid absolute URL (example: http://localhost:5005).");
        }
        else if (listenUri is not null && !(listenUri.Scheme == Uri.UriSchemeHttp || listenUri.Scheme == Uri.UriSchemeHttps))
        {
            errors.Add("gateway.listenUrl must use http or https.");
        }

        ValidatePath(config.GetAgentsDirectory(), "gateway.agentsDirectory", errors);
        ValidatePath(config.GetSessionsDirectory(), "gateway.sessionsDirectory", errors);
        ValidateSessionStore(config.GetSessionStore(), errors);
        ValidateCors(config.GetCors(), errors);

        var logLevel = config.GetLogLevel();
        if (!string.IsNullOrWhiteSpace(logLevel) &&
            !Enum.TryParse<LogLevel>(logLevel, ignoreCase: true, out _))
        {
            errors.Add("gateway.logLevel must be one of: Trace, Debug, Information, Warning, Error, Critical.");
        }

        ValidateProviders(config.Providers, errors);
        ValidateChannels(config.Channels, errors);
        ValidateAgents(config.Agents, errors);
        ValidateApiKeys(config.GetApiKeys(), errors);

        return errors;
    }

    /// <summary>Ensures the .botnexus directory exists.</summary>
    public static void EnsureConfigDirectory(string? configDir = null)
    {
        if (string.IsNullOrWhiteSpace(configDir) ||
            string.Equals(Path.GetFullPath(configDir), DefaultConfigDirectory, StringComparison.OrdinalIgnoreCase))
        {
            new BotNexusHome(configDir).Initialize();
            return;
        }

        Directory.CreateDirectory(configDir);
    }

    public static IDisposable Watch(string? configPath = null, Action<PlatformConfig>? onChanged = null, Action<Exception>? onError = null)
    {
        var path = string.IsNullOrWhiteSpace(configPath)
            ? DefaultConfigPath
            : Path.GetFullPath(configPath);

        var directory = Path.GetDirectoryName(path) ?? DefaultConfigDirectory;
        EnsureConfigDirectory(directory);

        return new PlatformConfigWatcher(path, onChanged, onError);
    }

    private static void ValidatePath(string? path, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            errors.Add($"{fieldName} contains invalid path characters.");
            return;
        }

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            errors.Add($"{fieldName} must be a valid path.");
        }
    }

    private static void ValidateProviders(Dictionary<string, ProviderConfig>? providers, List<string> errors)
    {
        if (providers is null)
            return;

        foreach (var (providerKey, providerConfig) in providers)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                errors.Add("providers contains an empty provider key. Use a provider ID (example: 'copilot').");
                continue;
            }

            if (providerConfig is null)
            {
                errors.Add($"providers.{providerKey} configuration is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(providerConfig.ApiKey) && string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
            {
                errors.Add($"providers.{providerKey} must define apiKey or baseUrl.");
            }

            if (!string.IsNullOrWhiteSpace(providerConfig.BaseUrl) &&
                (!Uri.TryCreate(providerConfig.BaseUrl, UriKind.Absolute, out var providerUri) ||
                 (providerUri.Scheme != Uri.UriSchemeHttp && providerUri.Scheme != Uri.UriSchemeHttps)))
            {
                errors.Add($"providers.{providerKey}.baseUrl must be a valid http or https absolute URL.");
            }
        }
    }

    private static void ValidateChannels(Dictionary<string, ChannelConfig>? channels, List<string> errors)
    {
        if (channels is null)
            return;

        foreach (var (channelKey, channelConfig) in channels)
        {
            if (string.IsNullOrWhiteSpace(channelKey))
            {
                errors.Add("channels contains an empty channel key. Use a channel ID (example: 'web').");
                continue;
            }

            if (string.IsNullOrWhiteSpace(channelConfig.Type))
                errors.Add($"channels.{channelKey}.type is required (example: 'websocket' or 'slack').");
        }
    }

    private static void ValidateAgents(Dictionary<string, AgentDefinitionConfig>? agents, List<string> errors)
    {
        if (agents is null)
            return;

        foreach (var (agentId, agentConfig) in agents)
        {
            if (string.IsNullOrWhiteSpace(agentId))
            {
                errors.Add("agents contains an empty agent ID. Use a stable ID (example: 'assistant').");
                continue;
            }

            if (string.IsNullOrWhiteSpace(agentConfig.Provider))
                errors.Add($"agents.{agentId}.provider is required (example: 'copilot').");
            if (string.IsNullOrWhiteSpace(agentConfig.Model))
                errors.Add($"agents.{agentId}.model is required (example: 'gpt-4.1').");
        }
    }

    private static void ValidateApiKeys(Dictionary<string, ApiKeyConfig>? apiKeys, List<string> errors)
    {
        if (apiKeys is null)
            return;

        foreach (var (keyId, keyConfig) in apiKeys)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                errors.Add("gateway.apiKeys contains an empty key ID. Use a stable key name (example: 'tenant-a').");
                continue;
            }

            var keyPath = $"gateway.apiKeys.{keyId}";
            if (string.IsNullOrWhiteSpace(keyConfig.ApiKey))
                errors.Add($"{keyPath}.apiKey is required.");
            if (string.IsNullOrWhiteSpace(keyConfig.TenantId))
                errors.Add($"{keyPath}.tenantId is required.");
            if (keyConfig.Permissions is null || keyConfig.Permissions.Count == 0)
                errors.Add($"{keyPath}.permissions must contain at least one permission (example: ['chat:send']).");
        }
    }

    private static void ValidateSessionStore(SessionStoreConfig? sessionStore, List<string> errors)
    {
        if (sessionStore is null)
            return;

        var configuredType = sessionStore.Type?.Trim();
        if (string.IsNullOrWhiteSpace(configuredType))
            return;

        if (configuredType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
            return;

        if (configuredType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(sessionStore.FilePath))
            {
                errors.Add("gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'.");
                return;
            }

            ValidatePath(sessionStore.FilePath, "gateway.sessionStore.filePath", errors);
            return;
        }

        errors.Add("gateway.sessionStore.type must be either 'InMemory' or 'File'.");
    }

    private static void ValidateCors(CorsConfig? cors, List<string> errors)
    {
        if (cors?.AllowedOrigins is null)
            return;

        for (var i = 0; i < cors.AllowedOrigins.Count; i++)
        {
            var origin = cors.AllowedOrigins[i];
            var field = $"gateway.cors.allowedOrigins[{i}]";
            if (string.IsNullOrWhiteSpace(origin))
            {
                errors.Add($"{field} must be a non-empty absolute URL.");
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"{field} must be a valid http or https absolute URL.");
            }
        }
    }

    private sealed class PlatformConfigWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly string _configPath;
        private readonly Action<PlatformConfig>? _onChanged;
        private readonly Action<Exception>? _onError;
        private readonly Lock _sync = new();
        private bool _disposed;

        public PlatformConfigWatcher(string configPath, Action<PlatformConfig>? onChanged, Action<Exception>? onError)
        {
            _configPath = configPath;
            _onChanged = onChanged;
            _onError = onError;

            _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            _timer = new Timer(OnTimerElapsed, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.EnableRaisingEvents = true;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            _watcher.Dispose();
            _timer.Dispose();
        }

        private static void OnTimerElapsed(object? state)
            => ((PlatformConfigWatcher)state!).ReloadConfig();

        private void OnFileChanged(object sender, FileSystemEventArgs e)
            => QueueReload();

        private void OnFileRenamed(object sender, RenamedEventArgs e)
            => QueueReload();

        private void QueueReload()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _timer.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void ReloadConfig()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
            }

            try
            {
                var config = Load(_configPath);
                _onChanged?.Invoke(config);
                ConfigChanged?.Invoke(config);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }
    }
}
