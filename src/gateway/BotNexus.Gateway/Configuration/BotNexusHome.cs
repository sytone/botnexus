using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

public sealed class BotNexusHome
{
    public const string HomeOverrideEnvVar = "BOTNEXUS_HOME";
    public const string DataDirOverrideEnvVar = "BOTNEXUS_DATA_DIR";
    private const string HomeDirectoryName = ".botnexus";

    private static readonly string[] DataDirectories =
    [
        "extensions",
        "tokens",
        "sessions",
        "logs",
        "agents",
        "backups"
    ];

    private static readonly string[] WorkspaceScaffoldFiles =
    [
        "AGENTS.md",
        "SOUL.md",
        "TOOLS.md",
        "BOOTSTRAP.md",
        "IDENTITY.md",
        "USER.md"
    ];

    private static readonly string[] LegacyWorkspaceFiles =
    [
        .. WorkspaceScaffoldFiles,
        "MEMORY.md"
    ];

    private readonly IFileSystem _fileSystem;

    public BotNexusHome(IFileSystem fileSystem, string? homePath = null, string? dataPath = null)
    {
        _fileSystem = fileSystem;
        RootPath = ResolveHomePath(homePath);
        DataPath = ResolveDataPath(dataPath) ?? RootPath;
    }

    public BotNexusHome(string? homePath = null)
        : this(new FileSystem(), homePath, dataPath: null)
    {
    }

    /// <summary>
    /// Configuration root path. May be read-only in containerized deployments.
    /// Contains config.json and agent descriptor files.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Writable data directory for runtime state (sessions, logs, tokens, extensions, agents, backups).
    /// Defaults to <see cref="RootPath"/> when BOTNEXUS_DATA_DIR is not set.
    /// </summary>
    public string DataPath { get; }

    public string AgentsPath => Path.Combine(DataPath, "agents");

    public static string ResolveHomePath(string? homePath = null)
    {
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(homePath);

        var homeOverride = Environment.GetEnvironmentVariable(HomeOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(homeOverride))
            return Path.GetFullPath(homeOverride);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            // Fallback to HOME environment variable on Linux/Unix systems
            userProfile = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }
        
        if (string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("Unable to determine user home directory. Please set BOTNEXUS_HOME environment variable.");

        return Path.GetFullPath(Path.Combine(userProfile, HomeDirectoryName));
    }

    /// <summary>
    /// Resolves the data directory path from explicit parameter or BOTNEXUS_DATA_DIR environment variable.
    /// Returns null when no override is configured (caller falls back to RootPath).
    /// </summary>
    public static string? ResolveDataPath(string? dataPath = null)
    {
        if (!string.IsNullOrWhiteSpace(dataPath))
            return Path.GetFullPath(dataPath);

        var dataOverride = Environment.GetEnvironmentVariable(DataDirOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(dataOverride))
            return Path.GetFullPath(dataOverride);

        return null;
    }

    public void Initialize()
    {
        // Create data directory structure (always writable)
        _fileSystem.Directory.CreateDirectory(DataPath);
        foreach (var directory in DataDirectories)
            _fileSystem.Directory.CreateDirectory(Path.Combine(DataPath, directory));

        // Only create RootPath if it is separate from DataPath and doesn't already exist.
        // When RootPath is mounted read-only (e.g. Docker :ro), it already exists and we skip creation.
        if (!string.Equals(RootPath, DataPath, StringComparison.OrdinalIgnoreCase)
            && !_fileSystem.Directory.Exists(RootPath))
        {
            _fileSystem.Directory.CreateDirectory(RootPath);
        }
    }

    public string GetAgentDirectory(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        Initialize();
        var agentDirectory = Path.Combine(AgentsPath, agentName.Trim());
        var isFirstCreation = !_fileSystem.Directory.Exists(agentDirectory);
        _fileSystem.Directory.CreateDirectory(agentDirectory);

        if (isFirstCreation)
            ScaffoldAgentWorkspace(agentDirectory);
        else
            MigrateLegacyWorkspace(agentDirectory);

        return agentDirectory;
    }

    private void ScaffoldAgentWorkspace(string agentDirectory)
    {
        var workspacePath = Path.Combine(agentDirectory, "workspace");
        _fileSystem.Directory.CreateDirectory(workspacePath);
        _fileSystem.Directory.CreateDirectory(Path.Combine(agentDirectory, "data", "sessions"));

        var assembly = typeof(BotNexusHome).Assembly;
        foreach (var file in WorkspaceScaffoldFiles)
        {
            var path = Path.Combine(workspacePath, file);
            if (_fileSystem.File.Exists(path))
                continue;

            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"Templates.{file}", StringComparison.OrdinalIgnoreCase));

            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    _fileSystem.File.WriteAllText(path, reader.ReadToEnd());
                    continue;
                }
            }

            _fileSystem.File.WriteAllText(path, string.Empty);
        }
    }

    private void MigrateLegacyWorkspace(string agentDirectory)
    {
        var workspacePath = Path.Combine(agentDirectory, "workspace");
        if (_fileSystem.Directory.Exists(workspacePath))
            return;

        var hasLegacyFiles = LegacyWorkspaceFiles
            .Any(f => _fileSystem.File.Exists(Path.Combine(agentDirectory, f)));
        if (!hasLegacyFiles)
        {
            ScaffoldAgentWorkspace(agentDirectory);
            return;
        }

        _fileSystem.Directory.CreateDirectory(workspacePath);
        _fileSystem.Directory.CreateDirectory(Path.Combine(agentDirectory, "data", "sessions"));
        foreach (var file in LegacyWorkspaceFiles)
        {
            var src = Path.Combine(agentDirectory, file);
            var dst = Path.Combine(workspacePath, file);
            if (_fileSystem.File.Exists(src))
                _fileSystem.File.Move(src, dst, overwrite: true);
        }
    }
}
