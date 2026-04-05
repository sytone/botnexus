using System.Text.Json;

namespace BotNexus.CodingAgent;

/// <summary>
/// Coding-agent configuration loaded from defaults, user-global, and project-local files.
/// </summary>
public sealed class CodingAgentConfig
{
    private const string ConfigFolderName = ".botnexus-agent";
    private const string LocalConfigFileName = "config.json";
    private const string GlobalConfigFolderName = ".botnexus";
    private const string GlobalConfigFileName = "coding-agent.json";

    public string ConfigDirectory { get; init; } = string.Empty;
    public string SessionsDirectory { get; init; } = string.Empty;
    public string ExtensionsDirectory { get; init; } = string.Empty;
    public string SkillsDirectory { get; init; } = string.Empty;

    public string? Model { get; set; }
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public int MaxToolIterations { get; set; } = 40;
    public int MaxContextTokens { get; set; } = 100000;
    public List<string> AllowedCommands { get; set; } = [];
    public List<string> BlockedPaths { get; set; } = [];
    public Dictionary<string, object?> Custom { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CodingAgentConfig Load(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));
        }

        var root = Path.GetFullPath(workingDirectory);
        var defaults = CreateDefaults(root);

        var globalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            GlobalConfigFolderName,
            GlobalConfigFileName);
        var localPath = Path.Combine(defaults.ConfigDirectory, LocalConfigFileName);

        var merged = ApplyOverride(defaults, ReadConfig(globalPath));
        merged = ApplyOverride(merged, ReadConfig(localPath));
        return merged;
    }

    public static void EnsureDirectories(string workingDirectory)
    {
        var config = Load(workingDirectory);
        Directory.CreateDirectory(config.ConfigDirectory);
        Directory.CreateDirectory(config.SessionsDirectory);
        Directory.CreateDirectory(config.ExtensionsDirectory);
        Directory.CreateDirectory(config.SkillsDirectory);
    }

    private static CodingAgentConfig CreateDefaults(string workingDirectory)
    {
        var configDirectory = Path.Combine(workingDirectory, ConfigFolderName);
        return new CodingAgentConfig
        {
            ConfigDirectory = configDirectory,
            SessionsDirectory = Path.Combine(configDirectory, "sessions"),
            ExtensionsDirectory = Path.Combine(configDirectory, "extensions"),
            SkillsDirectory = Path.Combine(configDirectory, "skills"),
            Model = null,
            Provider = null,
            ApiKey = null,
            MaxToolIterations = 40,
            MaxContextTokens = 100000,
            AllowedCommands = [],
            BlockedPaths = [],
            Custom = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ConfigDocument? ReadConfig(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ConfigDocument>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }

    private static CodingAgentConfig ApplyOverride(CodingAgentConfig source, ConfigDocument? overrides)
    {
        if (overrides is null)
        {
            return source;
        }

        return new CodingAgentConfig
        {
            ConfigDirectory = source.ConfigDirectory,
            SessionsDirectory = source.SessionsDirectory,
            ExtensionsDirectory = source.ExtensionsDirectory,
            SkillsDirectory = source.SkillsDirectory,
            Model = Coalesce(overrides.Model, source.Model),
            Provider = Coalesce(overrides.Provider, source.Provider),
            ApiKey = Coalesce(overrides.ApiKey, source.ApiKey),
            MaxToolIterations = overrides.MaxToolIterations ?? source.MaxToolIterations,
            MaxContextTokens = overrides.MaxContextTokens ?? source.MaxContextTokens,
            AllowedCommands = overrides.AllowedCommands is { Count: > 0 }
                ? [.. overrides.AllowedCommands]
                : [.. source.AllowedCommands],
            BlockedPaths = overrides.BlockedPaths is { Count: > 0 }
                ? [.. overrides.BlockedPaths]
                : [.. source.BlockedPaths],
            Custom = overrides.Custom is { Count: > 0 }
                ? new Dictionary<string, object?>(overrides.Custom, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(source.Custom, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string? Coalesce(string? incoming, string? fallback)
    {
        return string.IsNullOrWhiteSpace(incoming) ? fallback : incoming;
    }

    private sealed class ConfigDocument
    {
        public string? Model { get; init; }
        public string? Provider { get; init; }
        public string? ApiKey { get; init; }
        public int? MaxToolIterations { get; init; }
        public int? MaxContextTokens { get; init; }
        public List<string>? AllowedCommands { get; init; }
        public List<string>? BlockedPaths { get; init; }
        public Dictionary<string, object?>? Custom { get; init; }
    }
}
