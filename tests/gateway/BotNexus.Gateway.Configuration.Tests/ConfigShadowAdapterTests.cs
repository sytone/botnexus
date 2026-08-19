using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Tests for the adapters connecting the store to the shadow harness (#2646 PBI 2).
/// </summary>
public sealed class ConfigShadowAdapterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"botnexus-shadow-adapter-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { /* best effort in temp */ }
            }
        }
    }

    private sealed class EnabledGate : IConfigShadowGate
    {
        public Task<bool> IsShadowEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>
    /// The end-to-end shape: a real document through a real store produces a clean diff.
    ///
    /// <para>
    /// This is the assertion that makes shadow mode meaningful rather than decorative - it exercises
    /// the exact path a gateway start will take, with the real SQLite store rather than a fake, and
    /// asserts the store is faithful under the harness's own comparison.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RealStoreThroughTheHostedService_ProducesACleanDiff()
    {
        var fs = new MockFileSystem();
        var configPath = "/cfg/config.json";
        fs.AddFile(configPath, new MockFileData("""
            {
              "gateway": { "threshold": 10 },
              "agents": { "alpha": { "model": "x", "memory": null } }
            }
            """));

        var store = new SqliteConfigStore($"Data Source={_dbPath}");
        var sink = new ConfigShadowReportSink();

        var service = new ConfigShadowMigrationHostedService(
            new FileConfigShadowSource(fs, configPath),
            new NoOpConfigStoreRoundTrip(),
            sink,
            new EnabledGate(),
            NullLogger<ConfigShadowMigrationHostedService>.Instance,
            timeProvider: null,
            entryRoundTrip: new ConfigStoreRoundTrip(store));

        await service.StartAsync(CancellationToken.None);

        sink.LastFailure.ShouldBeNull();
        sink.Latest.ShouldNotBeNull();
        sink.Latest!.IsClean.ShouldBeTrue($"the store must reproduce the document: {sink.Latest.Summary}");
        sink.Latest.SourceKeyCount.ShouldBeGreaterThan(0, "a clean diff over an empty input proves nothing");
    }

    /// <summary>
    /// The source reads the raw file and preserves an explicit null, which a bound
    /// <see cref="PlatformConfig"/> would have collapsed into an absent field.
    /// </summary>
    [Fact]
    public async Task FileSource_PreservesExplicitNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/cfg/config.json", new MockFileData("""{ "agents": { "alpha": { "memory": null } } }"""));

        var document = await new FileConfigShadowSource(fs, "/cfg/config.json")
            .ReadRawDocumentAsync(CancellationToken.None);

        document.ShouldNotBeNull();
        var entries = ConfigDocumentFlattener.Flatten(document);
        entries["agents.alpha.memory"].State.ShouldBe(ConfigValueState.ExplicitNull);
    }

    /// <summary>A missing file yields null, which the hosted service records as a failure not a clean diff.</summary>
    [Fact]
    public async Task FileSource_MissingFile_ReturnsNull()
    {
        var document = await new FileConfigShadowSource(new MockFileSystem(), "/cfg/absent.json")
            .ReadRawDocumentAsync(CancellationToken.None);

        document.ShouldBeNull();
    }

    /// <summary>
    /// The shadow pass never writes to <c>config.json</c> (#2766 AC6), asserted on the file's bytes.
    /// </summary>
    [Fact]
    public async Task ShadowRun_LeavesConfigJsonByteIdentical()
    {
        const string raw = """{ "gateway": { "threshold": 10 }, "agents": { "alpha": { "memory": null } } }""";
        var fs = new MockFileSystem();
        fs.AddFile("/cfg/config.json", new MockFileData(raw));
        var before = fs.File.ReadAllBytes("/cfg/config.json");

        var service = new ConfigShadowMigrationHostedService(
            new FileConfigShadowSource(fs, "/cfg/config.json"),
            new NoOpConfigStoreRoundTrip(),
            new ConfigShadowReportSink(),
            new EnabledGate(),
            NullLogger<ConfigShadowMigrationHostedService>.Instance,
            timeProvider: null,
            entryRoundTrip: new ConfigStoreRoundTrip(new SqliteConfigStore($"Data Source={_dbPath}")));

        await service.StartAsync(CancellationToken.None);

        fs.File.ReadAllBytes("/cfg/config.json").ShouldBe(before,
            "the shadow path must be strictly read-only with respect to config.json - that is what " +
            "makes rollback 'delete the store file' rather than a restore procedure");
    }

    /// <summary>
    /// The no-op document seam returns null rather than echoing the source.
    ///
    /// <para>
    /// Echoing would make a mis-registration - the entry seam missing - render as a perfectly clean
    /// diff, which is the one outcome a verification harness must never fake.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NoOpRoundTrip_DoesNotEchoTheSource()
    {
        var source = JsonNode.Parse("""{ "a": 1 }""")!.AsObject();

        var result = await new NoOpConfigStoreRoundTrip()
            .MigrateAndReadBackAsync(source, CancellationToken.None);

        result.ShouldBeNull();
    }
}
