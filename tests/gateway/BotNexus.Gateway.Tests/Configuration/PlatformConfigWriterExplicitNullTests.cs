using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #2705: an <em>explicit null</em> in config.json is a distinct state from an absent key.
/// <see cref="AgentConfigMerger"/> reads explicit null as "suppress inheritance" and absence as
/// "inherit from defaults", so a whole-document write that erases explicit nulls silently inverts
/// the operator's intent. These tests pin the round-trip.
/// </summary>
public sealed class PlatformConfigWriterExplicitNullTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-writer-explicitnull-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public PlatformConfigWriterExplicitNullTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
    }

    private const string SourceJson = """
    {
      "gateway": { "listenUrl": "http://localhost:5005" },
      "agents": {
        "defaults": {
          "memory": { "enabled": true, "indexing": "auto" }
        },
        "alpha": {
          "provider": "copilot",
          "model": "gpt-4.1",
          "memory": null
        },
        "beta": {
          "provider": "copilot",
          "model": "gpt-4.1"
        },
        "gamma": {
          "provider": "copilot",
          "model": "gpt-4.1",
          "memory": { "enabled": false, "indexing": "manual" }
        }
      }
    }
    """;

    /// <summary>
    /// Acceptance criterion 1 and 3: a real write through <see cref="PlatformConfigWriter"/>
    /// (not a mock) must leave an explicitly-null merger-relevant key explicitly null.
    /// </summary>
    [Fact]
    public async Task UpdatePlatformConfigAsync_PreservesExplicitNull_ForMergerRelevantKey()
    {
        await File.WriteAllTextAsync(_configPath, SourceJson);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        // An unrelated whole-document write: the operator changed agent gamma's model, nothing else.
        var config = await writer.ReadPlatformConfigAsync();
        config.Agents!["gamma"].Model = "gpt-5";
        await writer.UpdatePlatformConfigAsync(config, "explicit-null-roundtrip-test");

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        var alpha = root["agents"]!["alpha"]!.AsObject();

        alpha.ContainsKey("memory").ShouldBeTrue(
            "#2705: an explicit null must survive a whole-document write; dropping the key means 'inherit'.");
        alpha["memory"].ShouldBeNull("The preserved key must still be an explicit JSON null, not an object.");
    }

    /// <summary>
    /// Acceptance criterion 2: absent / present-and-null / present-with-value must all remain
    /// distinguishable after a round-trip through the writer.
    /// </summary>
    [Fact]
    public async Task UpdatePlatformConfigAsync_KeepsAbsentNullAndValueDistinguishable()
    {
        await File.WriteAllTextAsync(_configPath, SourceJson);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        var config = await writer.ReadPlatformConfigAsync();
        config.Agents!["gamma"].Model = "gpt-5";
        await writer.UpdatePlatformConfigAsync(config, "explicit-null-tristate-test");

        var agents = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject()["agents"]!.AsObject();

        // absent
        agents["beta"]!.AsObject().ContainsKey("memory").ShouldBeFalse();
        // present and null
        agents["alpha"]!.AsObject().ContainsKey("memory").ShouldBeTrue();
        agents["alpha"]!["memory"].ShouldBeNull();
        // present with a value
        agents["gamma"]!["memory"].ShouldNotBeNull();
        agents["gamma"]!["memory"]!["indexing"]!.GetValue<string>().ShouldBe("manual");
    }

    /// <summary>
    /// Acceptance criterion 4: the consequence that actually matters. After the document has been
    /// rewritten, the explicit null must still be present in the raw JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This previously asserted through <c>AgentConfigMerger</c>, which #3515 deleted. The merger was
    /// only the observation instrument: what the test protects is that <c>PlatformConfigWriter</c>
    /// does not silently drop <c>"memory": null</c> when it rewrites the document, because a POCO
    /// round-trip collapses "absent" and "explicitly null" into the same null field.
    /// </para>
    /// <para>
    /// So the assertion now reads the raw element directly. That is strictly closer to the property
    /// being defended - the bytes on disk - and it survives whatever inheritance model replaces the
    /// merger (#3503), because it makes no claim about how the null is later interpreted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AfterRoundTrip_ExplicitNullSurvivesInTheDocument()
    {
        await File.WriteAllTextAsync(_configPath, SourceJson);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        var config = await writer.ReadPlatformConfigAsync();
        config.Agents!["gamma"].Model = "gpt-5";
        await writer.UpdatePlatformConfigAsync(config, "explicit-null-merger-test");

        var reloaded = PlatformConfigLoader.Load(_configPath, validateOnLoad: false, fileSystem: _fileSystem);
        reloaded.AgentDefaults.ShouldNotBeNull();
        reloaded.AgentDefaults!.Memory.ShouldNotBeNull("World defaults must still carry a memory section.");

        // alpha wrote "memory": null - present, and null.
        var alphaRaw = reloaded.AgentRawElements!["alpha"];
        alphaRaw.TryGetProperty("memory", out var alphaMemory).ShouldBeTrue(
            "#2705: 'memory': null must survive a rewrite; dropping the key turns suppression into inheritance.");
        alphaMemory.ValueKind.ShouldBe(JsonValueKind.Null);

        // Control: beta never mentioned memory, so the key must be ABSENT rather than written as null.
        var betaRaw = reloaded.AgentRawElements!["beta"];
        betaRaw.TryGetProperty("memory", out _).ShouldBeFalse(
            "an absent key must not be materialised as an explicit null - that would invert its meaning.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
