using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// #3654 AC3: the two numeric compaction tuning knobs are NOT credentials and must not appear
    /// in the discovered secret-path set. They were annotated <c>Secret = true</c>, which made every
    /// redacting read path serve the string <c>"***"</c> for a JSON number.
    /// </summary>
    [Fact]
    public void DiscoverSecretPaths_ExcludesNumericCompactionTuningFields()
    {
        var rendered = ConfigSecretMerge.DiscoverSecretPaths(typeof(PlatformConfig))
            .Select(static p => string.Join('.', p.Segments))
            .ToArray();

        rendered.ShouldNotContain("compaction.tokenThresholdRatio");
        rendered.ShouldNotContain("compaction.contextWindowTokens");

        // Nothing anywhere in the graph terminates on either name (e.g. a per-agent copy).
        rendered.Where(static p => p.EndsWith(".tokenThresholdRatio", StringComparison.Ordinal)).ShouldBeEmpty();
        rendered.Where(static p => p.EndsWith(".contextWindowTokens", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>
    /// #3654 AC4 — the fence. <see cref="ConfigSecretMerge"/> discovery is deliberately
    /// "best effort": any non-dictionary shape marked secret becomes a
    /// <c>SecretTerminal.Scalar</c> redaction target with no type check. Redaction then overwrites
    /// that node with the string placeholder <c>"***"</c>, so a non-<see cref="string"/> member
    /// marked secret silently produces a schema-invalid config document AND a SchemaForm password
    /// branch that writes a JSON string back into a numeric field.
    ///
    /// This test walks the REAL <see cref="PlatformConfig"/> graph, resolves every discovered
    /// scalar secret path back to the CLR member that produced it, and asserts that member is
    /// string-typed. A future numeric/boolean field annotated <c>Secret = true</c> reddens this
    /// test BY NAME rather than becoming a silent redaction target.
    /// </summary>
    [Fact]
    public void EveryScalarSecretPath_ResolvesToAStringTypedMember()
    {
        var offenders = new List<string>();

        foreach (var path in ConfigSecretMerge.DiscoverSecretPaths(typeof(PlatformConfig)))
        {
            if (path.Terminal != ConfigSecretMerge.SecretTerminal.Scalar)
                continue;

            var rendered = string.Join('.', path.Segments);
            var resolved = ResolveMemberType(typeof(PlatformConfig), path.Segments);

            // A path the walk emitted must be resolvable; an unresolvable one is itself a defect.
            resolved.ShouldNotBeNull($"Discovered secret path '{rendered}' did not resolve to a member.");

            var underlying = Nullable.GetUnderlyingType(resolved!) ?? resolved!;
            if (underlying != typeof(string))
                offenders.Add($"{rendered} : {underlying.Name}");
        }

        offenders.ShouldBeEmpty(
            "Every scalar secret path must resolve to a string-typed member, because redaction " +
            "replaces the node with the string placeholder \"***\". Non-string offenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// Resolves a discovered secret path (camelCase JSON segments, with <c>"*"</c> standing for a
    /// dictionary key) back to the CLR type of the terminal member, mirroring the name resolution
    /// in <c>ConfigSecretMerge.ResolveJsonName</c>. Returns null when a segment cannot be matched.
    /// </summary>
    private static Type? ResolveMemberType(Type root, IReadOnlyList<string> segments)
    {
        var current = root;
        Type? terminal = null;

        foreach (var segment in segments)
        {
            if (current is null)
                return null;

            if (segment == "*")
            {
                // Dictionary key wildcard: descend into the dictionary's value type.
                var valueType = DictionaryValueType(current);
                if (valueType is null)
                    return null;
                current = valueType;
                terminal = valueType;
                continue;
            }

            var property = current
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => string.Equals(JsonName(p), segment, StringComparison.Ordinal));

            if (property is null)
                return null;

            terminal = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            current = terminal;
        }

        return terminal;
    }

    private static string JsonName(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        return attribute is not null
            ? attribute.Name
            : JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    private static Type? DictionaryValueType(Type type)
    {
        var candidates = type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : type.GetInterfaces();
        foreach (var candidate in candidates)
        {
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                candidate.GetGenericArguments()[0] == typeof(string))
            {
                return candidate.GetGenericArguments()[1];
            }
        }

        return null;
    }

    /// <summary>
    /// #3654 AC5: the redaction applied by every <c>GET /api/config</c> read path must leave the
    /// numeric compaction knobs as JSON numbers matching <c>docs/botnexus-config.schema.json</c>
    /// (<c>tokenThresholdRatio</c>: number/double, <c>contextWindowTokens</c>: integer/int32),
    /// rather than overwriting them with the string placeholder.
    /// </summary>
    [Fact]
    public void Redact_LeavesNumericCompactionFieldsAsJsonNumbers()
    {
        var config = Parse("""
            {
              "apiKey": "sk-top-level-REAL",
              "compaction": {
                "tokenThresholdRatio": 0.7,
                "contextWindowTokens": 200000,
                "preservedTurns": 3
              }
            }
            """);

        ConfigSecretMerge.Redact(config);

        // The genuine credential is still redacted - this test does not weaken the secret contract.
        config["apiKey"]!.GetValue<string>().ShouldBe(ConfigSecretMerge.Placeholder);

        var ratio = config["compaction"]!["tokenThresholdRatio"]!;
        var window = config["compaction"]!["contextWindowTokens"]!;

        ratio.GetValueKind().ShouldBe(JsonValueKind.Number);
        window.GetValueKind().ShouldBe(JsonValueKind.Number);
        ratio.GetValue<double>().ShouldBe(0.7);
        window.GetValue<int>().ShouldBe(200_000);
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
