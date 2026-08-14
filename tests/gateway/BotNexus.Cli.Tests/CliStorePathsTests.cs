using BotNexus.Cli;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

/// <summary>Serialises the env-var-mutating store resolution tests.</summary>
[CollectionDefinition("cli-store-paths", DisableParallelization = true)]
public sealed class CliStorePathsCollection;

/// <summary>
/// Covers the shared tolerant session/cron store resolver introduced by issue #3126.
/// <para>
/// Before the fix every CLI reader hard-coded <c>Path.Combine(home, "sessions.db")</c> while every
/// writer created <c>sessions.sqlite</c> under the configured <em>data</em> directory - two
/// independent mismatches (filename AND directory), so <c>botnexus debug sessions</c> could never
/// open a real store on any deployment.
/// </para>
/// <para>
/// These tests mutate <c>BOTNEXUS_DATA_DIR</c>, so the class is not parallelised against itself.
/// </para>
/// </summary>
[Collection("cli-store-paths")]
public sealed class CliStorePathsTests : IDisposable
{
    private readonly string _home;
    private readonly string _dataDir;
    private readonly string? _originalDataDir;

    public CliStorePathsTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"botnexus-store-{Guid.NewGuid():N}");
        _home = Path.Combine(root, "home");
        _dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar);
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, _originalDataDir);
        try { Directory.Delete(Path.GetDirectoryName(_home)!, recursive: true); } catch { }
    }

    private static void Touch(string path) => File.WriteAllText(path, string.Empty);

    // ── AC6: fails on b43bd2cd - a sessions.sqlite in the home is resolved. ──────────────────────

    [Fact]
    public void Resolve_SessionsSqliteInHome_ReturnsTheSqliteFile()
    {
        var expected = Path.Combine(_home, "sessions.sqlite");
        Touch(expected);

        var resolution = CliStorePaths.Resolve("sessions", _home);

        resolution.Found.ShouldBeTrue();
        resolution.Path.ShouldBe(expected);
    }

    [Fact]
    public void ResolveSessionsDb_SessionsSqliteInHome_IsFoundByDebugSessionsReader()
    {
        var expected = Path.Combine(_home, "sessions.sqlite");
        Touch(expected);

        DebugSessionsCommand.ResolveSessionsDb(_home).ShouldBe(expected);
    }

    [Fact]
    public void ResolveSessionsDb_SessionsSqliteInHome_IsFoundBySubAgentReader()
    {
        var expected = Path.Combine(_home, "sessions.sqlite");
        Touch(expected);

        SubAgentCommand.ResolveSessionsDb(_home).ShouldBe(expected);
    }

    // ── AC7: data dir != home - the store lives ONLY in the data directory. ─────────────────────

    [Fact]
    public void Resolve_SessionsSqliteOnlyInDataDirectory_IsStillResolved()
    {
        var expected = Path.Combine(_dataDir, "sessions.sqlite");
        Touch(expected);
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, _dataDir);

        // No --target: the ambient data directory is the writer's location and must be searched.
        var resolution = CliStorePaths.Resolve("sessions", target: null);

        resolution.Found.ShouldBeTrue();
        resolution.Path.ShouldBe(expected);
    }

    [Fact]
    public void ResolveSessionsDb_DataDirectoryDiffersFromHome_IsFoundByDebugSessionsReader()
    {
        var expected = Path.Combine(_dataDir, "sessions.sqlite");
        Touch(expected);
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, _dataDir);

        DebugSessionsCommand.ResolveSessionsDb(null).ShouldBe(expected);
    }

    // ── AC1: tolerant naming, equivalent to DebugDbCommand's normalisation. ────────────────────

    [Theory]
    [InlineData("sessions")]
    [InlineData("sessions.sqlite")]
    [InlineData("sessions.db")]
    public void Resolve_TolerantNaming_AllSpellingsResolveTheSameStore(string requested)
    {
        var expected = Path.Combine(_home, "sessions.sqlite");
        Touch(expected);

        CliStorePaths.Resolve(requested, _home).Path.ShouldBe(expected);
    }

    [Fact]
    public void Resolve_LegacyDbFileOnDisk_IsStillResolved()
    {
        // Pre-existing .db stores from older deployments must keep working.
        var expected = Path.Combine(_home, "sessions.db");
        Touch(expected);

        CliStorePaths.Resolve("sessions", _home).Path.ShouldBe(expected);
    }

    [Fact]
    public void Resolve_BothExtensionsPresent_PrefersTheWritersSqliteFile()
    {
        Touch(Path.Combine(_home, "sessions.db"));
        var expected = Path.Combine(_home, "sessions.sqlite");
        Touch(expected);

        CliStorePaths.Resolve("sessions", _home).Path.ShouldBe(expected);
    }

    // ── AC2: explicit --target outranks an ambient BOTNEXUS_DATA_DIR. ──────────────────────────

    [Fact]
    public void Resolve_ExplicitTargetWins_OverAmbientDataDirectory()
    {
        Touch(Path.Combine(_dataDir, "sessions.sqlite"));
        var expected = Path.Combine(_home, "sessions.sqlite");
        Touch(expected);
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, _dataDir);

        CliStorePaths.Resolve("sessions", _home).Path.ShouldBe(expected);
    }

    // ── Nothing on disk: fall back to the WRITER's path, never a "sessions.db" guess. ──────────

    [Fact]
    public void Resolve_NoStoreOnDisk_FallsBackToTheWriterSqliteName()
    {
        var resolution = CliStorePaths.Resolve("sessions", _home);

        resolution.Found.ShouldBeFalse();
        Path.GetFileName(resolution.Path).ShouldBe("sessions.sqlite");
    }

    // ── AC4: the not-found message names what was sought and every directory searched. ─────────

    [Fact]
    public void BuildNotFoundMessage_NamesBothCandidateFilesAndEveryDirectorySearched()
    {
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, _dataDir);
        var attempted = Path.Combine(_home, "sessions.sqlite");

        var message = CliStorePaths.BuildNotFoundMessage("sessions", attempted);

        message.ShouldContain("sessions.sqlite");
        message.ShouldContain("sessions.db");
        message.ShouldContain(_dataDir);
        message.ShouldContain(_home);
    }

    // ── AC5: the cron reader shares the same helper. ───────────────────────────────────────────

    [Fact]
    public void ResolveCronDb_CronSqliteInHome_IsFoundByTheSharedResolver()
    {
        var expected = Path.Combine(_home, "cron.sqlite");
        Touch(expected);

        DebugCronCommand.ResolveCronDb(_home).ShouldBe(expected);
    }

    [Fact]
    public void ResolveCronDb_DataDirectoryDiffersFromHome_IsStillResolved()
    {
        var expected = Path.Combine(_dataDir, "cron.sqlite");
        Touch(expected);
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, _dataDir);

        DebugCronCommand.ResolveCronDb(null).ShouldBe(expected);
    }
}
