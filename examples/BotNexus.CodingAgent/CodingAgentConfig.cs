using System.Text.Json;
using System.IO.Abstractions;

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
    public string LogsDirectory { get; init; } = string.Empty;

    public string? Model { get; set; }
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public int MaxToolIterations { get; set; } = 40;
    public int MaxContextTokens { get; set; } = 100000;
    /// <summary>
    /// Default timeout in seconds for shell command execution.
    /// Tools can override per-call via the 'timeout' argument.
    /// Null means no timeout (process runs until complete or cancelled).
    /// </summary>
    public int? DefaultShellTimeoutSeconds { get; init; } = 600;
    /// <summary>
    /// Preferred shell for command execution on Windows.
    /// Values: "auto" (default), "pwsh", "bash".
    /// </summary>
    public string? ShellPreference { get; init; }
    public List<string> AllowedCommands { get; set; } = [];
    public List<string> BlockedPaths { get; set; } = [];
    public Dictionary<string, object?> Custom { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CodingAgentConfig Load(IFileSystem fileSystem, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
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

        var merged = ApplyOverride(defaults, ReadConfig(fileSystem, globalPath));
        merged = ApplyOverride(merged, ReadConfig(fileSystem, localPath));
        return merged;
    }

    /// <summary>
    /// Creates the .botnexus-agent/ directory structure and writes a default config.json
    /// if one does not already exist. Called on every startup so the user always has
    /// a visible, editable configuration file in their project root.
    /// </summary>
    public static void EnsureDirectories(IFileSystem fileSystem, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var config = Load(fileSystem, workingDirectory);
        fileSystem.Directory.CreateDirectory(config.ConfigDirectory);
        fileSystem.Directory.CreateDirectory(config.SessionsDirectory);
        fileSystem.Directory.CreateDirectory(config.ExtensionsDirectory);
        fileSystem.Directory.CreateDirectory(config.SkillsDirectory);
        fileSystem.Directory.CreateDirectory(config.LogsDirectory);

        var configPath = Path.Combine(config.ConfigDirectory, LocalConfigFileName);
        if (!fileSystem.File.Exists(configPath))
        {
            WriteDefaultConfig(fileSystem, configPath);
        }
    }

    private static void WriteDefaultConfig(IFileSystem fileSystem, string path)
    {
        var defaults = new ConfigDocument
        {
            Model = null,
            Provider = null,
            ApiKey = null,
            MaxToolIterations = 40,
            MaxContextTokens = 100000,
            DefaultShellTimeoutSeconds = 600,
            AllowedCommands = [],
            BlockedPaths = [],
            Custom = new Dictionary<string, object?>()
        };

        var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        fileSystem.File.WriteAllText(path, json);
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
            LogsDirectory = Path.Combine(configDirectory, "logs"),
            Model = null,
            Provider = null,
            ApiKey = null,
            MaxToolIterations = 40,
            MaxContextTokens = 100000,
            DefaultShellTimeoutSeconds = 600,
            AllowedCommands = [],
            BlockedPaths = [],
            Custom = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ConfigDocument? ReadConfig(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.File.Exists(path))
        {
            return null;
        }

        var json = fileSystem.File.ReadAllText(path);
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
            LogsDirectory = source.LogsDirectory,
            Model = Coalesce(overrides.Model, source.Model),
            Provider = Coalesce(overrides.Provider, source.Provider),
            ApiKey = Coalesce(overrides.ApiKey, source.ApiKey),
            MaxToolIterations = overrides.MaxToolIterations ?? source.MaxToolIterations,
            MaxContextTokens = overrides.MaxContextTokens ?? source.MaxContextTokens,
            DefaultShellTimeoutSeconds = overrides.DefaultShellTimeoutSeconds ?? source.DefaultShellTimeoutSeconds,
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
        public int? DefaultShellTimeoutSeconds { get; init; }
        public List<string>? AllowedCommands { get; init; }
        public List<string>? BlockedPaths { get; init; }
        public Dictionary<string, object?>? Custom { get; init; }
    }
}
