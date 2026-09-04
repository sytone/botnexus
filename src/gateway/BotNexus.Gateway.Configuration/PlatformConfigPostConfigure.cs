using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.IO.Abstractions;
using System.Text.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Applies BotNexus-specific normalization to <see cref="PlatformConfig"/> after standard IConfiguration binding.
/// Handles agents.defaults extraction, AgentRawElements capture, JsonElement field population,
/// and legacy root-level gateway field migration.
/// </summary>
public sealed class PlatformConfigPostConfigure(
    IConfiguration configuration,
    string? configFilePath = null,
    IConfigStore? store = null) : IPostConfigureOptions<PlatformConfig>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Creates a post-configure for a home, wiring the SQLite config store as the raw-JSON fallback
    /// when <c>config.db</c> exists beside <paramref name="configPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration sites should prefer this over the constructor. Constructing without a store keeps
    /// the pre-#3842 behaviour, which on a store-only home is silently destructive rather than merely
    /// incomplete - see <see cref="ReadRawJsonFromStore"/>.
    /// </para>
    /// <para>
    /// The store is attached only when the file exists, matching
    /// <see cref="PlatformConfigurationSources.AddPlatformConfiguration"/>: the store file existing IS
    /// the operator's opt-in, so a file-only installation stays byte-identical to before.
    /// </para>
    /// </remarks>
    public static PlatformConfigPostConfigure ForConfigPath(
        IConfiguration configuration,
        string configPath,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fs = fileSystem ?? new FileSystem();
        IConfigStore? store = null;
        try
        {
            var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, fs);
            if (fs.File.Exists(storePath))
                store = new SqliteConfigStore($"Data Source={storePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A store we cannot even probe is not a startup failure: fall through to file-only, which
            // is exactly what happened before the store existed.
        }

        return new PlatformConfigPostConfigure(configuration, configPath, store);
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, PlatformConfig config)
    {
        var rawJson = configFilePath is not null && File.Exists(configFilePath)
            ? TryReadFile(configFilePath)
            : ReadRawJson(configuration);

        // #3842: both arms above are file-bound, so a home whose configuration lives only in
        // config.db yielded null and took the else-branch below. That branch does not merely skip
        // normalisation - it REMOVES agents.defaults - so the store's defaults, its version, its
        // legacy gateway fields and every JsonElement field were dropped with no error and no
        // warning. The store is consulted only when the file produced nothing, which keeps the file
        // authoritative wherever it exists.
        rawJson ??= ReadRawJsonFromStore();

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            // A malformed config.json must not crash IOptions resolution (which would take down
            // the host the first time any service resolves IOptions<PlatformConfig>). The raw-JSON
            // helpers below call JsonDocument.Parse and throw on invalid JSON; fall back to the
            // already-bound config so the gateway can start on defaults.
            try
            {
                PlatformConfigLoader.MigrateLegacyGatewaySettings(config, rawJson);
                PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);
                PopulateVersionFromRawJson(config, rawJson);
                PopulateJsonElementFields(config, rawJson);
            }
            catch (JsonException)
            {
                // Invalid JSON — keep bound defaults. The startup config loader already logged the
                // parse failure; here we just avoid re-throwing during options resolution.
            }
        }
        else
        {
            if (config.Agents is not null)
            {
                var keysToRemove = config.Agents.Keys
                    .Where(k => string.Equals(k, "defaults", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var key in keysToRemove)
                    config.Agents.Remove(key);
            }
        }

        // IConfiguration cannot bind JsonElement fields — null out any that were left in
        // an invalid/undefined state to prevent serialization crashes (e.g. schema validation).
        NullifyInvalidJsonElements(config);
    }

    /// <summary>
    /// Populate Version from raw JSON since IConfiguration binding uses a remapped key
    /// (via ConfigurationKeyName) to avoid collision with DOTNET_VERSION env var.
    /// </summary>
    private static void PopulateVersionFromRawJson(PlatformConfig config, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("version", out var versionEl) &&
                versionEl.TryGetInt32(out var version))
            {
                config.PlatformVersion = version;
            }
        }
        catch { /* non-fatal — Version defaults to 1 */ }
    }

    /// <summary>
    /// Populate JsonElement fields on AgentDefinitionConfig from raw JSON.
    /// IConfiguration cannot bind JsonElement — these fields are left invalid after binding.
    /// </summary>
    private static void PopulateJsonElementFields(PlatformConfig config, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);

            // Populate gateway.extensions.defaults (Dictionary<string, JsonElement>)
            if (config.Gateway?.Extensions is not null &&
                doc.RootElement.TryGetProperty("gateway", out var gatewayEl) &&
                gatewayEl.TryGetProperty("extensions", out var extEl) &&
                extEl.TryGetProperty("defaults", out var defaultsEl) &&
                defaultsEl.ValueKind == JsonValueKind.Object)
            {
                config.Gateway.Extensions.Defaults = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in defaultsEl.EnumerateObject())
                    config.Gateway.Extensions.Defaults[prop.Name] = prop.Value.Clone();
            }

            // Populate per-agent JsonElement fields
            if (config.Agents is not null && config.Agents.Count > 0 &&
                doc.RootElement.TryGetProperty("agents", out var agentsEl))
            {
                foreach (var (agentId, agentConfig) in config.Agents)
                {
                    if (!agentsEl.TryGetProperty(agentId, out var agentEl))
                        continue;

                    if (agentEl.TryGetProperty("metadata", out var meta))
                        agentConfig.Metadata = meta.Clone();
                    if (agentEl.TryGetProperty("isolationOptions", out var iso))
                        agentConfig.IsolationOptions = iso.Clone();
                    if (agentEl.TryGetProperty("extensions", out var ext) &&
                        ext.ValueKind == JsonValueKind.Object)
                    {
                        agentConfig.Extensions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in ext.EnumerateObject())
                            agentConfig.Extensions[prop.Name] = prop.Value.Clone();
                    }
                }
            }
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Null out any JsonElement fields left in an undefined state by IConfiguration binding.
    /// </summary>
    private static void NullifyInvalidJsonElements(PlatformConfig config)
    {
        // gateway.extensions.defaults
        if (config.Gateway?.Extensions?.Defaults is not null)
        {
            var badKeys = config.Gateway.Extensions.Defaults
                .Where(kvp => kvp.Value.ValueKind == JsonValueKind.Undefined)
                .Select(kvp => kvp.Key).ToList();
            foreach (var key in badKeys)
                config.Gateway.Extensions.Defaults.Remove(key);
            if (config.Gateway.Extensions.Defaults.Count == 0)
                config.Gateway.Extensions.Defaults = null;
        }

        if (config.Agents is null)
            return;

        foreach (var agentConfig in config.Agents.Values)
        {
            if (agentConfig.Metadata.HasValue && agentConfig.Metadata.Value.ValueKind == JsonValueKind.Undefined)
                agentConfig.Metadata = null;
            if (agentConfig.IsolationOptions.HasValue && agentConfig.IsolationOptions.Value.ValueKind == JsonValueKind.Undefined)
                agentConfig.IsolationOptions = null;
            if (agentConfig.Extensions is not null)
            {
                var badKeys = agentConfig.Extensions
                    .Where(kvp => kvp.Value.ValueKind == JsonValueKind.Undefined)
                    .Select(kvp => kvp.Key).ToList();
                foreach (var key in badKeys)
                    agentConfig.Extensions.Remove(key);
                if (agentConfig.Extensions.Count == 0)
                    agentConfig.Extensions = null;
            }
        }
    }

    private static string? TryReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>
    /// Serialises the store's rehydrated document as the raw-JSON normalisation input.
    /// </summary>
    /// <remarks>
    /// Deliberately best-effort. An unreadable, locked or internally inconsistent store yields null
    /// and the caller falls back exactly as it did before the store existed - failing options
    /// resolution would take the host down the first time any service resolves
    /// <c>IOptions&lt;PlatformConfig&gt;</c>, which is strictly worse than the degraded read.
    /// </remarks>
    private string? ReadRawJsonFromStore()
    {
        if (store is null)
            return null;

        try
        {
            var entries = store.ReadEntriesAsync().GetAwaiter().GetResult();
            if (entries.Count == 0)
                return null;

            return ConfigDocumentRehydrator.Rehydrate(entries).ToJsonString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadRawJson(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
            return null;

        foreach (var provider in root.Providers.Reverse())
        {
            var type = provider.GetType();
            if (!type.FullName!.Contains("Json", StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceProp = type.GetProperty("Source");
            if (sourceProp?.GetValue(provider) is not { } source)
                continue;

            var pathProp = source.GetType().GetProperty("Path");
            if (pathProp?.GetValue(source) is not string filePath || string.IsNullOrWhiteSpace(filePath))
                continue;

            if (!filePath.EndsWith("config.json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(filePath))
                return null;

            try { return File.ReadAllText(filePath); }
            catch { return null; }
        }

        return null;
    }
}
