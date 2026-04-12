using System.Text.Json;
using System.Diagnostics;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Loads and validates platform configuration from ~/.botnexus/config.json.
/// </summary>
public static class PlatformConfigLoader
{
    private const int SupportedConfigVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(500);

    public static event Action<PlatformConfig>? ConfigChanged;

    /// <summary>The default platform configuration directory.</summary>
    public static string DefaultConfigDirectory => GetDefaultConfigDirectory(new FileSystem());

    /// <summary>The default BotNexus home path (~/.botnexus).</summary>
    public static string DefaultHomePath => DefaultConfigDirectory;

    /// <summary>The default configuration file path.</summary>
    public static string DefaultConfigPath =>
        Path.Combine(DefaultConfigDirectory, "config.json");

    public static string GetDefaultConfigDirectory(IFileSystem fileSystem)
        => new BotNexusHome(fileSystem).RootPath;

    public static string GetDefaultHomePath(IFileSystem fileSystem)
        => GetDefaultConfigDirectory(fileSystem);

    public static string GetDefaultConfigPath(IFileSystem fileSystem)
        => Path.Combine(GetDefaultConfigDirectory(fileSystem), "config.json");

    /// <summary>Loads config from disk and optionally validates it.</summary>
    public static PlatformConfig Load(
        string? configPath = null,
        bool validateOnLoad = true,
        IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = configPath ?? GetDefaultConfigPath(fs);
        if (!fs.File.Exists(path))
            return new PlatformConfig();

        PlatformConfig config;
        string rawJson;
        try
        {
            using var stream = fs.File.OpenRead(path);
            using var reader = new StreamReader(stream);
            rawJson = reader.ReadToEnd();
            config = JsonSerializer.Deserialize<PlatformConfig>(rawJson, JsonOptions)
                ?? new PlatformConfig();
            config = MigrateLegacyGatewaySettings(config, rawJson);
        }
        catch (JsonException ex)
        {
            throw new OptionsValidationException(
                nameof(PlatformConfig),
                typeof(PlatformConfig),
                [$"Invalid JSON in '{path}'. {ex.Message}"]);
        }

        if (!validateOnLoad)
        {
            EmitVersionWarning(config, path);
            return config;
        }

        var errors = new List<string>(PlatformConfigSchema.ValidateObject(config));
        errors.AddRange(Validate(config));
        if (errors.Count > 0)
            throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), errors);

        EmitVersionWarning(config, path);
        return config;
    }

    /// <summary>Loads config from disk and optionally validates it.</summary>
    public static async Task<PlatformConfig> LoadAsync(
        string? configPath = null,
        CancellationToken cancellationToken = default,
        bool validateOnLoad = true,
        IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = configPath ?? GetDefaultConfigPath(fs);
        if (!fs.File.Exists(path))
            return new PlatformConfig();

        PlatformConfig config;
        string rawJson;
        try
        {
            await using var stream = fs.File.OpenRead(path);
            using var reader = new StreamReader(stream);
            rawJson = await reader.ReadToEndAsync(cancellationToken);
            config = JsonSerializer.Deserialize<PlatformConfig>(rawJson, JsonOptions)
                ?? new PlatformConfig();
            config = MigrateLegacyGatewaySettings(config, rawJson);
        }
        catch (JsonException ex)
        {
            throw new OptionsValidationException(
                nameof(PlatformConfig),
                typeof(PlatformConfig),
                [$"Invalid JSON in '{path}'. {ex.Message}"]);
        }

        if (!validateOnLoad)
        {
            EmitVersionWarning(config, path);
            return config;
        }

        var errors = new List<string>(PlatformConfigSchema.ValidateObject(config));
        errors.AddRange(Validate(config));
        if (errors.Count > 0)
            throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), errors);

        EmitVersionWarning(config, path);
        return config;
    }

    /// <summary>Validates non-fatal configuration concerns and returns warnings.</summary>
    public static IReadOnlyList<string> ValidateWarnings(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> warnings = [];
        if (config.Version > SupportedConfigVersion)
        {
            warnings.Add(
                $"version '{config.Version}' is newer than supported version '{SupportedConfigVersion}'. " +
                "The gateway will continue with best-effort compatibility.");
        }

        return warnings;
    }

    /// <summary>Validates the configuration and returns any errors.</summary>
    public static IReadOnlyList<string> Validate(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> errors = [];
        Uri? listenUri = null;
        var listenUrl = config.Gateway?.ListenUrl;

        if (!string.IsNullOrWhiteSpace(listenUrl) &&
            !Uri.TryCreate(listenUrl, UriKind.Absolute, out listenUri))
        {
            errors.Add("gateway.listenUrl must be a valid absolute URL (example: http://localhost:5005).");
        }
        else if (listenUri is not null && !(listenUri.Scheme == Uri.UriSchemeHttp || listenUri.Scheme == Uri.UriSchemeHttps))
        {
            errors.Add("gateway.listenUrl must use http or https.");
        }

        ValidatePath(config.Gateway?.AgentsDirectory, "gateway.agentsDirectory", errors);
        ValidatePath(config.Gateway?.SessionsDirectory, "gateway.sessionsDirectory", errors);
        ValidateSessionStore(config.Gateway?.SessionStore, errors);
        ValidateCors(config.Gateway?.Cors, errors);

        var logLevel = config.Gateway?.LogLevel;
        if (!string.IsNullOrWhiteSpace(logLevel) &&
            !Enum.TryParse<LogLevel>(logLevel, ignoreCase: true, out _))
        {
            errors.Add("gateway.logLevel must be one of: Trace, Debug, Information, Warning, Error, Critical.");
        }

        ValidateProviders(config.Providers, errors);
        ValidateChannels(config.Channels, errors);
        ValidateAgents(config.Agents, errors);
        ValidateApiKeys(config.Gateway?.ApiKeys, errors);
        ValidateCron(config.Cron, errors);

        return errors;
    }

    /// <summary>Ensures the .botnexus directory exists.</summary>
    public static void EnsureConfigDirectory(string? configDir = null, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var defaultConfigDirectory = GetDefaultConfigDirectory(fs);
        if (string.IsNullOrWhiteSpace(configDir) ||
            string.Equals(Path.GetFullPath(configDir), defaultConfigDirectory, StringComparison.OrdinalIgnoreCase))
        {
            new BotNexusHome(fs, configDir).Initialize();
            return;
        }

        fs.Directory.CreateDirectory(configDir);
    }

    public static IDisposable Watch(
        string? configPath = null,
        Action<PlatformConfig>? onChanged = null,
        Action<Exception>? onError = null,
        IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = string.IsNullOrWhiteSpace(configPath)
            ? GetDefaultConfigPath(fs)
            : Path.GetFullPath(configPath);

        var directory = Path.GetDirectoryName(path) ?? GetDefaultConfigDirectory(fs);
        EnsureConfigDirectory(directory, fs);

        return new PlatformConfigWatcher(path, fs, onChanged, onError);
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

            if (!providerConfig.Enabled)
                continue;

            if (!string.IsNullOrWhiteSpace(providerConfig.BaseUrl) &&
                (!Uri.TryCreate(providerConfig.BaseUrl, UriKind.Absolute, out var providerUri) ||
                 (providerUri.Scheme != Uri.UriSchemeHttp && providerUri.Scheme != Uri.UriSchemeHttps)))
            {
                errors.Add($"providers.{providerKey}.baseUrl must be a valid http or https absolute URL.");
            }
        }
    }

    internal static PlatformConfig MigrateLegacyGatewaySettings(PlatformConfig config, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(rawJson))
            return config;

        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return config;

        var root = document.RootElement;
        var gateway = config.Gateway ?? new GatewaySettingsConfig();
        var migrated = false;

        migrated |= TryMigrateString(root, "listenUrl", gateway.ListenUrl, value => gateway.ListenUrl = value);
        migrated |= TryMigrateString(root, "defaultAgentId", gateway.DefaultAgentId, value => gateway.DefaultAgentId = value);
        migrated |= TryMigrateString(root, "agentsDirectory", gateway.AgentsDirectory, value => gateway.AgentsDirectory = value);
        migrated |= TryMigrateString(root, "sessionsDirectory", gateway.SessionsDirectory, value => gateway.SessionsDirectory = value);
        migrated |= TryMigrateString(root, "logLevel", gateway.LogLevel, value => gateway.LogLevel = value);
        migrated |= TryMigrateObject(root, "apiKeys", gateway.ApiKeys, value => gateway.ApiKeys = value);
        migrated |= TryMigrateObject(root, "sessionStore", gateway.SessionStore, value => gateway.SessionStore = value);
        migrated |= TryMigrateObject(root, "compaction", gateway.Compaction, value => gateway.Compaction = value);
        migrated |= TryMigrateObject(root, "cors", gateway.Cors, value => gateway.Cors = value);
        migrated |= TryMigrateObject(root, "rateLimit", gateway.RateLimit, value => gateway.RateLimit = value);
        migrated |= TryMigrateObject(root, "extensions", gateway.Extensions, value => gateway.Extensions = value);

        if (migrated || config.Gateway is not null)
            config.Gateway = gateway;

        return config;
    }

    private static bool TryMigrateString(
        JsonElement root,
        string propertyName,
        string? currentValue,
        Action<string> setter)
    {
        if (!string.IsNullOrWhiteSpace(currentValue) || !root.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind != JsonValueKind.String)
            return false;

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        setter(value);
        return true;
    }

    private static bool TryMigrateObject<T>(
        JsonElement root,
        string propertyName,
        T? currentValue,
        Action<T> setter) where T : class
    {
        if (currentValue is not null || !root.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;

        var value = JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
        if (value is null)
            return false;

        setter(value);
        return true;
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
                errors.Add($"channels.{channelKey}.type is required (example: 'signalr' or 'slack').");
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

        if (configuredType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(sessionStore.ConnectionString))
                errors.Add("gateway.sessionStore.connectionString is required when gateway.sessionStore.type is 'Sqlite'.");

            return;
        }

        errors.Add("gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'.");
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

    private static void ValidateCron(CronConfig? cron, List<string> errors)
    {
        if (cron is null)
            return;

        if (cron.TickIntervalSeconds <= 0)
            errors.Add("cron.tickIntervalSeconds must be greater than zero.");

        if (cron.Jobs is null)
            return;

        foreach (var (jobId, job) in cron.Jobs)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                errors.Add("cron.jobs contains an empty job key. Use a stable job ID.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(job.Schedule))
                errors.Add($"cron.jobs.{jobId}.schedule is required.");
            if (string.IsNullOrWhiteSpace(job.ActionType))
                errors.Add($"cron.jobs.{jobId}.actionType is required.");
        }
    }

    private static void EmitVersionWarning(PlatformConfig config, string configPath)
    {
        foreach (var warning in ValidateWarnings(config))
            Trace.TraceWarning("Platform config warning for '{0}': {1}", configPath, warning);
    }

    private sealed class PlatformConfigWatcher : IDisposable
    {
        private readonly IFileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly string _configPath;
        private readonly IFileSystem _fileSystem;
        private readonly Action<PlatformConfig>? _onChanged;
        private readonly Action<Exception>? _onError;
        private readonly Lock _sync = new();
        private bool _disposed;

        public PlatformConfigWatcher(string configPath, IFileSystem fileSystem, Action<PlatformConfig>? onChanged, Action<Exception>? onError)
        {
            _configPath = configPath;
            _fileSystem = fileSystem;
            _onChanged = onChanged;
            _onError = onError;

            _watcher = _fileSystem.FileSystemWatcher.New(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath));
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
            _watcher.IncludeSubdirectories = false;
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
                var config = Load(_configPath, fileSystem: _fileSystem);
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
