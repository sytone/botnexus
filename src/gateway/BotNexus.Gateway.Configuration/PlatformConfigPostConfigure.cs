using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Applies normalization that ordinary configuration binding cannot represent while preserving the
/// already-bound effective graph, including environment, command-line, and later-provider overlays.
/// </summary>
public sealed class PlatformConfigPostConfigure(IConfiguration configuration, string? configFilePath = null) : IPostConfigureOptions<PlatformConfig>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, PlatformConfig config)
    {
        // Retained for source compatibility; authoritative raw data now comes from the composed
        // provider graph so last-known-good and provider precedence cannot be bypassed.
        _ = configFilePath;
        try
        {
            ApplyAuthoritativeRawShape(configuration, config);
        }
        catch (JsonException)
        {
            // Providers own last-known-good handling. If an unusual custom provider still exposes a
            // malformed shape, retain the graph that binding already produced.
        }

        NullifyInvalidJsonElements(config);
    }

    /// <summary>
    /// Reads accepted provider documents and applies only the raw-shape normalizations
    /// that binding cannot express. This is shared by options and startup extension bootstrap.
    /// </summary>
    public static void ApplyAuthoritativeRawShape(IConfiguration configuration, PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(config);

        if (configuration is not IConfigurationRoot configurationRoot)
            return;

        var providers = configurationRoot.Providers.ToArray();
        ExactModelCapacityConfiguration.Apply(providers, config);
        var rawProviderIndex = -1;
        ConfigDocument? document = null;
        for (var index = providers.Length - 1; index >= 0; index--)
        {
            if (providers[index] is not IAcceptedRawConfigDocumentProvider rawProvider)
                continue;

            document = rawProvider.GetAcceptedRawDocument();
            if (document is not null)
            {
                rawProviderIndex = index;
                break;
            }
        }

        if (document is null)
            return;
        var migration = LegacyGatewayExtensionsMigration.Apply(document);
        if (!migration.Succeeded)
        {
            throw new OptionsValidationException(
                nameof(PlatformConfig),
                typeof(PlatformConfig),
                migration.Errors);
        }

        var rawJson = document.ToJsonString();
        using var parsed = JsonDocument.Parse(rawJson);
        var root = parsed.RootElement;

        // These methods mutate only normalization-owned values; they never replace the bound root.
        PlatformConfigLoader.MigrateLegacyGatewaySettings(config, root);
        PopulateVersionFromRawJson(config, root);
        PlatformConfigLoader.ExtractAgentDefaults(config, root);
        var rawDefaultExtensions = config.AgentDefaults?.Extensions;
        config.AgentDefaults ??= new AgentDefaultsConfig();
        configuration.GetSection("agents:defaults").Bind(config.AgentDefaults);
        config.AgentDefaults.Extensions = rawDefaultExtensions;
        PopulateJsonElementFields(config, root);

        // Loader values are the one typed subtree introduced by this migration. Materialize that
        // subtree from the composed raw shape, where canonical provider overlays already won.
        var migrated = PlatformConfigLoader.MaterializeConfig(rawJson);
        if (migrated.Gateway?.ExtensionLoader is { } rawLoader)
        {
            config.Gateway ??= new GatewaySettingsConfig();
            config.Gateway.ExtensionLoader ??= new ExtensionLoaderConfig();
            if (!HasHigherPrecedenceValue(providers, rawProviderIndex, "gateway:extensionLoader:path"))
                config.Gateway.ExtensionLoader.Path = rawLoader.Path;
            if (!HasHigherPrecedenceValue(providers, rawProviderIndex, "gateway:extensionLoader:enabled"))
                config.Gateway.ExtensionLoader.Enabled = rawLoader.Enabled;
        }
    }

    private static bool HasHigherPrecedenceValue(
        IReadOnlyList<IConfigurationProvider> providers,
        int rawProviderIndex,
        string key)
    {
        for (var index = providers.Count - 1; index > rawProviderIndex; index--)
        {
            if (providers[index].TryGet(key, out _))
                return true;
        }
        return false;
    }

    private static void PopulateVersionFromRawJson(PlatformConfig config, JsonElement root)
    {
        if (root.TryGetProperty("version", out var versionElement) && versionElement.TryGetInt32(out var version))
            config.PlatformVersion = version;
    }

    private static void PopulateJsonElementFields(PlatformConfig config, JsonElement root)
    {
        PopulateExtensionBag(root, "world", bag =>
        {
            config.World ??= new WorldSettingsConfig();
            config.World.Extensions = bag;
        });
        PopulateExtensionBag(root, "gateway", bag =>
        {
            config.Gateway ??= new GatewaySettingsConfig();
            config.Gateway.Extensions = bag;
        });

        if (config.Agents is null || !root.TryGetProperty("agents", out var agentsElement))
            return;

        foreach (var (agentId, agentConfig) in config.Agents)
        {
            if (!agentsElement.TryGetProperty(agentId, out var agentElement))
                continue;
            if (agentElement.TryGetProperty("metadata", out var metadata))
                agentConfig.Metadata = metadata.Clone();
            if (agentElement.TryGetProperty("isolationOptions", out var isolation))
                agentConfig.IsolationOptions = isolation.Clone();
            if (agentElement.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object)
            {
                agentConfig.Extensions = extensions.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void PopulateExtensionBag(JsonElement root, string scope, Action<Dictionary<string, JsonElement>> setter)
    {
        if (root.TryGetProperty(scope, out var scopeElement)
            && scopeElement.TryGetProperty("extensions", out var extensions)
            && extensions.ValueKind == JsonValueKind.Object)
        {
            setter(extensions.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase));
        }
    }

    private static void NullifyInvalidExtensionBag(Dictionary<string, JsonElement>? bag)
    {
        if (bag is null)
            return;
        foreach (var key in bag.Where(pair => pair.Value.ValueKind == JsonValueKind.Undefined).Select(pair => pair.Key).ToArray())
            bag.Remove(key);
    }

    private static void NullifyInvalidJsonElements(PlatformConfig config)
    {
        NullifyInvalidExtensionBag(config.World?.Extensions);
        NullifyInvalidExtensionBag(config.Gateway?.Extensions);
        NullifyInvalidExtensionBag(config.AgentDefaults?.Extensions);

        if (config.Agents is null)
            return;
        foreach (var agentConfig in config.Agents.Values)
        {
            if (agentConfig.Metadata.HasValue && agentConfig.Metadata.Value.ValueKind == JsonValueKind.Undefined)
                agentConfig.Metadata = null;
            if (agentConfig.IsolationOptions.HasValue && agentConfig.IsolationOptions.Value.ValueKind == JsonValueKind.Undefined)
                agentConfig.IsolationOptions = null;
            NullifyInvalidExtensionBag(agentConfig.Extensions);
            if (agentConfig.Extensions is { Count: 0 })
                agentConfig.Extensions = null;
        }
    }
}
