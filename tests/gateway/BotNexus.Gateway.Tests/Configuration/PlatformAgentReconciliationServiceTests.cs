using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Tests for #2635: additive startup reconciliation of the bundled agent catalog.
/// </summary>
public sealed class PlatformAgentReconciliationServiceTests : IDisposable
{
    private const string TrailguideId = BundledPlatformAgents.TrailguideAgentId;

    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _backupsDir;

    public PlatformAgentReconciliationServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
        _backupsDir = Path.Combine(_dir, "backups");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
    }

    // ---------------------------------------------------------------- helpers

    private async Task WriteConfigAsync(JsonObject root)
        => await File.WriteAllTextAsync(
            _configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private PlatformAgentReconciliationService CreateService(ILogger? logger = null)
    {
        var fileSystem = new FileSystem();
        var backup = new ConfigBackupService(_backupsDir, fileSystem);
        var writer = new PlatformConfigWriter(_configPath, fileSystem, backup);
        return new PlatformAgentReconciliationService(
            writer,
            BundledPlatformAgents.All,
            logger ?? NullLogger<PlatformAgentReconciliationService>.Instance);
    }

    private async Task<JsonObject> ReadConfigAsync()
        => JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();

    private int BackupCount()
        => Directory.Exists(_backupsDir) ? Directory.GetFiles(_backupsDir, "config-*.json").Length : 0;

    private static JsonObject ValidAgent(string provider, string model, bool enabled = true)
        => new()
        {
            ["provider"] = provider,
            ["model"] = model,
            ["enabled"] = enabled
        };

    // ---------------------------------------------------------------- insert

    [Fact]
    public async Task StartAsync_InsertsTrailguide_WhenKeyIsAbsent()
    {
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject { ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4") }
        });

        await CreateService().StartAsync(CancellationToken.None);

        var agents = (await ReadConfigAsync())["agents"]!.AsObject();
        agents.ShouldContainKey(TrailguideId);
        agents[TrailguideId]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_InsertsTrailguide_WhenAgentsSectionIsAbsentEntirely()
    {
        await WriteConfigAsync(new JsonObject { ["gateway"] = new JsonObject() });

        await CreateService().StartAsync(CancellationToken.None);

        var root = await ReadConfigAsync();
        root["agents"].ShouldNotBeNull();
        root["agents"]!.AsObject().ShouldContainKey(TrailguideId);
    }

    [Fact]
    public async Task StartAsync_StampsDefinitionVersion_OnInsertedEntry()
    {
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject { ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4") }
        });

        await CreateService().StartAsync(CancellationToken.None);

        var entry = (await ReadConfigAsync())["agents"]![TrailguideId]!.AsObject();
        entry[BundledPlatformAgents.DefinitionVersionMetadataKey]!.GetValue<int>()
            .ShouldBe(BundledPlatformAgents.TrailguideDefinitionVersion);
    }

    // ------------------------------------------------- insert-only / no write

    [Fact]
    public async Task StartAsync_PerformsZeroWrites_WhenTrailguideAlreadyExists()
    {
        // AC2: proven by mtime + a write-counting filesystem, NOT by comparing content — a
        // rewrite that happens to reproduce identical bytes would still be a write we forbade.
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4"),
                [TrailguideId] = ValidAgent("github-copilot", "claude-opus-4")
            }
        });

        var counting = new WriteCountingFileSystem();
        var writer = new PlatformConfigWriter(_configPath, counting, new ConfigBackupService(_backupsDir, counting));
        var service = new PlatformAgentReconciliationService(
            writer, BundledPlatformAgents.All, NullLogger<PlatformAgentReconciliationService>.Instance);

        var before = File.GetLastWriteTimeUtc(_configPath);
        await Task.Delay(20);

        await service.StartAsync(CancellationToken.None);

        counting.WriteCount.ShouldBe(0, "An existing bundled agent entry must produce no write at all.");
        File.GetLastWriteTimeUtc(_configPath).ShouldBe(before);
        BackupCount().ShouldBe(0);
    }

    [Fact]
    public async Task StartAsync_PreservesDisabledFlag_OnExistingTrailguideEntry()
    {
        // AC3: the single most important behaviour. A user who turned Trailguide off must not
        // find it back on after an upgrade.
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4"),
                [TrailguideId] = ValidAgent("github-copilot", "claude-opus-4", enabled: false)
            }
        });

        await CreateService().StartAsync(CancellationToken.None);

        var entry = (await ReadConfigAsync())["agents"]![TrailguideId]!.AsObject();
        entry["enabled"]!.GetValue<bool>().ShouldBeFalse(
            "A user-disabled bundled agent must survive reconciliation disabled.");
    }

    [Fact]
    public async Task StartAsync_PreservesEveryExistingField_ByteForByte()
    {
        // AC4.
        var existing = new JsonObject
        {
            ["enabled"] = false,
            ["displayName"] = "My Renamed Guide",
            ["provider"] = "openai",
            ["model"] = "gpt-4.1",
            ["description"] = "user edited",
            ["toolIds"] = new JsonArray("read", "write"),
            ["systemPromptFiles"] = new JsonArray("custom/PROMPT.md")
        };
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4"),
                [TrailguideId] = existing
            }
        });
        var beforeBytes = await File.ReadAllBytesAsync(_configPath);

        await CreateService().StartAsync(CancellationToken.None);

        (await File.ReadAllBytesAsync(_configPath)).ShouldBe(beforeBytes);
    }

    [Fact]
    public async Task StartAsync_TreatsEmptyEntryAsPresent_AndDoesNotTopItUp()
    {
        // Insert-only means insert-only: a half-filled entry is still the user's.
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4"),
                [TrailguideId] = new JsonObject()
            }
        });
        var beforeBytes = await File.ReadAllBytesAsync(_configPath);

        await CreateService().StartAsync(CancellationToken.None);

        (await File.ReadAllBytesAsync(_configPath)).ShouldBe(beforeBytes);
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task StartAsync_RunTwiceInOneProcess_ProducesOneWriteAndOneBackup()
    {
        // AC5.
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject { ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4") }
        });

        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var afterFirst = await File.ReadAllBytesAsync(_configPath);
        var mtimeAfterFirst = File.GetLastWriteTimeUtc(_configPath);
        var backupsAfterFirst = BackupCount();
        await Task.Delay(20);

        await service.StartAsync(CancellationToken.None);

        backupsAfterFirst.ShouldBe(1, "The insert itself must back up exactly once.");
        BackupCount().ShouldBe(1, "A second run must not create a second backup.");
        File.GetLastWriteTimeUtc(_configPath).ShouldBe(mtimeAfterFirst);
        (await File.ReadAllBytesAsync(_configPath)).ShouldBe(afterFirst);
    }

    // ------------------------------------------------ provider/model resolution

    [Fact]
    public void ResolveProviderAndModel_PrefersDefaultAgent_OverEarlierEnabledAgent()
    {
        // Step 2 of the documented order. "alpha" enumerates first, so a result of alpha's pair
        // would mean the defaultAgentId lookup was skipped entirely.
        var root = new JsonObject
        {
            ["gateway"] = new JsonObject { ["defaultAgentId"] = "beta" },
            ["agents"] = new JsonObject
            {
                ["alpha"] = ValidAgent("openai", "gpt-4.1"),
                ["beta"] = ValidAgent("github-copilot", "claude-opus-4")
            }
        };

        var resolved = PlatformAgentReconciliationService.ResolveProviderAndModel(root);

        resolved.ShouldNotBeNull();
        resolved!.Value.Provider.ShouldBe("github-copilot");
        resolved.Value.Model.ShouldBe("claude-opus-4");
    }

    [Fact]
    public void ResolveProviderAndModel_FallsBackToFirstEnabledAgent_WhenDefaultAgentIsIncomplete()
    {
        // Step 3: the defaultAgentId agent exists but has no model, so it is not a valid source.
        var root = new JsonObject
        {
            ["gateway"] = new JsonObject { ["defaultAgentId"] = "beta" },
            ["agents"] = new JsonObject
            {
                ["beta"] = new JsonObject { ["provider"] = "github-copilot", ["enabled"] = true },
                ["alpha"] = ValidAgent("openai", "gpt-4.1")
            }
        };

        var resolved = PlatformAgentReconciliationService.ResolveProviderAndModel(root);

        resolved.ShouldNotBeNull();
        resolved!.Value.Provider.ShouldBe("openai");
    }

    [Fact]
    public void ResolveProviderAndModel_FallsBackToFirstEnabledAgent_WhenDefaultAgentIdIsUnset()
    {
        var root = new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["alpha"] = ValidAgent("openai", "gpt-4.1")
            }
        };

        PlatformAgentReconciliationService.ResolveProviderAndModel(root)!.Value.Provider.ShouldBe("openai");
    }

    [Fact]
    public void ResolveProviderAndModel_SkipsDisabledAgents()
    {
        var root = new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["alpha"] = ValidAgent("openai", "gpt-4.1", enabled: false),
                ["beta"] = ValidAgent("github-copilot", "claude-opus-4")
            }
        };

        PlatformAgentReconciliationService.ResolveProviderAndModel(root)!.Value.Provider
            .ShouldBe("github-copilot");
    }

    [Fact]
    public void ResolveProviderAndModel_ReturnsNull_WhenNoAgentHasValidProviderAndModel()
    {
        // Step 4.
        var root = new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["alpha"] = new JsonObject { ["provider"] = "openai", ["enabled"] = true },
                ["beta"] = ValidAgent("github-copilot", "claude-opus-4", enabled: false)
            }
        };

        PlatformAgentReconciliationService.ResolveProviderAndModel(root).ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_InsertsDisabledEntryWithActionableDescription_WhenNoProviderResolvable()
    {
        // Step 4 end-to-end: never guess a provider; insert disabled and say what to do.
        await WriteConfigAsync(new JsonObject { ["agents"] = new JsonObject() });

        await CreateService().StartAsync(CancellationToken.None);

        var entry = (await ReadConfigAsync())["agents"]![TrailguideId]!.AsObject();
        entry["enabled"]!.GetValue<bool>().ShouldBeFalse();
        entry.ShouldNotContainKey("provider");
        var description = entry["description"]!.GetValue<string>();
        description.ShouldBe(BundledPlatformAgents.UnresolvedProviderDescription);
        description.ShouldContain("provider");
        description.ShouldContain("enabled");
    }

    [Fact]
    public async Task StartAsync_CopiesProviderAndModel_FromDefaultAgent()
    {
        await WriteConfigAsync(new JsonObject
        {
            ["gateway"] = new JsonObject { ["defaultAgentId"] = "farnsworth" },
            ["agents"] = new JsonObject
            {
                ["zeta"] = ValidAgent("openai", "gpt-4.1"),
                ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4")
            }
        });

        await CreateService().StartAsync(CancellationToken.None);

        var entry = (await ReadConfigAsync())["agents"]![TrailguideId]!.AsObject();
        entry["provider"]!.GetValue<string>().ShouldBe("github-copilot");
        entry["model"]!.GetValue<string>().ShouldBe("claude-opus-4");
    }

    // ---------------------------------------------------------------- resilience

    [Fact]
    public async Task StartAsync_DoesNotThrowAndWarnsOnce_WhenConfigJsonIsMalformed()
    {
        // AC7. An unhandled JsonException in a hosted service takes the whole gateway down.
        await File.WriteAllTextAsync(_configPath, "this is not json at all {{{");
        var logger = new CountingLogger();

        var ex = await Record.ExceptionAsync(() => CreateService(logger).StartAsync(CancellationToken.None));

        ex.ShouldBeNull();
        logger.WarningCount.ShouldBe(1, "Exactly one bounded warning, not a per-agent storm.");
    }

    [Fact]
    public async Task StartAsync_DoesNotThrowAndWarnsOnce_WhenConfigIsReadOnly()
    {
        // AC7. Simulated at the filesystem seam so it behaves identically on Windows and Linux;
        // a real chmod/attrib is not portable and silently no-ops for elevated processes.
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject { ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4") }
        });
        var logger = new CountingLogger();
        var readOnly = new WriteCountingFileSystem(throwOnWrite: true);
        var writer = new PlatformConfigWriter(_configPath, readOnly, new ConfigBackupService(_backupsDir, readOnly));
        var service = new PlatformAgentReconciliationService(writer, BundledPlatformAgents.All, logger);

        var ex = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        ex.ShouldBeNull("A read-only config mount must not prevent gateway startup.");
        logger.WarningCount.ShouldBe(1);
    }

    [Fact]
    public async Task StartAsync_NoOps_WhenCatalogIsEmpty()
    {
        await File.WriteAllTextAsync(_configPath, "{\"version\":1}");
        var writer = new PlatformConfigWriter(_configPath, new FileSystem());
        var service = new PlatformAgentReconciliationService(
            writer, [], NullLogger<PlatformAgentReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(_configPath)).ShouldBe("{\"version\":1}");
    }

    // ---------------------------------------------------------------- data dir

    [Fact]
    public void ResolveBackupDirectory_UsesDataDir_WhenBotNexusDataDirIsSet()
    {
        // AC8. Config may live on a read-only mount; backups must land in the writable data dir.
        var dataDir = Path.Combine(_dir, "data");
        var home = new BotNexusHome(new FileSystem(), homePath: _dir, dataPath: dataDir);

        PlatformAgentReconciliationService.ResolveBackupDirectory(home)
            .ShouldBe(Path.Combine(Path.GetFullPath(dataDir), "backups"));
    }

    [Fact]
    public void ResolveBackupDirectory_FallsBackToHomeRoot_WhenDataDirIsUnset()
    {
        var home = new BotNexusHome(new FileSystem(), homePath: _dir, dataPath: null);

        PlatformAgentReconciliationService.ResolveBackupDirectory(home)
            .ShouldBe(Path.Combine(Path.GetFullPath(_dir), "backups"));
    }

    [Fact]
    public async Task Create_ReadsAndWritesConfigUnderHomeRoot()
    {
        // AC8: the reconciler resolves its config path from BotNexusHome rather than a hardcoded
        // ~/.botnexus, so a BOTNEXUS_DATA_DIR/BOTNEXUS_HOME install is reconciled, not ignored.
        var dataDir = Path.Combine(_dir, "data");
        await WriteConfigAsync(new JsonObject
        {
            ["agents"] = new JsonObject { ["farnsworth"] = ValidAgent("github-copilot", "claude-opus-4") }
        });
        var home = new BotNexusHome(new FileSystem(), homePath: _dir, dataPath: dataDir);

        var service = PlatformAgentReconciliationService.Create(
            home, new FileSystem(), NullLogger<PlatformAgentReconciliationService>.Instance);
        await service.StartAsync(CancellationToken.None);

        (await ReadConfigAsync())["agents"]!.AsObject().ShouldContainKey(TrailguideId);
        Directory.GetFiles(Path.Combine(dataDir, "backups"), "config-*.json").Length.ShouldBe(1);
    }

    // ---------------------------------------------------------------- test doubles

    /// <summary>
    /// Wraps a real filesystem and counts every content-producing write to config.json, so an
    /// insert-only violation is caught even when the rewritten bytes happen to be identical.
    /// </summary>
    private sealed class WriteCountingFileSystem : IFileSystem
    {
        private readonly FileSystem _inner = new();
        private readonly CountingFile _file;

        public WriteCountingFileSystem(bool throwOnWrite = false)
        {
            _file = new CountingFile(this, throwOnWrite);
        }

        public int WriteCount => _file.WriteCount;

        public IFile File => _file;
        public IDirectory Directory => _inner.Directory;
        public IFileInfoFactory FileInfo => _inner.FileInfo;
        public IFileStreamFactory FileStream => _inner.FileStream;
        public IPath Path => _inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => _inner.DirectoryInfo;
        public IDriveInfoFactory DriveInfo => _inner.DriveInfo;
        public IFileSystemWatcherFactory FileSystemWatcher => _inner.FileSystemWatcher;
        public IFileVersionInfoFactory FileVersionInfo => _inner.FileVersionInfo;

        private sealed class CountingFile(WriteCountingFileSystem owner, bool throwOnWrite)
            : FileWrapper(owner)
        {
            private int _writeCount;

            public int WriteCount => _writeCount;

            public override Task WriteAllTextAsync(
                string path, string? contents, CancellationToken cancellationToken = default)
            {
                // The writer stages every persistent change through a "*.tmp" file before the
                // atomic swap, so counting temp writes counts real writes without also counting
                // the reads and existence probes a no-op pass legitimately performs.
                if (path.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _writeCount);
                    if (throwOnWrite)
                        throw new UnauthorizedAccessException("Access to the path is denied (simulated read-only mount).");
                }

                return base.WriteAllTextAsync(path, contents, cancellationToken);
            }
        }
    }

    /// <summary>Counts warning-level log entries so "exactly one bounded warning" is assertable.</summary>
    private sealed class CountingLogger : ILogger
    {
        private int _warningCount;

        public int WarningCount => _warningCount;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Interlocked.Increment(ref _warningCount);
        }
    }
}
