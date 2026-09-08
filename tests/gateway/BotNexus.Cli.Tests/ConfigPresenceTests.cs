using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Cli;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

/// <summary>
/// #3823: "is this home configured?" must not be answered by <c>File.Exists(config.json)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ten CLI commands guarded on the config file and refused with "Run botnexus init first". That was
/// equivalent to the intended question only while JSON was the sole source. Once
/// <c>botnexus config store enable</c> makes <c>config.db</c> authoritative (#3514), a home can hold
/// a complete configuration and no file.
/// </para>
/// <para>
/// Verified on a live store-only instance before this fix: <c>config get</c>, <c>config set</c>,
/// <c>agent list</c>, <c>locations list</c>, <c>prompt list</c>, <c>session list</c>, <c>doctor</c>
/// and <c>validate</c> all refused while 690 entries sat in the store and the gateway served them.
/// </para>
/// <para>
/// The guards are kept rather than deleted: refusing loudly on a genuinely unconfigured home is
/// better than silently reporting defaults as the operator's settings. Only the predicate changes.
/// </para>
/// </remarks>
public sealed class ConfigPresenceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public ConfigPresenceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-config-presence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);

    [Fact]
    public async Task Exists_WithStoreButNoFile_IsTrue()
    {
        await ConfigStoreBootstrap.PopulateAsync(
            StorePath,
            JsonNode.Parse("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""")!.AsObject());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);

        File.Exists(_configPath).ShouldBeFalse();
        ConfigPresence.Exists(_configPath, _fileSystem).ShouldBeTrue(
            "a store-only home is configured; refusing here blocks every read command (#3823)");
    }

    [Fact]
    public void Exists_WithFileButNoStore_IsTrue()
    {
        File.WriteAllText(_configPath, """{ "gateway": {} }""");
        ConfigPresence.Exists(_configPath, _fileSystem).ShouldBeTrue();
    }

    /// <summary>
    /// The genuinely-unconfigured home must still be refused, so the fix does not turn a loud,
    /// correct failure into a silent one reporting defaults.
    /// </summary>
    [Fact]
    public void Exists_WithNeitherSource_IsFalse()
    {
        ConfigPresence.Exists(_configPath, _fileSystem).ShouldBeFalse();
    }

    [Fact]
    public void NotFoundMessage_NamesBothSources()
    {
        var message = ConfigPresence.NotFoundMessage(_configPath);
        message.ShouldContain("SQLite store",
            customMessage: "the operator must not be sent to 'botnexus init' when the real problem " +
                           "is a store they expected to be present");
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
