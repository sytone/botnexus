using System.IO.Abstractions;

using BotNexus.Domain.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Reads and writes the <c>world.json</c> sentinel at the root of a BotNexus home (#2836).
/// </summary>
/// <remarks>
/// This is the <see cref="IFileSystem"/>-backed half of <see cref="WorldSentinel"/>: Domain owns the
/// parsing and the comparison so every consumer reaches the same verdict, and this owns the IO so
/// the gateway keeps its testable filesystem abstraction.
/// </remarks>
public static class HomeWorldSentinel
{
    /// <summary>The sentinel file name. Re-exported so callers need only one using.</summary>
    public const string FileName = WorldSentinel.FileName;

    /// <summary>Reads and parses a home's sentinel, or <see langword="null"/> when absent/unreadable.</summary>
    public static WorldSentinelDocument? Read(IFileSystem fileSystem, string homePath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(homePath);

        var path = Path.Combine(homePath, FileName);
        if (!fileSystem.File.Exists(path))
            return null;

        try
        {
            return WorldSentinel.Parse(fileSystem.File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable sentinel presents no competing identity. Treated as absent, which routes
            // to adoption rather than refusal - see WorldSentinel.Parse for why that asymmetry is
            // deliberate.
            return null;
        }
    }

    /// <summary>
    /// Classifies a home against the running world, throwing on mismatch. Performs <b>no writes</b>:
    /// AC5 requires that a process pointed at a foreign home leaves it byte-for-byte untouched, so
    /// verification must be strictly separable from stamping.
    /// </summary>
    public static WorldSentinelVerdict Verify(IFileSystem fileSystem, string homePath, string worldId)
    {
        var sentinel = Read(fileSystem, homePath);
        var verdict = WorldSentinel.Classify(worldId, sentinel, IsPopulated(fileSystem, homePath));

        if (verdict == WorldSentinelVerdict.Mismatch)
            throw new HomeWorldIdentityMismatchException(worldId, sentinel!.WorldId, homePath);

        return verdict;
    }

    /// <summary>
    /// Writes the sentinel for a home that does not already carry a usable one.
    /// </summary>
    /// <remarks>
    /// The skip condition is "already declares a world", not "a file exists". An existing but
    /// unparseable sentinel must be rewritten: <see cref="WorldSentinel.Parse"/> deliberately treats
    /// it as absent, so leaving the corrupt bytes in place would make the home permanently unstamped
    /// while looking stamped - the guard would silently never fire for it again. A sentinel that does
    /// name a world is never rewritten, because that would reset <c>created_at</c> and destroy the
    /// record of when the home was created.
    /// </remarks>
    public static void Stamp(IFileSystem fileSystem, string homePath, string worldId)
    {
        var path = Path.Combine(homePath, FileName);
        if (Read(fileSystem, homePath) is not null)
            return;

        try
        {
            fileSystem.Directory.CreateDirectory(homePath);
            fileSystem.File.WriteAllText(
                path,
                WorldSentinel.Serialize(worldId, ResolveVersion()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Two legitimate cases, neither of which may fail a start: a read-only home mount
            // (Docker :ro), and a first-start race where the gateway, the CLI and cron all stamp the
            // same fresh home at once. The loser of that race sees the winner's file - which names
            // the same world - so the identity is still correct; only the timestamp's owner differs.
        }
    }

    private static bool IsPopulated(IFileSystem fileSystem, string homePath)
    {
        if (!fileSystem.Directory.Exists(homePath))
            return false;

        try
        {
            return fileSystem.Directory
                .EnumerateFileSystemEntries(homePath)
                .Any(entry => !string.Equals(Path.GetFileName(entry), FileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot prove it is empty, so assume it is not: adoption warns, and a spurious warning is
            // strictly safer than silently absorbing populated state.
            return true;
        }
    }

    private static string ResolveVersion()
        => typeof(HomeWorldSentinel).Assembly.GetName().Version?.ToString() ?? "unknown";
}

public sealed class BotNexusHome : IVerifiedHome
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
    private readonly string? _worldId;
    private readonly ILogger _logger;
    private readonly WorldSentinelVerdict _verdict;
    private int _adoptionWarned;

    /// <summary>
    /// Resolves a home and, when a world identity is supplied, verifies the home's sentinel against
    /// it (#2836).
    /// </summary>
    /// <remarks>
    /// <para><b>Why verification happens in the constructor.</b> Every file-backed store derives its
    /// path from this object. Verifying here means the mismatch is detected before any consumer can
    /// hold a path into the wrong world, and - because <see cref="HomeWorldSentinel.Verify"/> writes
    /// nothing - a refused home is left untouched on disk (AC5).</para>
    /// <para><b>Why <paramref name="worldId"/> is optional.</b> The guard is inert when no identity is
    /// configured, matching <c>SqliteStoreIdentityGuard</c>: tests, tools and hosts that have not
    /// opted in behave exactly as before. Verification is only meaningful once the process can state
    /// which world it is. The value must be the single already-resolved <c>WorldId</c>, never
    /// re-derived here - a second derivation would fail identically in both the identity and the path,
    /// and the guard would pass over wrong data (#2834).</para>
    /// </remarks>
    public BotNexusHome(
        IFileSystem fileSystem,
        string? homePath = null,
        string? dataPath = null,
        string? worldId = null,
        ILogger? logger = null)
    {
        _fileSystem = fileSystem;
        _logger = logger ?? NullLogger.Instance;
        _worldId = string.IsNullOrWhiteSpace(worldId) ? null : worldId;
        RootPath = ResolveHomePath(homePath);
        DataPath = ResolveDataPath(dataPath) ?? RootPath;

        _verdict = _worldId is null
            ? WorldSentinelVerdict.Match
            : HomeWorldSentinel.Verify(_fileSystem, RootPath, _worldId);
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

    /// <summary>
    /// The world this home was verified against, or <see langword="null"/> when no identity was
    /// supplied and the sentinel guard is inert (#2836).
    /// </summary>
    public string? WorldId => _worldId;

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
        // Create data directory structure (writable in normal deployments).
        // Tolerate IOException/UnauthorizedAccessException for read-only filesystems (Docker :ro
        // mounts) — the gateway can still function if directories already exist or aren't needed.
        try
        {
            _fileSystem.Directory.CreateDirectory(DataPath);
            foreach (var directory in DataDirectories)
                _fileSystem.Directory.CreateDirectory(Path.Combine(DataPath, directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only filesystem — skip directory creation.
            // Gateway will function with reduced capabilities (no persistent sessions, no agent workspaces).
        }

        // Only create RootPath if it is separate from DataPath and doesn't already exist.
        // When RootPath is mounted read-only (e.g. Docker :ro), it already exists and we skip creation.
        if (!string.Equals(RootPath, DataPath, StringComparison.OrdinalIgnoreCase)
            && !_fileSystem.Directory.Exists(RootPath))
        {
            try { _fileSystem.Directory.CreateDirectory(RootPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* read-only — skip */ }
        }

        StampWorldSentinel();
    }

    /// <summary>
    /// Stamps the home's world sentinel when the constructor's verdict calls for it, warning once for
    /// an adoption (#2836).
    /// </summary>
    /// <remarks>
    /// The warning latch is per-instance rather than process-wide on purpose: <see cref="Initialize"/>
    /// is re-entered by <see cref="GetAgentDirectory"/> on every call, so an unlatched warning would
    /// fire once per agent directory and train operators to ignore the one message that means their
    /// data is in the wrong place.
    /// </remarks>
    private void StampWorldSentinel()
    {
        if (_worldId is null || _verdict == WorldSentinelVerdict.Match)
            return;

        HomeWorldSentinel.Stamp(_fileSystem, RootPath, _worldId);

        if (_verdict == WorldSentinelVerdict.Adopt
            && Interlocked.Exchange(ref _adoptionWarned, 1) == 0)
        {
            _logger.LogWarning(
                "Adopted unstamped BotNexus home '{HomePath}' into world {WorldId}. The home predates " +
                "world-identity stamping; if this path is not this world's data, stop the process now.",
                RootPath,
                _worldId);
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
