using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for config secret annotation coverage (#2014, companion to the
/// attribute-driven redaction walker #2012 and the data-loss/secret-echo bugs #1954 / #1955).
///
/// <para>
/// The lesson behind the config-cleanup investigation was that per-instance tests asserted mock
/// behaviour, not real data flow, and nothing guarded the <b>category</b> of mistake: "a
/// secret-shaped field was added to the config graph but never marked
/// <c>[ConfigField(Secret = true)]</c>". A per-field test catches one field; it does not stop the
/// next unannotated secret from silently leaking through the redaction walker (which discovers its
/// secret paths purely from the annotation - see <see cref="ConfigSecretMerge"/>).
/// </para>
///
/// <para>
/// This fence is <b>reflection-based with zero runtime harness</b>: it walks the same typed
/// <see cref="PlatformConfig"/> POCO graph <see cref="ConfigSecretMerge"/> walks (nested config
/// POCOs plus string-keyed dictionaries of POCOs), and asserts a structural invariant - every
/// property whose <em>name</em> matches a secret-shaped pattern
/// (<c>*apiKey</c>, <c>*connectionString</c>, <c>*token</c>, <c>*secret</c>, <c>*password</c>,
/// case-insensitive) MUST carry <c>[ConfigField(Secret = true)]</c> (or the equivalent
/// <c>Widget == ConfigFieldWidget.Secret</c>). A newly added secret-shaped field that forgets the
/// annotation fails CI here - naming the offending <c>Type.Property</c> - instead of leaking in
/// production.
/// </para>
///
/// <para>
/// An explicit, reviewed <see cref="Exemptions"/> allow-list is the escape hatch for genuine
/// non-secrets that trip the name pattern (for example a hypothetical <c>tokenCount</c>), so the
/// guard stays honest rather than being weakened globally.
/// </para>
/// </summary>
public sealed class ConfigSecretAnnotationFitnessArchitectureTests
{
    /// <summary>
    /// Case-insensitive substrings that make a property name "secret-shaped". Mirrors the families
    /// the redaction fence (#1502) and the config-cleanup investigation identified.
    /// </summary>
    private static readonly string[] SecretNameFragments =
    {
        "apikey",
        "connectionstring",
        "token",
        "secret",
        "password",
    };

    /// <summary>
    /// Explicit, reviewed exemptions: <c>Type.Property</c> names that trip a secret-shaped pattern
    /// but are provably not secrets (for example a token <em>count</em>). Keep this list minimal and
    /// justified - every entry weakens the guard for exactly one property. Entries here are
    /// name-shaped false positives (numeric "token" tuning knobs, and a dictionary <em>container</em>
    /// whose nested secret property is itself annotated and redacted by the walk).
    /// </summary>
    private static readonly HashSet<string> Exemptions = new(StringComparer.Ordinal)
    {
        // Numeric compaction tuning knobs that merely contain the substring "token" - a ratio and a
        // window size, not credentials. Nothing sensitive to redact.
        "CompactionOptions.TokenThresholdRatio",
        "CompactionOptions.ContextWindowTokens",

        // A container: Dictionary<string, ApiKeyConfig>. The dictionary itself is not a secret value;
        // the actual secret is the nested ApiKeyConfig.ApiKey property, which IS annotated
        // [ConfigField(Secret = true)] and is reached (and redacted) by the ConfigSecretMerge walk
        // through this section. Redacting the container node itself would be meaningless.
        "GatewaySettingsConfig.ApiKeys",
    };

    [Fact]
    public void EverySecretShapedProperty_InPlatformConfigGraph_IsAnnotatedSecret()
    {
        var violations = FindUnannotatedSecretShapedProperties(typeof(PlatformConfig), Exemptions);

        violations.ShouldBeEmpty(
            "Every property in the PlatformConfig graph whose name is secret-shaped " +
            "(*apiKey / *connectionString / *token / *secret / *password) must be annotated " +
            "[ConfigField(Secret = true)] so the attribute-driven redaction walker (#2012, " +
            "ConfigSecretMerge) masks and restores it. An unannotated secret-shaped field leaks " +
            "in plaintext through GET /config and can be clobbered on PUT (#1955). If a listed " +
            "property is genuinely not a secret, add it to the reviewed Exemptions allow-list with " +
            "a justification. Offenders:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsAnUnannotatedSecretShapedProperty()
    {
        // Drive the exact same walker over a synthetic graph that contains an unannotated
        // secret-shaped property (nested and dictionary-valued) to prove the guard actually catches
        // the class of defect rather than passing vacuously.
        var violations = FindUnannotatedSecretShapedProperties(typeof(LeakyRootFixture), Exemptions);

        violations.ShouldContain(
            v => v.Contains(nameof(LeakyLeafFixture)) && v.Contains(nameof(LeakyLeafFixture.ApiKey)),
            "Vacuity guard: the walker must flag an unannotated secret-shaped property on a nested " +
            "config POCO. If this fails, the fitness function passes vacuously.");

        violations.ShouldContain(
            v => v.Contains(nameof(DictionaryValueFixture)) && v.Contains(nameof(DictionaryValueFixture.SecretToken)),
            "Vacuity guard: the walker must reach secret-shaped properties on POCOs held in a " +
            "string-keyed dictionary (the shape used by providers, apiKeys, satellites, peers).");
    }

    [Fact]
    public void Fence_DoesNotOverReport_WhenSecretShapedPropertyIsAnnotated()
    {
        // A correctly annotated secret-shaped property (both via Secret = true and via
        // Widget = Secret) must NOT be reported, so the fence does not over-tighten and force
        // spurious annotations elsewhere.
        var violations = FindUnannotatedSecretShapedProperties(typeof(WellAnnotatedFixture), Exemptions);

        violations.ShouldBeEmpty(
            "Positive pin: annotated secret-shaped properties (Secret = true or " +
            "Widget = ConfigFieldWidget.Secret) must be accepted. If this fails, the detector is " +
            "over-tight. Offenders:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_HonoursExemptionAllowList()
    {
        var key = $"{nameof(NonSecretTokenFixture)}.{nameof(NonSecretTokenFixture.TokenCount)}";

        // Without the exemption the count property is flagged (it is name-shaped like a secret)...
        var withoutExemption = FindUnannotatedSecretShapedProperties(
            typeof(NonSecretTokenFixture), new HashSet<string>(StringComparer.Ordinal));
        withoutExemption.ShouldContain(
            v => v.StartsWith(key, StringComparison.Ordinal),
            "Precondition: a non-secret token-shaped property is flagged when not exempted.");

        // ...and adding it to the allow-list suppresses the report.
        var withExemption = FindUnannotatedSecretShapedProperties(
            typeof(NonSecretTokenFixture), new HashSet<string>(StringComparer.Ordinal) { key });
        withExemption.ShouldNotContain(
            v => v.StartsWith(key, StringComparison.Ordinal),
            "The reviewed Exemptions allow-list must suppress a named non-secret property so the " +
            "guard stays honest rather than being weakened globally.");
    }

    // ── Walker ────────────────────────────────────────────────────────────────
    //
    // Mirrors ConfigSecretMerge's traversal: recurse into BotNexus config POCOs and into the value
    // type of string-keyed dictionaries of POCOs. This intentionally re-implements the walk (rather
    // than calling ConfigSecretMerge's internal discovery) because the invariant under test is over
    // property *names*, which the redaction walker does not track - it only follows annotations.

    private static IReadOnlyList<string> FindUnannotatedSecretShapedProperties(
        Type root, IReadOnlySet<string> exemptions)
    {
        var violations = new List<string>();
        Walk(root, new HashSet<Type>(), violations, exemptions);
        return violations;
    }

    private static void Walk(Type type, HashSet<Type> ancestry, List<string> violations, IReadOnlySet<string> exemptions)
    {
        // Guard against cycles in the type graph.
        if (!ancestry.Add(type))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            var key = $"{type.Name}.{property.Name}";
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (IsSecretShapedName(property.Name) && !exemptions.Contains(key) && !IsAnnotatedSecret(property))
            {
                violations.Add($"{key} (type {propertyType.Name}) is secret-shaped but not annotated [ConfigField(Secret = true)]");
            }

            // Recurse into nested config POCOs and dictionaries of POCOs, matching ConfigSecretMerge.
            if (TryGetDictionaryValueType(propertyType, out var valueType) && IsConfigPoco(valueType))
            {
                Walk(valueType, ancestry, violations, exemptions);
            }
            else if (IsConfigPoco(propertyType))
            {
                Walk(propertyType, ancestry, violations, exemptions);
            }
        }

        ancestry.Remove(type);
    }

    private static bool IsSecretShapedName(string name)
    {
        foreach (var fragment in SecretNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsAnnotatedSecret(PropertyInfo property)
    {
        var configField = property.GetCustomAttribute<ConfigFieldAttribute>();
        return configField is not null &&
            (configField.Secret || configField.Widget == ConfigFieldWidget.Secret);
    }

    private static bool IsConfigPoco(Type type)
        => type is { IsClass: true } &&
           type != typeof(string) &&
           type.Namespace is { } ns &&
           ns.StartsWith("BotNexus", StringComparison.Ordinal);

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        foreach (var candidate in EnumerateSelfAndInterfaces(type))
        {
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                candidate.GetGenericArguments()[0] == typeof(string))
            {
                valueType = candidate.GetGenericArguments()[1];
                return true;
            }
        }

        valueType = typeof(object);
        return false;
    }

    private static IEnumerable<Type> EnumerateSelfAndInterfaces(Type type)
    {
        if (type.IsInterface)
            yield return type;
        foreach (var iface in type.GetInterfaces())
            yield return iface;
    }

    // ── Fixtures (private, nested - never part of the real config graph) ────────

    /// <summary>Synthetic root whose graph contains unannotated secret-shaped properties.</summary>
    private sealed class LeakyRootFixture
    {
        public LeakyLeafFixture? Leaf { get; set; }
        public Dictionary<string, DictionaryValueFixture>? Entries { get; set; }
    }

    /// <summary>Nested POCO with an unannotated secret-shaped scalar.</summary>
    private sealed class LeakyLeafFixture
    {
        // Secret-shaped, deliberately NOT annotated - the fence must catch this.
        public string? ApiKey { get; set; }
    }

    /// <summary>POCO reached through a string-keyed dictionary, with an unannotated secret.</summary>
    private sealed class DictionaryValueFixture
    {
        // Secret-shaped, deliberately NOT annotated - the fence must reach it via the dictionary.
        public string? SecretToken { get; set; }
    }

    /// <summary>All secret-shaped properties correctly annotated - must not be reported.</summary>
    private sealed class WellAnnotatedFixture
    {
        [ConfigField(Secret = true)]
        public string? ApiKey { get; set; }

        [ConfigField(Widget = ConfigFieldWidget.Secret)]
        public string? Password { get; set; }

        [ConfigField(Secret = true)]
        public string? ConnectionString { get; set; }
    }

    /// <summary>A token-shaped property that is genuinely a non-secret count.</summary>
    private sealed class NonSecretTokenFixture
    {
        public int TokenCount { get; set; }
    }
}
