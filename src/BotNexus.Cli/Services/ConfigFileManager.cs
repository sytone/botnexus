using System.Reflection;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Core.Configuration;

namespace BotNexus.Cli.Services;

public sealed class ConfigFileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string GetConfigPath(string homePath) => Path.Combine(homePath, "config.json");

    public BotNexusConfig LoadConfig(string homePath)
    {
        var defaults = new BotNexusConfig();
        var configPath = GetConfigPath(homePath);
        if (!File.Exists(configPath))
            return defaults;

        try
        {
            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
                return defaults;

            var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            BotNexusConfig? parsed = null;

            if (root.TryGetProperty(BotNexusConfig.SectionName, out var section))
                parsed = section.Deserialize<BotNexusConfig>(JsonOptions);
            else if (root.ValueKind == JsonValueKind.Object)
                parsed = root.Deserialize<BotNexusConfig>(JsonOptions);

            return MergeConfig(defaults, parsed ?? new BotNexusConfig());
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    public void SaveConfig(string homePath, BotNexusConfig config)
    {
        Directory.CreateDirectory(homePath);
        var payload = new Dictionary<string, BotNexusConfig>
        {
            [BotNexusConfig.SectionName] = config
        };

        var configPath = GetConfigPath(homePath);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(configPath, json);
    }

    public bool TryValidateConfig(string homePath, out BotNexusConfig? config, out string message)
    {
        var configPath = GetConfigPath(homePath);
        config = null;

        if (!File.Exists(configPath))
        {
            message = $"Config file not found: {configPath}";
            return false;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                message = "Config file is empty.";
                return false;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty(BotNexusConfig.SectionName, out var section))
                config = section.Deserialize<BotNexusConfig>(JsonOptions);
            else if (root.ValueKind == JsonValueKind.Object)
                config = root.Deserialize<BotNexusConfig>(JsonOptions);

            if (config is null)
            {
                message = "Config JSON is valid but could not bind to BotNexusConfig.";
                return false;
            }

            message = "Config is valid JSON and binds to BotNexusConfig.";
            return true;
        }
        catch (JsonException ex)
        {
            message = $"Invalid JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Failed to validate config: {ex.Message}";
            return false;
        }
    }

    public void AddAgent(string homePath, string name, AgentConfig agent)
    {
        var config = LoadConfig(homePath);
        config.Agents.Named[name] = agent;
        SaveConfig(homePath, config);
    }

    public void AddProvider(string homePath, string name, ProviderConfig provider)
    {
        var config = LoadConfig(homePath);
        config.Providers[name] = provider;
        SaveConfig(homePath, config);
    }

    public void AddChannel(string homePath, string type, ChannelConfig channel)
    {
        var config = LoadConfig(homePath);
        config.Channels.Instances[type] = channel;
        SaveConfig(homePath, config);
    }

    public BotNexusConfig MergeConfig(BotNexusConfig existing, BotNexusConfig overlay)
    {
        var result = JsonSerializer.Deserialize<BotNexusConfig>(
            JsonSerializer.Serialize(existing, JsonOptions),
            JsonOptions) ?? new BotNexusConfig();

        MergeObject(result, overlay);
        return result;
    }

    private static void MergeObject(object target, object overlay)
    {
        if (target is IDictionary targetDictionary && overlay is IDictionary overlayDictionary)
        {
            foreach (DictionaryEntry entry in overlayDictionary)
            {
                if (entry.Value is null)
                    continue;

                if (targetDictionary.Contains(entry.Key) &&
                    targetDictionary[entry.Key] is { } existingValue &&
                    entry.Value.GetType() == existingValue.GetType() &&
                    IsComplexType(entry.Value.GetType()))
                {
                    MergeObject(existingValue, entry.Value);
                    continue;
                }

                targetDictionary[entry.Key] = entry.Value;
            }

            return;
        }

        var targetType = target.GetType();
        foreach (var property in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite)
                continue;
            if (property.GetIndexParameters().Length > 0)
                continue;

            var overlayValue = property.GetValue(overlay);
            if (overlayValue is null)
                continue;

            var targetValue = property.GetValue(target);
            if (targetValue is not null && IsComplexType(property.PropertyType))
            {
                MergeObject(targetValue, overlayValue);
                continue;
            }

            property.SetValue(target, overlayValue);
        }
    }

    private static bool IsComplexType(Type type)
        => type.IsClass && type != typeof(string) && !typeof(JsonNode).IsAssignableFrom(type);
}
