using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Applies BotNexus-specific normalization to <see cref="PlatformConfig"/> after standard IConfiguration binding.
/// Handles agents.defaults extraction, AgentRawElements capture, and legacy root-level gateway field migration.
/// </summary>
public sealed class PlatformConfigPostConfigure(IConfiguration configuration, string? configFilePath = null) : IPostConfigureOptions<PlatformConfig>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public void PostConfigure(string? name, PlatformConfig config)
    {
        // Re-read the raw JSON from the config file path to support presence-aware merging
        // (AgentRawElements) and legacy root-level gateway migration.
        // IConfiguration alone does not preserve original JSON element structure.
        var rawJson = configFilePath is not null && File.Exists(configFilePath)
            ? TryReadFile(configFilePath)
            : ReadRawJson(configuration);

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            PlatformConfigLoader.MigrateLegacyGatewaySettings(config, rawJson);
            PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);
        }
        else
        {
            // No raw JSON available (e.g., no file) — still strip the reserved "defaults" key
            // from the Agents dictionary to prevent it from being treated as a real agent.
            if (config.Agents is not null)
            {
                var keysToRemove = config.Agents.Keys
                    .Where(k => string.Equals(k, "defaults", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var key in keysToRemove)
                    config.Agents.Remove(key);
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
        // IConfigurationRoot exposes the providers; find the JSON file provider and read its path.
        // Fallback: walk IConfigurationProvider looking for a file-based one.
        if (configuration is not IConfigurationRoot root)
            return null;

        foreach (var provider in root.Providers.Reverse())
        {
            // Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider has an internal Source.Path
            var type = provider.GetType();
            if (!type.FullName!.Contains("Json", StringComparison.OrdinalIgnoreCase))
                continue;

            // Get the path via the Source property (JsonConfigurationProvider -> JsonConfigurationSource -> Path)
            var sourceProp = type.GetProperty("Source");
            if (sourceProp?.GetValue(provider) is not { } source)
                continue;

            var pathProp = source.GetType().GetProperty("Path");
            if (pathProp?.GetValue(source) is not string filePath || string.IsNullOrWhiteSpace(filePath))
                continue;

            // Only read BotNexus config files (not appsettings.json etc.)
            if (!filePath.EndsWith("config.json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(filePath))
                return null;

            try
            {
                return File.ReadAllText(filePath);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
