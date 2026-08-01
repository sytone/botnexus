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
    /// rewritten, the merger must still suppress inheritance for the explicitly-null agent.
    /// </summary>
    [Fact]
    public async Task AfterRoundTrip_MergerStillSuppressesInheritance_ForExplicitlyNullAgent()
    {
        await File.WriteAllTextAsync(_configPath, SourceJson);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        var config = await writer.ReadPlatformConfigAsync();
        config.Agents!["gamma"].Model = "gpt-5";
        await writer.UpdatePlatformConfigAsync(config, "explicit-null-merger-test");

        var reloaded = PlatformConfigLoader.Load(_configPath, validateOnLoad: false, fileSystem: _fileSystem);
        reloaded.AgentDefaults.ShouldNotBeNull();
        reloaded.AgentDefaults!.Memory.ShouldNotBeNull("World defaults must still carry a memory section.");

        JsonElement? alphaRaw = reloaded.AgentRawElements!["alpha"];
        var merged = AgentConfigMerger.Merge(reloaded.AgentDefaults, reloaded.Agents!["alpha"], alphaRaw);
        merged.Memory.ShouldBeNull(
            "#2705: 'memory': null means suppress inheritance; after a rewrite the agent must not inherit defaults.");

        // Control: the agent that never mentioned memory must still inherit.
        JsonElement? betaRaw = reloaded.AgentRawElements!["beta"];
        var mergedBeta = AgentConfigMerger.Merge(reloaded.AgentDefaults, reloaded.Agents!["beta"], betaRaw);
        mergedBeta.Memory.ShouldNotBeNull("An absent key must still inherit the world default.");
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
