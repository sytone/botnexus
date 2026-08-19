using System.Text.Json;
using System.Diagnostics;
using System.IO.Abstractions;
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

    /// <summary>The default platform configuration directory.</summary>
    public static string DefaultConfigDirectory => GetDefaultConfigDirectory(new FileSystem());

    /// <summary>The default BotNexus home path (~/.botnexus).</summary>
    public static string DefaultHomePath => DefaultConfigDirectory;

    /// <summary>The default configuration file path.</summary>
    public static string DefaultConfigPath =>
        Path.Combine(DefaultConfigDirectory, "config.json");

    public static string GetDefaultConfigDirectory(IFileSystem fileSystem)
        => new BotNexusHome(fileSystem).RootPath;

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

        string rawJson;
        using (var stream = fs.File.OpenRead(path))
        using (var reader = new StreamReader(stream))
        {
            rawJson = reader.ReadToEnd();
        }

        return FinishLoad(rawJson, path, validateOnLoad);
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

        string rawJson;
        await using (var stream = fs.File.OpenRead(path))
        using (var reader = new StreamReader(stream))
        {
            rawJson = await reader.ReadToEndAsync(cancellationToken);
        }

        return FinishLoad(rawJson, path, validateOnLoad);
    }

    /// <summary>
    /// Loads config from whichever source is currently authoritative (#3180).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cutover entry point. <see cref="Load"/> and <see cref="LoadAsync"/> read the file
    /// unconditionally and are unchanged; this overload delegates the choice of source to
    /// <paramref name="source"/>, which honours <c>ConfigStoreAuthoritative</c> and falls back to the
    /// file on any store failure.
    /// </para>
    /// <para>
    /// The store-served document is funnelled through the <em>same</em> <see cref="FinishLoad"/>
    /// pipeline as a file-served one - deserialize, migrate legacy schema, extract agent defaults,
    /// validate. That is the point of routing through the raw JSON text rather than binding the
    /// document directly: a store-backed load and a file-backed load cannot drift, because after the
    /// read there is only one code path. A second binding path would be free to silently skip the
    /// legacy migration and produce a subtly different <see cref="PlatformConfig"/> from identical
    /// content.
    /// </para>
    /// <para>
    /// A <see langword="null"/> document (no configuration anywhere) yields a default
    /// <see cref="PlatformConfig"/>, matching what the file loaders do when <c>config.json</c> is
    /// absent.
    /// </para>
    /// </remarks>
    /// <param name="source">Supplies the document and reports which source served it.</param>
    /// <param name="configPath">Used only for error messages, so a failure names a real location.</param>
    /// <param name="validateOnLoad">Whether to run schema and cross-field validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<PlatformConfig> LoadFromSourceAsync(
        Store.IConfigDocumentSource source,
        string? configPath = null,
        bool validateOnLoad = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var read = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Document is null)
            return new PlatformConfig();

        var path = configPath ?? (read.Origin == Store.ConfigDocumentOrigin.Store
            ? "the configuration store"
            : DefaultConfigPath);

        return FinishLoad(read.Document.ToJsonString(), path, validateOnLoad);
    }

    /// <summary>
    /// Shared post-read pipeline for <see cref="Load"/> and <see cref="LoadAsync"/>: deserialize,
    /// migrate legacy schema, optionally validate, and emit the version warning.
    /// </summary>
    /// <remarks>
    /// The sync and async loaders differ <em>only</em> in how they read <paramref name="rawJson"/>
    /// (sync <c>ReadToEnd</c> vs <c>await ReadToEndAsync</c>). Centralising everything after the read
    /// here removes the sync/async divergence hazard: a change to the load pipeline (a new validation
    /// step, a migration reorder) now applies to both paths automatically.
    /// </remarks>
    private static PlatformConfig FinishLoad(string rawJson, string path, bool validateOnLoad)
    {
        PlatformConfig config;
        try
        {
            config = MaterializeConfig(rawJson);
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
            EmitVersionWarning(config);
            return config;
        }

        var errors = new List<string>(PlatformConfigSchema.ValidateObject(config));
        errors.AddRange(PlatformConfigValidator.Validate(config));
        if (errors.Count > 0)
            throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), errors);

        EmitVersionWarning(config);
        return config;
    }

    /// <summary>
    /// The single deserialize &#8594; migrate &#8594; extract materialisation core shared by the primary
    /// load pipeline (<see cref="FinishLoad"/>) and backup recovery (<see cref="TryRecoverFromBackup"/>).
    /// </summary>
    /// <remarks>
    /// Parses the raw JSON document <em>once</em> and threads the root element into both
    /// <see cref="MigrateLegacyGatewaySettings(PlatformConfig, JsonElement)"/> and
    /// <see cref="ExtractAgentDefaults(PlatformConfig, JsonElement)"/> instead of letting each re-parse
    /// the same string. Centralising the sequence here means a new migration/extraction step is applied
    /// identically to primary loads and recovered backups -- the divergence hazard that previously existed
    /// because <c>TryRecoverFromBackup</c> hand-duplicated this sequence inline.
    /// </remarks>
    /// <exception cref="JsonException">The raw JSON is not valid (callers translate this as needed).</exception>
    private static PlatformConfig MaterializeConfig(string rawJson)
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>(rawJson, JsonOptions)
            ?? new PlatformConfig();

        if (string.IsNullOrWhiteSpace(rawJson))
            return config;

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        config = MigrateLegacyGatewaySettings(config, root);
        ExtractAgentDefaults(config, root);
        return config;
    }

    /// <summary>
    /// Validates a complete candidate config <em>document</em> without touching disk.
    /// </summary>
    /// <remarks>
    /// Targeted raw-path mutations (#2057) must prove the whole candidate document is loadable and
    /// valid <em>before</em> the live file is replaced, so a rejected candidate leaves the original
    /// bytes untouched. This runs the same materialise (deserialize -&gt; migrate -&gt; extract
    /// <c>agents.defaults</c>) pipeline the real loader uses, then the same schema and cross-field
    /// validators, so a candidate that passes here is guaranteed to reload successfully.
    /// </remarks>
    /// <param name="rawJson">The candidate document's full JSON text.</param>
    /// <returns>One message per problem; empty when the candidate is safe to persist.</returns>
    public static IReadOnlyList<string> ValidateRawJson(string rawJson)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        PlatformConfig config;
        try
        {
            config = MaterializeConfig(rawJson);
        }
        catch (JsonException ex)
        {
            return [$"Invalid JSON. {ex.Message}"];
        }

        var errors = new List<string>(PlatformConfigSchema.ValidateObject(config));
        errors.AddRange(PlatformConfigValidator.Validate(config));
        return errors;
    }

    /// <summary>Validates non-fatal configuration concerns and returns warnings.</summary>
    /// <remarks>
    /// Forwarding shim retained for existing callers; the implementation lives in
    /// <see cref="PlatformConfigValidator.ValidateWarnings(PlatformConfig)"/> after the validation
    /// engine was extracted (#1764).
    /// </remarks>
    public static IReadOnlyList<string> ValidateWarnings(PlatformConfig config)
        => PlatformConfigValidator.ValidateWarnings(config);

    /// <summary>
    /// Validates the configuration and returns any errors.
    /// </summary>
    /// <remarks>
    /// Forwarding shim retained for existing callers; the implementation lives in
    /// <see cref="PlatformConfigValidator.Validate(PlatformConfig)"/> after the validation engine
    /// was extracted (#1764).
    /// </remarks>
    public static IReadOnlyList<string> Validate(PlatformConfig config)
        => PlatformConfigValidator.Validate(config);

    /// <summary>
    /// Runs server-side configuration validation through the DataAnnotations pipeline.
    /// </summary>
    /// <remarks>
    /// Forwarding shim retained for existing callers; the implementation lives in
    /// <see cref="PlatformConfigValidator.ValidateAnnotated(PlatformConfig)"/> after the validation
    /// engine was extracted (#1764).
    /// </remarks>
    /// <param name="config">The platform configuration to validate.</param>
    /// <returns>The distinct validation error messages, or an empty list when the config is valid.</returns>
    public static IReadOnlyList<string> ValidateAnnotated(PlatformConfig config)
        => PlatformConfigValidator.ValidateAnnotated(config);

    /// <summary>
    /// The imperative cross-field, dictionary-graph, and conditional validation rules for
    /// <see cref="PlatformConfig"/>.
    /// </summary>
    /// <remarks>
    /// Forwarding shim retained for existing callers (including
    /// <see cref="PlatformConfig.Validate(System.ComponentModel.DataAnnotations.ValidationContext)"/>);
    /// the implementation lives in
    /// <see cref="PlatformConfigValidator.CollectCrossFieldErrors(PlatformConfig)"/> after the
    /// validation engine was extracted (#1764).
    /// </remarks>
    /// <param name="config">The platform configuration to validate.</param>
    /// <returns>One message per rule violation.</returns>
    public static IReadOnlyList<string> CollectCrossFieldErrors(PlatformConfig config)
        => PlatformConfigValidator.CollectCrossFieldErrors(config);

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

        // Tolerate a read-only config directory (e.g. Docker `:ro` mount). When the directory
        // already exists we don't need to create it; when the filesystem is read-only we let the
        // gateway continue and rely on BOTNEXUS_DATA_DIR for writable runtime state.
        if (fs.Directory.Exists(configDir))
            return;

        try
        {
            fs.Directory.CreateDirectory(configDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only or permission-denied config directory - continue without creating it.
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the loaded config looks clobbered or suspiciously minimal.
    /// Heuristics:
    /// <list type="bullet">
    ///   <item>No agents, no providers, no channels, and no gateway settings (empty shell config).</item>
    ///   <item>The raw JSON is shorter than <see cref="MinHealthyConfigLength"/> characters.</item>
    /// </list>
    /// Both heuristics must match to avoid false positives on genuinely empty configs.
    /// </summary>
    public static bool IsConfigSuspicious(PlatformConfig config, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rawJson);

        var hasNoAgents = config.Agents is null || config.Agents.Count == 0;
        var hasNoProviders = config.Providers is null || config.Providers.Count == 0;
        var hasNoChannels = config.Channels is null || config.Channels.Count == 0;
        var hasNoGateway = config.Gateway is null;
        var hasNoCron = config.Cron is null;

        var structurallyEmpty = hasNoAgents && hasNoProviders && hasNoChannels && hasNoGateway && hasNoCron;
        var physicallySmall = rawJson.Length < MinHealthyConfigLength;

        return structurallyEmpty && physicallySmall;
    }

    /// <summary>Minimum character length below which a non-empty config is considered suspiciously small.</summary>
    public const int MinHealthyConfigLength = 50;

    /// <summary>
    /// Attempts to find the most recent valid backup of <paramref name="configPath"/> in
    /// the sibling <c>backups/</c> directory, validate it, and return the recovered config.
    /// </summary>
    /// <param name="configPath">Path to the primary config file (used to locate the backups directory).</param>
    /// <param name="recoveredPath">When recovery succeeds, set to the path of the backup that was restored.</param>
    /// <param name="fileSystem">Optional file-system abstraction (for testing).</param>
    /// <returns>The recovered <see cref="PlatformConfig"/>, or <c>null</c> if no valid backup exists.</returns>
    public static PlatformConfig? TryRecoverFromBackup(
        string configPath,
        out string? recoveredPath,
        IFileSystem? fileSystem = null)
    {
        recoveredPath = null;
        var fs = fileSystem ?? new FileSystem();

        var configDirectory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(configDirectory))
            return null;

        var backupsDirectory = Path.Combine(configDirectory, "backups");
        if (!fs.Directory.Exists(backupsDirectory))
            return null;

        // Find backup files ordered newest-first.
        var backupFiles = fs.Directory
            .GetFiles(backupsDirectory, "config-*.json")
            .OrderByDescending(f => fs.File.GetLastWriteTimeUtc(f))
            .ToList();

        foreach (var backup in backupFiles)
        {
            try
            {
                using var stream = fs.File.OpenRead(backup);
                using var reader = new StreamReader(stream);
                var rawJson = reader.ReadToEnd();

                if (rawJson.Length < MinHealthyConfigLength)
                    continue;

                var config = MaterializeConfig(rawJson);
                if (IsConfigSuspicious(config, rawJson))
                    continue;

                var errors = new List<string>(PlatformConfigSchema.ValidateObject(config));
                errors.AddRange(PlatformConfigValidator.Validate(config));
                if (errors.Count > 0)
                    continue;

                recoveredPath = backup;
                return config;
            }
            catch (JsonException)
            {
                // Corrupt backup file -- try the next one.
            }
            catch (IOException)
            {
                // Unreadable backup -- try the next one.
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts <c>agents.defaults</c> from the raw JSON and populates
    /// <see cref="PlatformConfig.AgentDefaults" /> and <see cref="PlatformConfig.AgentRawElements" />,
    /// then removes the reserved <c>defaults</c> key from <see cref="PlatformConfig.Agents" />.
    /// </summary>
    internal static void ExtractAgentDefaults(PlatformConfig config, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        using var document = JsonDocument.Parse(rawJson);
        ExtractAgentDefaults(config, document.RootElement);
    }

    /// <summary>
    /// Parse-once overload of <see cref="ExtractAgentDefaults(PlatformConfig, string)"/>: consumes a
    /// root <see cref="JsonElement"/> already parsed by the load pipeline rather than re-parsing the raw
    /// JSON string. The string overload is retained for external callers and delegates here after a
    /// single parse.
    /// </summary>
    internal static void ExtractAgentDefaults(PlatformConfig config, JsonElement root)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (root.ValueKind != JsonValueKind.Object)
            return;

        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Object)
            return;

        // Extract agents.defaults
        if (agentsElement.TryGetProperty("defaults", out var defaultsElement) &&
            defaultsElement.ValueKind == JsonValueKind.Object)
        {
            config.AgentDefaults = JsonSerializer.Deserialize<AgentDefaultsConfig>(
                defaultsElement.GetRawText(), JsonOptions);
        }

        // Remove reserved key from the Agents dictionary
        if (config.Agents is not null)
        {
            var keysToRemove = config.Agents.Keys
                .Where(k => string.Equals(k, "defaults", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in keysToRemove)
                config.Agents.Remove(key);
        }

        // Capture raw JSON elements for each agent for presence-aware merging
        if (config.Agents is not null && config.Agents.Count > 0)
        {
            var rawElements = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in agentsElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "defaults", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (property.Value.ValueKind == JsonValueKind.Object)
                    rawElements[property.Name] = property.Value.Clone();
            }
            config.AgentRawElements = rawElements;
        }
    }

    /// <summary>
    /// The legacy top-level property names <see cref="MigrateLegacyGatewaySettings(PlatformConfig, JsonElement)"/>
    /// lifts into <c>gateway</c>. A document containing any of these is an <em>old-schema</em>
    /// document: it still loads, but only because the migration step rewrites it on the way in.
    /// </summary>
    /// <remarks>
    /// #2884. Backup restore has to be able to report "this snapshot needs migrating" as a verdict
    /// distinct from "valid" and from "unloadable", because restoring an old-schema snapshot
    /// verbatim is its own outage. That verdict is exactly "does the document use these keys", so
    /// the set is named here beside the migration that consumes it rather than being re-listed by
    /// the caller. Any key added to the migration must be added here too;
    /// <c>ConfigBackupRestoreServiceTests</c> asserts the two agree.
    /// </remarks>
    public static readonly IReadOnlyList<string> LegacyRootSettingKeys =
    [
        "listenUrl", "defaultAgentId", "agentsDirectory", "sessionsDirectory", "logLevel",
        "apiKeys", "sessionStore", "compaction", "cors", "rateLimit", "extensions",
        "locations", "crossWorld",
    ];

    internal static PlatformConfig MigrateLegacyGatewaySettings(PlatformConfig config, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(rawJson))
            return config;

        using var document = JsonDocument.Parse(rawJson);
        return MigrateLegacyGatewaySettings(config, document.RootElement);
    }

    /// <summary>
    /// Parse-once overload of <see cref="MigrateLegacyGatewaySettings(PlatformConfig, string)"/>: consumes
    /// a root <see cref="JsonElement"/> already parsed by the load pipeline rather than re-parsing the raw
    /// JSON string. The string overload is retained for external callers and delegates here after a
    /// single parse.
    /// </summary>
    internal static PlatformConfig MigrateLegacyGatewaySettings(PlatformConfig config, JsonElement root)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (root.ValueKind != JsonValueKind.Object)
            return config;

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
        migrated |= TryMigrateObject(root, "locations", gateway.Locations, value => gateway.Locations = value);
        migrated |= TryMigrateObject(root, "crossWorld", gateway.CrossWorld, value => gateway.CrossWorld = value);

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

    private static void EmitVersionWarning(PlatformConfig config)
    {
        foreach (var warning in PlatformConfigValidator.ValidateWarnings(config))
            Trace.TraceWarning("Platform config warning: {0}", warning);
    }
}
