using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// The writer the gateway CONTAINER builds fans out to the store when one exists (#3527).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is separate from the fan-out unit tests.</b> Those construct a
/// <c>FanOutConfigurationWriter</c> by hand and prove it writes to both backends. That is necessary
/// and not sufficient: it says nothing about whether the gateway ever ASSEMBLES one.
/// </para>
/// <para>
/// A mutation removing the SQLite backend from <c>CreatePlatformConfigWriter</c> passed all twelve
/// fan-out tests. The registration is where the split-write defect would actually live - a perfectly
/// correct fan-out that nothing hands two backends to - so it needs its own assertion against the
/// real DI path.
/// </para>
/// </remarks>
public sealed class PlatformConfigWriterRegistrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;

    public PlatformConfigWriterRegistrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-writer-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
        _storePath = Path.Combine(_directory, "config.db");
        File.WriteAllText(_configPath, """{ "gateway": { "listenUrl": "http://localhost:5000" } }""");
    }

    /// <summary>
    /// Builds the writer through the shared factory every production call site uses.
    /// </summary>
    /// <remarks>
    /// <c>ConfigWriterFactory.Create</c> is the single place that decides which backends a config
    /// path needs. The DI registration delegates to it, and so do the seven CLI and API call sites
    /// that previously constructed <c>PlatformConfigWriter</c> directly - which is how the split
    /// this test catches got in.
    /// </remarks>
    private PlatformConfigWriter BuildWriterFromContainer()
        => ConfigWriterFactory.Create(_configPath, new FileSystem());

    /// <summary>
    /// With a store present, a write through the container-built writer lands in BOTH backends.
    /// </summary>
    [Fact]
    public async Task WithAStorePresent_TheContainerWriterWritesToBoth()
    {
        // Create the store the way an operator does, then build the container so the registration
        // sees it.
        await ConfigStoreBootstrap.PopulateAsync(
            _storePath,
            JsonNode.Parse("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""")!.AsObject());

        File.Exists(_storePath).ShouldBeTrue("the fixture must actually create a store");

        var writer = BuildWriterFromContainer();
        await writer.UpdateSectionAsync(
            "gateway",
            JsonNode.Parse("""{ "listenUrl": "http://localhost:8888" }""")!);

        // The FILE has it.
        var fromFile = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        fromFile["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:8888");

        // And so does the STORE - this is the assertion the mutation escaped.
        var entries = await new SqliteConfigStore($"Data Source={_storePath}").ReadEntriesAsync();
        entries.ShouldContainKey("gateway.listenUrl");
        entries["gateway.listenUrl"].Value.ShouldBe(
            "\"http://localhost:8888\"",
            "the container must register the SQLite backend when config.db exists, or a write " +
            "reaches only the file while the store keeps serving the stale value on read");
    }

    /// <summary>
    /// With no store, the container writer is JSON-only and creates no store. An installation that
    /// never opted in must be unaffected.
    /// </summary>
    [Fact]
    public async Task WithNoStore_TheContainerWriterWritesOnlyTheFile()
    {
        File.Exists(_storePath).ShouldBeFalse();

        var writer = BuildWriterFromContainer();
        await writer.UpdateSectionAsync(
            "gateway",
            JsonNode.Parse("""{ "listenUrl": "http://localhost:9999" }""")!);

        var fromFile = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        fromFile["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:9999");
        File.Exists(_storePath).ShouldBeFalse("a write must never create the store as a side effect");
    }

    public void Dispose()
    {
        try
        {
            ConfigStoreBootstrap.ReleaseConnections(_storePath);
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // SQLite handles can linger briefly on Windows.
        }
    }
}
