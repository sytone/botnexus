using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

public sealed class BotNexusHome
{
    public const string HomeOverrideEnvVar = "BOTNEXUS_HOME";
    private const string HomeDirectoryName = ".botnexus";

    private static readonly string[] RequiredDirectories =
    [
        "extensions",
        "tokens",
        "sessions",
        "logs",
        "agents"
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

    public BotNexusHome(IFileSystem fileSystem, string? homePath = null)
    {
        _fileSystem = fileSystem;
        RootPath = ResolveHomePath(homePath);
    }

    public BotNexusHome(string? homePath = null)
        : this(new FileSystem(), homePath)
    {
    }

    public string RootPath { get; }

    public string AgentsPath => Path.Combine(RootPath, "agents");

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

    public void Initialize()
    {
        _fileSystem.Directory.CreateDirectory(RootPath);
        foreach (var directory in RequiredDirectories)
            _fileSystem.Directory.CreateDirectory(Path.Combine(RootPath, directory));
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
