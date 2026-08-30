using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #3560 acceptance suite: a KEY-PRESERVATION fence over the agent descriptor round trip.
/// Fails when saving an agent loses configuration that was already stored, whatever the cause.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists alongside
/// <c>PlatformConfigAgentRoundTripTests.FieldParity_EveryDescriptorProperty_HasAnExplicitPersistenceDecision</c>.</b>
/// That fence is a <em>property-classification</em> guarantee: it answers "has someone made an
/// explicit persistence decision about every settable <see cref="AgentDescriptor"/> property?" The
/// fences here are a <em>key-preservation</em> guarantee: they answer "does a real
/// read-modify-write lose keys that were already stored?" Those are different questions, and only
/// the second one is the #3547 defect class.
/// </para>
/// <para>
/// The two are genuinely independent, and the live incident proves it. On 2026-08-26 a single
/// <c>PUT /api/agents/aurum</c> changing one scalar returned 200 and removed eleven keys: ten
/// <c>extensions.*</c> namespaces and <c>maxConcurrentSessions: 0</c>, plus <c>fileAccess</c>
/// aliases rewritten to machine-specific absolute paths. <c>ExtensionConfig</c>,
/// <c>MaxConcurrentSessions</c> and <c>FileAccess</c> are all in
/// <see cref="AgentDescriptorConfigMapping.Persisted"/>, so the property-parity fence was GREEN on
/// that commit and would be green on any future variant of the same bug. A property can be
/// perfectly classified and still be projected back lossily. Both guarantees are required; neither
/// substitutes for the other.
/// </para>
/// <para>
/// Expectations are derived from the SEEDED DOCUMENT rather than from a hardcoded property list, so
/// a config key added in future is covered without editing this file. The seed deliberately carries
/// keys no descriptor property models, so the assertions cannot be satisfied by property-level
/// reasoning alone.
/// </para>
/// </remarks>
public sealed class PlatformConfigAgentKeyPreservationTests : IDisposable
{
    private const string AgentIdValue = "key-fence-agent";

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "botnexus-agent-key-fence-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly BotNexusHome _home;

    public PlatformConfigAgentKeyPreservationTests()
    {
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
        _home = new BotNexusHome(_fileSystem, _rootPath);
    }

    /// <summary>
    /// The headline fence: a real read -> change one scalar -> save must lose no stored key and
    /// must not rewrite any value it was not asked to change.
    /// </summary>
    [Fact]
    public async Task ReadModifyWrite_LosesNoStoredKey_AndRewritesNoUntouchedValue()
    {
        var resolver = SeedConfigWithUnmodelledAgentKeys();
        var seeded = Flatten(ReadConfigRoot());

        var descriptor = await ReadAgentAsync(resolver);
        // Exactly one unrelated scalar changes - the same shape as the live PUT that lost 11 keys.
        await CreateWriter(resolver).SaveAsync(
            descriptor with { Description = "edited-description" },
            CancellationToken.None);

        var after = Flatten(ReadConfigRoot());

        // (1) Nothing stored may disappear. A superset assertion covers every key, including ones
        // that no AgentDescriptor property models.
        var lost = seeded.Keys
            .Where(k => !after.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        lost.ShouldBeEmpty(
            "An agent save must not delete stored configuration keys (#3547/#3560). LOST KEYS: "
            + string.Join(", ", lost));

        // (2) Nothing untouched by the edit may be rewritten, not even into a semantically
        // equivalent form. This is what catches resolve-on-read / write-back-resolved asymmetry:
        // '@key-fence-location' resolved to an absolute path and written back compares unequal here
        // even though both spellings denote the same directory.
        var editedPath = $"agents.{AgentIdValue}.description";
        var changed = seeded
            .Where(kv => !string.Equals(kv.Key, editedPath, StringComparison.Ordinal))
            .Where(kv => after.TryGetValue(kv.Key, out var now)
                && !string.Equals(now, kv.Value, StringComparison.Ordinal))
            .Select(kv => $"{kv.Key}: {kv.Value} -> {after[kv.Key]}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        changed.ShouldBeEmpty(
            "An agent save must not rewrite values it was not asked to change (#3547/#3560). CHANGED: "
            + string.Join("; ", changed));

        // The edit itself must have landed, otherwise both assertions above are trivially
        // satisfiable by a writer that does nothing at all.
        after[editedPath].ShouldBe("\"edited-description\"");
    }

    /// <summary>
    /// Falsy-but-legitimate stored scalars (<c>0</c>, <c>false</c>, <c>""</c>) are values, not
    /// absences, and must survive an unrelated edit.
    /// </summary>
    [Fact]
    public async Task FalsyStoredScalars_AreNotTreatedAsUnset()
    {
        var resolver = SeedConfigWithUnmodelledAgentKeys();

        var descriptor = await ReadAgentAsync(resolver);
        await CreateWriter(resolver).SaveAsync(
            descriptor with { Thinking = "low" },
            CancellationToken.None);

        var entry = ReadConfigRoot()["agents"]![AgentIdValue]!.AsObject();

        // 0 is a LEGAL stored value ("unlimited"), not an absent one. The <= 0 sentinel treated it
        // as unset and removed the key on every unrelated edit.
        entry["maxConcurrentSessions"].ShouldNotBeNull();
        entry["maxConcurrentSessions"]!.GetValue<int>().ShouldBe(0);

        var metadata = entry["metadata"]!.AsObject();
        metadata["zeroCount"]!.GetValue<int>().ShouldBe(0);
        metadata["disabledFlag"]!.GetValue<bool>().ShouldBeFalse();
        metadata["blankString"]!.GetValue<string>().ShouldBe(string.Empty);
    }

    /// <summary>
    /// The extensions bag is an open map the descriptor models only partially, so "absent from the
    /// descriptor" cannot mean "delete from the file".
    /// </summary>
    [Fact]
    public async Task SaveWithPartialExtensionBag_KeepsUnmentionedExtensionNamespaces()
    {
        var resolver = SeedConfigWithUnmodelledAgentKeys();
        var before = ReadConfigRoot()["agents"]![AgentIdValue]!["extensions"]!.DeepClone().AsObject();

        // A portal PUT carries only the extensions its client knew about.
        var descriptor = await ReadAgentAsync(resolver);
        var partial = descriptor with
        {
            ExtensionConfig = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["botnexus-exec"] = JsonSerializer.Deserialize<JsonElement>("""{"shell":"pwsh"}""")
            }
        };

        await CreateWriter(resolver).SaveAsync(partial, CancellationToken.None);

        var after = ReadConfigRoot()["agents"]![AgentIdValue]!["extensions"]!.AsObject();

        // The extension the caller carried is written through ...
        JsonNode.DeepEquals(after["botnexus-exec"], JsonNode.Parse("""{"shell":"pwsh"}""")).ShouldBeTrue();

        // ... and every namespace it never mentioned survives byte-identically.
        foreach (var (key, value) in before)
        {
            if (string.Equals(key, "botnexus-exec", StringComparison.Ordinal))
                continue;
            after.ShouldContainKey(key);
            JsonNode.DeepEquals(after[key], value).ShouldBeTrue(
                $"extensions.{key} must survive a save that never mentioned it (#3547/#3560).");
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Seeds a config document whose agent entry carries keys no <see cref="AgentDescriptor"/>
    /// property models, and returns a resolver that knows the seeded <c>@key-fence-location</c>.
    /// </summary>
    /// <remarks>
    /// Beyond the ordinary modelled surface the seed carries: an extension namespace the descriptor
    /// cannot echo back (<c>botnexus-suppressed: null</c>, which the config source strips on read);
    /// an unknown nested object under the modelled <c>metadata</c> section; falsy-but-legitimate
    /// scalars (<c>0</c>, <c>false</c>, <c>""</c>); and portable <c>@location</c> aliases that the
    /// read side resolves to absolute paths.
    /// </remarks>
    private StubLocationResolver SeedConfigWithUnmodelledAgentKeys()
    {
        var locationPath = Path.Combine(_rootPath, "aliased-location");
        Directory.CreateDirectory(locationPath);
        Directory.CreateDirectory(Path.Combine(locationPath, "inner"));

        var seed = """
            {
              "version": 1,
              "agents": {
                "key-fence-agent": {
                  "provider": "github-copilot",
                  "model": "reasoning-model",
                  "displayName": "Key Fence Agent",
                  "enabled": true,
                  "description": "seeded-description",
                  "thinking": "high",
                  "maxConcurrentSessions": 0,
                  "metadata": {
                    "owner": "team-gateway",
                    "zeroCount": 0,
                    "disabledFlag": false,
                    "blankString": "",
                    "unmodelledNested": { "deep": { "keep": "me" }, "list": [ 1, 2 ] }
                  },
                  "fileAccess": {
                    "allowedReadPaths": [ "@key-fence-location" ],
                    "allowedWritePaths": [ "@key-fence-location/inner" ]
                  },
                  "extensions": {
                    "botnexus-exec": { "shell": "pwsh" },
                    "botnexus-skills": { "unknownKey": "keep-me", "nested": { "flag": false } },
                    "botnexus-suppressed": null
                  }
                }
              }
            }
            """;
        _fileSystem.File.WriteAllText(_configPath, seed);
        return new StubLocationResolver("key-fence-location", locationPath);
    }

    private JsonObject ReadConfigRoot()
        => JsonNode.Parse(_fileSystem.File.ReadAllText(_configPath))!.AsObject();

    private PlatformConfigAgentWriter CreateWriter(ILocationResolver? locationResolver)
        => new(new PlatformConfigWriter(_configPath, _fileSystem), _home, locationResolver);

    private async Task<AgentDescriptor> ReadAgentAsync(ILocationResolver? locationResolver)
    {
        var reloaded = await PlatformConfigLoader.LoadAsync(
            _configPath, CancellationToken.None, validateOnLoad: true, fileSystem: _fileSystem);
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(reloaded),
            _rootPath,
            new NullLogger<PlatformConfigAgentSource>(),
            locationResolver,
            MakeModelRegistry());
        return (await source.LoadAsync()).Single(d => d.AgentId.Value == AgentIdValue);
    }

    /// <summary>
    /// Flattens a JSON document to leaf-path -&gt; raw-JSON-value pairs so key presence and value
    /// identity can be asserted without naming any property.
    /// </summary>
    /// <remarks>
    /// Empty objects and empty arrays are recorded as leaves in their own right, so a write that
    /// empties one is still reported as a lost key rather than silently ignored.
    /// </remarks>
    private static Dictionary<string, string> Flatten(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(node, string.Empty, result);
        return result;

        static void Walk(JsonNode? current, string path, Dictionary<string, string> sink)
        {
            switch (current)
            {
                case JsonObject obj when obj.Count > 0:
                    foreach (var (key, value) in obj)
                        Walk(value, path.Length == 0 ? key : $"{path}.{key}", sink);
                    return;
                case JsonArray array when array.Count > 0:
                    for (var i = 0; i < array.Count; i++)
                        Walk(array[i], $"{path}[{i}]", sink);
                    return;
                default:
                    sink[path] = current?.ToJsonString() ?? "null";
                    return;
            }
        }
    }

    private static ModelRegistry MakeModelRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register("github-copilot", new LlmModel(
            Id: "reasoning-model",
            Name: "Reasoning Model",
            Api: "github-copilot-responses",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 200_000,
            MaxTokens: 64_000,
            SupportsExtraHighThinking: true));
        return registry;
    }

    /// <summary>Minimal <see cref="ILocationResolver"/> exposing a single filesystem location.</summary>
    private sealed class StubLocationResolver(string name, string path) : ILocationResolver
    {
        private readonly Location _location = new()
        {
            Name = name,
            Type = LocationType.FileSystem,
            Path = path
        };

        public Location? Resolve(string locationName)
            => string.Equals(locationName, _location.Name, StringComparison.OrdinalIgnoreCase)
                ? _location
                : null;

        public string? ResolvePath(string locationName) => Resolve(locationName)?.Path;

        public IReadOnlyList<Location> GetAll() => [_location];
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }
}
