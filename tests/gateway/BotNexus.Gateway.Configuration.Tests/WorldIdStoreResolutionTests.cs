using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// #3823: a home whose configuration lives in the SQLite store must resolve its EXISTING world
/// identity, not generate a new one.
/// </summary>
/// <remarks>
/// <para>
/// The world ID is resolved during DI registration, before any configuration provider exists, so
/// <see cref="WorldIdResolver"/> reads a persisted source directly. Reading only <c>config.json</c>
/// reports "nothing persisted" for a store-only home.
/// </para>
/// <para>
/// The failure that produces is not a missing value but a wrong one, and it cascades:
/// <c>SqliteStoreIdentityGuard</c> is configured with the fresh identity, and every existing
/// sessions/cron/webhook database in the home is then refused as belonging to another world. A live
/// store-only migration reproduced exactly that - 22 agents present in the store, a brand-new world
/// ID written over them, and identity-mismatch exceptions on every store the gateway opened.
/// </para>
/// </remarks>
public sealed class WorldIdStoreResolutionTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    private const string KnownWorld = "478420b7-2822-496e-85fd-20b18ad7d987";

    public WorldIdStoreResolutionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-worldid-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);

    private async Task SeedStoreAsync(string worldId)
    {
        var document = JsonNode.Parse($$"""
            {
              "worldId": "{{worldId}}",
              "gateway": { "listenUrl": "http://localhost:5000" }
            }
            """)!.AsObject();

        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
    }

    [Fact]
    public async Task TryRead_WithNoFileButAPopulatedStore_ReturnsTheStoredWorldId()
    {
        await SeedStoreAsync(KnownWorld);
        File.Exists(_configPath).ShouldBeFalse("this test covers the store-only home");

        var identity = WorldIdResolver.TryRead(_configPath, _fileSystem);

        identity.ShouldNotBeNull();
        identity!.Value.ShouldBe(KnownWorld);
    }

    /// <summary>
    /// The headline regression: <c>generated: false</c> is what stops the caller writing a fresh
    /// identity over a home that already has one.
    /// </summary>
    [Fact]
    public async Task Resolve_WithNoFileButAPopulatedStore_DoesNotGenerateANewIdentity()
    {
        await SeedStoreAsync(KnownWorld);

        var identity = WorldIdResolver.Resolve(_configPath, _fileSystem, out var generated);

        generated.ShouldBeFalse(
            "a home whose world ID is in the store already has an identity; generating a new one " +
            "reconfigures SqliteStoreIdentityGuard and orphans every existing store (#3823)");
        identity.Value.ShouldBe(KnownWorld);
    }

    /// <summary>
    /// The file still wins when present, so a store-backed home with both sources is unchanged.
    /// </summary>
    [Fact]
    public async Task TryRead_WithBothSources_PrefersTheFile()
    {
        await SeedStoreAsync(KnownWorld);
        const string fileWorld = "11111111-2222-3333-4444-555555555555";
        File.WriteAllText(_configPath, $$"""{ "worldId": "{{fileWorld}}" }""");

        WorldIdResolver.TryRead(_configPath, _fileSystem)!.Value.ShouldBe(fileWorld);
    }

    /// <summary>
    /// With neither source there genuinely is no identity, so generation remains correct. Pins the
    /// fallback so the fix does not overreach.
    /// </summary>
    [Fact]
    public void Resolve_WithNoFileAndNoStore_StillGenerates()
    {
        WorldIdResolver.Resolve(_configPath, _fileSystem, out var generated);
        generated.ShouldBeTrue();
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
