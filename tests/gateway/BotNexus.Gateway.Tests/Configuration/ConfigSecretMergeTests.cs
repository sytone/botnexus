using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Unit tests for the attribute-driven secret redaction in <see cref="ConfigSecretMerge"/> (#2012).
///
/// The secret-path set is discovered by reflecting over <c>[ConfigField(Secret = true)]</c>
/// annotations on the typed <see cref="PlatformConfig"/> graph rather than a hard-coded literal
/// field-name list. These tests lock in that:
/// <list type="bullet">
///   <item>Every historically-redacted secret path is still redacted and losslessly restored on a
///   round-trip (GET redact -> UI round-trips placeholder -> PUT -> restore).</item>
///   <item>Dictionary-valued secret sections (providers, gateway.apiKeys, gateway.locations,
///   gateway.satellites, gateway.crossWorld.peers, gateway.crossWorld.inbound.apiKeys) are covered.</item>
///   <item>A field newly annotated <c>[ConfigField(Secret = true)]</c> is redacted with NO change to
///   <see cref="ConfigSecretMerge"/> - proven via the reflection discovery over a nested POCO.</item>
/// </list>
/// </summary>
public sealed class ConfigSecretMergeTests
{
    private const string LiveConfigJson = """
        {
          "apiKey": "sk-top-level-REAL",
          "providers": {
            "github-copilot": { "apiKey": "sk-provider-REAL", "model": "claude" }
          },
          "gateway": {
            "apiKeys": {
              "primary": { "apiKey": "gw-REAL", "tenantId": "t1" }
            },
            "sessionStore": { "type": "Sqlite", "connectionString": "Data Source=REAL.db" },
            "locations": {
              "db1": { "type": "database", "connectionString": "Server=REAL;Pwd=hunter2" }
            },
            "satellites": {
              "sat-a": { "displayName": "Sat A", "apiKey": "sat_REAL_key" }
            },
            "crossWorld": {
              "peers": {
                "peerA": { "endpoint": "https://peer", "apiKey": "peer-REAL-key" }
              },
              "inbound": {
                "enabled": true,
                "apiKeys": { "worldX": "inbound-REAL-key", "worldY": "inbound-REAL-key-2" }
              }
            }
          }
        }
        """;

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Redact_MasksEveryKnownSecretPath()
    {
        var config = Parse(LiveConfigJson);

        ConfigSecretMerge.Redact(config);

        var p = ConfigSecretMerge.Placeholder;
        config["apiKey"]!.GetValue<string>().ShouldBe(p);
        config["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe(p);
        var gateway = config["gateway"]!;
        gateway["apiKeys"]!["primary"]!["apiKey"]!.GetValue<string>().ShouldBe(p);
        gateway["sessionStore"]!["connectionString"]!.GetValue<string>().ShouldBe(p);
        gateway["locations"]!["db1"]!["connectionString"]!.GetValue<string>().ShouldBe(p);
        gateway["satellites"]!["sat-a"]!["apiKey"]!.GetValue<string>().ShouldBe(p);
        gateway["crossWorld"]!["peers"]!["peerA"]!["apiKey"]!.GetValue<string>().ShouldBe(p);
        gateway["crossWorld"]!["inbound"]!["apiKeys"]!["worldX"]!.GetValue<string>().ShouldBe(p);
        gateway["crossWorld"]!["inbound"]!["apiKeys"]!["worldY"]!.GetValue<string>().ShouldBe(p);
    }

    [Fact]
    public void Redact_LeavesNonSecretFieldsUntouched()
    {
        var config = Parse(LiveConfigJson);

        ConfigSecretMerge.Redact(config);

        config["providers"]!["github-copilot"]!["model"]!.GetValue<string>().ShouldBe("claude");
        config["gateway"]!["sessionStore"]!["type"]!.GetValue<string>().ShouldBe("Sqlite");
        config["gateway"]!["apiKeys"]!["primary"]!["tenantId"]!.GetValue<string>().ShouldBe("t1");
        config["gateway"]!["crossWorld"]!["inbound"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void RestoreSecrets_RestoresEveryPlaceholderFromExisting()
    {
        var existing = Parse(LiveConfigJson);

        // Simulate the UI round-trip: redact a clone, then PUT it back with placeholders intact.
        var incoming = Parse(LiveConfigJson);
        ConfigSecretMerge.Redact(incoming);

        ConfigSecretMerge.RestoreSecrets(existing, incoming);

        incoming["apiKey"]!.GetValue<string>().ShouldBe("sk-top-level-REAL");
        incoming["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-provider-REAL");
        var gateway = incoming["gateway"]!;
        gateway["apiKeys"]!["primary"]!["apiKey"]!.GetValue<string>().ShouldBe("gw-REAL");
        gateway["sessionStore"]!["connectionString"]!.GetValue<string>().ShouldBe("Data Source=REAL.db");
        gateway["locations"]!["db1"]!["connectionString"]!.GetValue<string>().ShouldBe("Server=REAL;Pwd=hunter2");
        gateway["satellites"]!["sat-a"]!["apiKey"]!.GetValue<string>().ShouldBe("sat_REAL_key");
        gateway["crossWorld"]!["peers"]!["peerA"]!["apiKey"]!.GetValue<string>().ShouldBe("peer-REAL-key");
        gateway["crossWorld"]!["inbound"]!["apiKeys"]!["worldX"]!.GetValue<string>().ShouldBe("inbound-REAL-key");
        gateway["crossWorld"]!["inbound"]!["apiKeys"]!["worldY"]!.GetValue<string>().ShouldBe("inbound-REAL-key-2");
    }

    [Fact]
    public void RestoreSecrets_KeepsUserEditedSecretWhenNotPlaceholder()
    {
        var existing = Parse(LiveConfigJson);

        // User genuinely changed the provider key (not a placeholder) - it must NOT be reverted.
        var incoming = Parse(LiveConfigJson);
        ConfigSecretMerge.Redact(incoming);
        incoming["providers"]!["github-copilot"]!["apiKey"] = "sk-provider-NEW";

        ConfigSecretMerge.RestoreSecrets(existing, incoming);

        incoming["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-provider-NEW");
        // Untouched (still placeholder) top-level key is restored.
        incoming["apiKey"]!.GetValue<string>().ShouldBe("sk-top-level-REAL");
    }

    [Fact]
    public void DiscoverSecretPaths_IncludesAllKnownSecretSections()
    {
        var paths = ConfigSecretMerge.DiscoverSecretPaths(typeof(PlatformConfig));

        string Render(ConfigSecretMerge.SecretPath p) => string.Join('.', p.Segments);
        var rendered = paths.Select(Render).ToHashSet(StringComparer.Ordinal);

        rendered.ShouldContain("apiKey");
        rendered.ShouldContain("providers.*.apiKey");
        rendered.ShouldContain("gateway.apiKeys.*.apiKey");
        rendered.ShouldContain("gateway.sessionStore.connectionString");
        rendered.ShouldContain("gateway.locations.*.connectionString");
        rendered.ShouldContain("gateway.satellites.*.apiKey");
        rendered.ShouldContain("gateway.crossWorld.peers.*.apiKey");
        rendered.ShouldContain("gateway.crossWorld.inbound.apiKeys");
    }

    /// <summary>
    /// Acceptance criterion 4 (#2012): a field annotated <c>[ConfigField(Secret = true)]</c> is
    /// discovered as a secret path purely by reflection, with NO change to
    /// <see cref="ConfigSecretMerge"/>. Proven here against a fresh POCO type nested under a
    /// dictionary section: the discovery walk finds its secret path automatically, and a document
    /// shaped like that graph is redacted and restored without any hard-coded knowledge of the field.
    /// </summary>
    [Fact]
    public void NewlyAnnotatedSecretField_IsDiscoveredAndRoundTripped_WithoutCodeChange()
    {
        var paths = ConfigSecretMerge.DiscoverSecretPaths(typeof(FakeRoot));
        string Render(ConfigSecretMerge.SecretPath p) => string.Join('.', p.Segments);
        var rendered = paths.Select(Render).ToHashSet(StringComparer.Ordinal);

        // The brand-new secret field is discovered with no literal path list anywhere.
        rendered.ShouldContain("widgets.*.freshSecret");
        rendered.ShouldContain("topSecret");
        // A non-secret sibling is not treated as a secret.
        rendered.ShouldNotContain("widgets.*.label");

        var existing = new JsonObject
        {
            ["topSecret"] = "ROOT-REAL",
            ["widgets"] = new JsonObject
            {
                ["w1"] = new JsonObject { ["label"] = "hello", ["freshSecret"] = "WIDGET-REAL" },
            },
        };
        var incoming = existing.DeepClone().AsObject();

        RedactWith(paths, incoming);
        incoming["topSecret"]!.GetValue<string>().ShouldBe(ConfigSecretMerge.Placeholder);
        incoming["widgets"]!["w1"]!["freshSecret"]!.GetValue<string>().ShouldBe(ConfigSecretMerge.Placeholder);
        incoming["widgets"]!["w1"]!["label"]!.GetValue<string>().ShouldBe("hello");

        RestoreWith(paths, existing, incoming);
        incoming["topSecret"]!.GetValue<string>().ShouldBe("ROOT-REAL");
        incoming["widgets"]!["w1"]!["freshSecret"]!.GetValue<string>().ShouldBe("WIDGET-REAL");
    }

    // The production Redact/RestoreSecrets cache the PlatformConfig path set, so these helpers
    // exercise the same path-application engine against an arbitrary discovered set for the
    // synthetic FakeRoot graph. They reflect into the same internal engine via the public API by
    // building a PlatformConfig-independent walk - see ConfigSecretMerge for the shared logic.
    private static void RedactWith(IReadOnlyList<ConfigSecretMerge.SecretPath> paths, JsonObject config)
        => ConfigSecretMerge.RedactPaths(config, paths);

    private static void RestoreWith(IReadOnlyList<ConfigSecretMerge.SecretPath> paths, JsonObject existing, JsonObject target)
        => ConfigSecretMerge.RestorePaths(existing, target, paths);

    // ── Synthetic graph for the "new annotation, no code change" proof ──
    private sealed class FakeRoot
    {
        [ConfigField(Widget = ConfigFieldWidget.Secret, Secret = true)]
        public string? TopSecret { get; set; }

        public Dictionary<string, FakeWidget>? Widgets { get; set; }
    }

    private sealed class FakeWidget
    {
        public string? Label { get; set; }

        [ConfigField(Widget = ConfigFieldWidget.Secret, Secret = true)]
        public string? FreshSecret { get; set; }
    }
}
