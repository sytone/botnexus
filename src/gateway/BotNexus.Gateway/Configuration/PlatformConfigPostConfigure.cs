using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Applies BotNexus-specific normalization to <see cref="PlatformConfig"/> after standard IConfiguration binding.
/// Handles agents.defaults extraction, AgentRawElements capture, JsonElement field population,
/// and legacy root-level gateway field migration.
/// </summary>
public sealed class PlatformConfigPostConfigure(IConfiguration configuration, string? configFilePath = null) : IPostConfigureOptions<PlatformConfig>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public void PostConfigure(string? name, PlatformConfig config)
    {
        var rawJson = configFilePath is not null && File.Exists(configFilePath)
            ? TryReadFile(configFilePath)
            : ReadRawJson(configuration);

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            PlatformConfigLoader.MigrateLegacyGatewaySettings(config, rawJson);
            PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);
            PopulateJsonElementFields(config, rawJson);
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
