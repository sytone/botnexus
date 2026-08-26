using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for <c>[ConfigField]</c> coverage across the configuration graph (#2701).
///
/// <para>
/// <b>Why this exists.</b> A measurement on 2026-08-01 found only <b>23.6%</b> of configuration
/// properties carried <see cref="ConfigFieldAttribute"/> (77 of 326 across 19 config POCOs). The gap
/// was not random: <c>PlatformConfig</c> held essentially every annotation and <em>every extension
/// config type sat at exactly zero</em>. Annotations were applied to the root document and never
/// propagated outward, leaving the settings UI structurally blind to extension configuration.
/// </para>
///
/// <para>
/// Until now the invariant "a config property has an attribute, a config path, and is persisted" was
/// enforced <em>socially</em> - by a genre of drift tests written one at a time after each individual
/// miss was discovered. At 23.6% coverage those tests were demonstrably not holding the line. This
/// fence makes the invariant <b>categorical</b>: adding an unannotated property to any type reachable
/// from the configuration root fails CI, naming the offending <c>Type.Property</c>.
/// </para>
///
/// <para>
/// <b>Discovery is structural, not a name list.</b> Types under test are those <em>reachable from
/// <see cref="PlatformConfig"/></em> by the same traversal <see cref="ConfigSecretMerge"/> performs
/// (nested BotNexus POCOs plus string-keyed dictionaries of POCOs). Reachability is the definition of
/// "is configuration" - it is exactly the graph that gets serialised, merged and redacted. A
/// hand-maintained list of type names would reproduce the very failure mode being fixed: it would have
/// to be remembered and extended, and #2481 records what happens when a rule is a reactive allow-list
/// instead of a categorical one. Helpers, services, writers, loaders, mergers and API response DTOs
/// are excluded <em>by construction</em> because nothing in the config graph points at them.
/// </para>
///
/// <para>
/// <b>No baseline.</b> The 210 pre-existing violations captured on 2026-08-01 were drawn down to zero
/// (#3231 batch 1 took 210 to 174; #3533 annotated the remaining 174). The baseline file, its exact-count
/// assertion and the stale-entry sweep are all <b>deleted</b> - there is nothing left to suppress, so the
/// fence is now unconditional. Re-introducing a baseline would re-introduce the drift this exists to stop.
/// </para>
///
/// <para>
/// <b>Four clauses.</b> A configuration property must (1) carry <see cref="ConfigFieldAttribute"/>,
/// (2) carry a <see cref="System.ComponentModel.DataAnnotations.DisplayAttribute"/> with a non-empty
/// <c>Name</c> and <c>Description</c> so a generated editor has real labels rather than a property name,
/// (3) sit at a config path the resolver can actually resolve, and (4) survive a round-trip through the
/// writer byte-identically. Presentation and path are asserted together because an annotated property with
/// no label is invisible in the UI in a different way than an unannotated one - both are failures of the
/// same invariant.
/// </para>
/// </summary>
public sealed class ConfigFieldCoverageFenceArchitectureTests
{
    // Pre-existing unannotated properties were drawn down to zero by #3533; the baseline file, its
    // exact-count assertion and the stale-entry sweep are deleted. This note is a tombstone so a future
    // reader does not re-add a suppression list believing one was always intended.

    [Fact]
    public void EveryConfigProperty_CarriesConfigFieldAttribute()
    {
        var violations = FindUnannotatedProperties(typeof(PlatformConfig));

        violations.ShouldBeEmpty(
            "Every public settable property on a type reachable from PlatformConfig must carry " +
            "[ConfigField]. An unannotated property is invisible to the settings UI and may have no " +
            "resolvable config path (#2701, #3533). Annotate it, or - only if it is genuinely not " +
            "configuration - keep it off the config graph.\nOffenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    [Fact]
    public void EveryConfigProperty_CarriesDisplayNameAndDescription()
    {
        var violations = FindUndescribedProperties(typeof(PlatformConfig));

        violations.ShouldBeEmpty(
            "Every configuration property must carry [Display(Name = ..., Description = ...)] with both " +
            "non-empty (#3533). A generated settings editor renders Name as the field label and " +
            "Description as its help text; without them the UI falls back to a raw property name and no " +
            "explanation. Boilerplate such as \"Gets or sets the X\" is not a description - say what the " +
            "setting does and what changing it costs.\nOffenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    [Fact]
    public void Fence_FlagsAnUnannotatedProperty_AndIsNotVacuous()
    {
        // Drive the same walker over a synthetic graph containing an unannotated property, nested and
        // dictionary-valued, to prove the fence catches the class of defect rather than passing
        // vacuously. If this fails, EveryConfigProperty_CarriesConfigFieldAttribute proves nothing.
        var violations = FindUnannotatedProperties(typeof(UnannotatedRootFixture));

        violations.ShouldContain(
            v => v.Key == $"{nameof(UnannotatedLeafFixture)}.{nameof(UnannotatedLeafFixture.RetryCount)}",
            "Vacuity guard: the walker must flag an unannotated property on a nested config POCO.");

        violations.ShouldContain(
            v => v.Key == $"{nameof(DictionaryValueFixture)}.{nameof(DictionaryValueFixture.Endpoint)}",
            "Vacuity guard: the walker must reach properties on POCOs held in a string-keyed " +
            "dictionary - the shape used by providers, apiKeys, satellites and peers.");
    }

    [Fact]
    public void Fence_DoesNotOverReport_WhenPropertyIsAnnotated()
    {
        // Positive pin for the structural predicate (AC2): an annotated property must not be reported,
        // so the fence cannot be satisfied by over-tightening into spurious annotations.
        var violations = FindUnannotatedProperties(typeof(WellAnnotatedFixture));

        violations.ShouldBeEmpty(
            "Positive pin: properties carrying [ConfigField] must be accepted. Offenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    [Fact]
    public void Fence_IgnoresNonConfigurationMembers()
    {
        // Negative pin for the structural predicate (AC2 / AC8). Get-only computed members, [JsonIgnore]
        // members and indexers are not configuration - they are never serialised into config.json - so
        // requiring an attribute on them would force annotation of things a user cannot set.
        var violations = FindUnannotatedProperties(typeof(NonConfigurationMemberFixture));

        violations.ShouldBeEmpty(
            "Negative pin: get-only, [JsonIgnore] and indexer members are not settable configuration " +
            "and must not be required to carry [ConfigField]. Offenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    // ── Walker ────────────────────────────────────────────────────────────────
    //
    // Mirrors ConfigSecretMerge's traversal (and the #2014 secret-annotation fence): recurse into
    // BotNexus config POCOs and into the value type of string-keyed dictionaries of POCOs. Reachability
    // from PlatformConfig IS the structural definition of "is configuration" - see the class summary.

    private sealed record Violation(string Key, string TypeName)
    {
        public string Describe() => $"{Key} (type {TypeName}) has no [ConfigField] attribute";
    }

    private static IReadOnlyList<Violation> FindUnannotatedProperties(Type root)
    {
        var violations = new List<Violation>();
        Walk(root, new HashSet<Type>(), violations);
        return violations;
    }

    private static void Walk(Type type, HashSet<Type> ancestry, List<Violation> violations)
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

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            // Only settable properties are configuration. A get-only computed member cannot be set from
            // config.json, so demanding an attribute on it would be noise.
            if (IsSettable(property))
            {
                var key = $"{type.Name}.{property.Name}";
                if (property.GetCustomAttribute<ConfigFieldAttribute>() is null)
                {
                    violations.Add(new Violation(key, propertyType.Name));
                }
            }

            if (TryGetDictionaryValueType(propertyType, out var valueType) && IsConfigPoco(valueType))
            {
                Walk(valueType, ancestry, violations);
            }
            else if (TryGetEnumerableElementType(propertyType, out var elementType) && IsConfigPoco(elementType))
            {
                Walk(elementType, ancestry, violations);
            }
            else if (IsConfigPoco(propertyType))
            {
                Walk(propertyType, ancestry, violations);
            }
        }

        ancestry.Remove(type);
    }

    private static IReadOnlyList<Violation> FindUndescribedProperties(Type root)
    {
        var violations = new List<Violation>();
        WalkForDisplay(root, new HashSet<Type>(), violations);
        return violations;
    }

    private static void WalkForDisplay(Type type, HashSet<Type> ancestry, List<Violation> violations)
    {
        if (!ancestry.Add(type))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (IsSettable(property))
            {
                var display = property.GetCustomAttribute<DisplayAttribute>();
                if (string.IsNullOrWhiteSpace(display?.Name) ||
                    string.IsNullOrWhiteSpace(display?.Description))
                {
                    violations.Add(new Violation($"{type.Name}.{property.Name}", propertyType.Name));
                }
            }

            if (TryGetDictionaryValueType(propertyType, out var valueType) && IsConfigPoco(valueType))
            {
                WalkForDisplay(valueType, ancestry, violations);
            }
            else if (TryGetEnumerableElementType(propertyType, out var elementType) && IsConfigPoco(elementType))
            {
                WalkForDisplay(elementType, ancestry, violations);
            }
            else if (IsConfigPoco(propertyType))
            {
                WalkForDisplay(propertyType, ancestry, violations);
            }
        }

        ancestry.Remove(type);
    }

    private static bool IsSettable(PropertyInfo property)
        => property.SetMethod is { IsPublic: true };

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

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type != typeof(string))
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(type))
            {
                if (candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    elementType = candidate.GetGenericArguments()[0];
                    return true;
                }
            }
        }

        elementType = typeof(object);
        return false;
    }

    private static IEnumerable<Type> EnumerateSelfAndInterfaces(Type type)
    {
        yield return type;
        foreach (var i in type.GetInterfaces())
            yield return i;
    }
    [Fact]
    public void DisplayFence_FlagsMissingAndBlankDescriptions_AndIsNotVacuous()
    {
        // Vacuity guard for the Display clause. Without this, EveryConfigProperty_CarriesDisplayName-
        // AndDescription could pass simply because the walker never reached anything. Three distinct
        // failure shapes are pinned: no [Display] at all, a Name with no Description, and a Description
        // that is whitespace - the last is the one a careless bulk-annotation would produce.
        var violations = FindUndescribedProperties(typeof(DisplayFixture));

        violations.ShouldContain(
            v => v.Key == $"{nameof(DisplayFixture)}.{nameof(DisplayFixture.NoDisplayAtAll)}",
            "Vacuity guard: a property with no [Display] must be flagged.");

        violations.ShouldContain(
            v => v.Key == $"{nameof(DisplayFixture)}.{nameof(DisplayFixture.NameButNoDescription)}",
            "Vacuity guard: [Display(Name)] without a Description must be flagged - a label with no " +
            "help text still leaves the operator guessing.");

        violations.ShouldContain(
            v => v.Key == $"{nameof(DisplayFixture)}.{nameof(DisplayFixture.WhitespaceDescription)}",
            "Vacuity guard: a whitespace Description must be flagged, not accepted as present.");

        violations.ShouldNotContain(
            v => v.Key == $"{nameof(DisplayFixture)}.{nameof(DisplayFixture.FullyDescribed)}",
            "Positive pin: a property with both Name and Description must be accepted.");
    }

    [Fact]
    public void EveryAnnotatedConfigProperty_SitsAtAResolvablePath()
    {
        // Clause 3: presentation metadata is only half of it - a field the UI can render but whose
        // path the resolver cannot address is still not editable. #2764 is the canonical failure:
        // a wrong traversal returns null, which is indistinguishable from "not configured".
        //
        // Resolution is DELEGATED to the production ConfigPathResolver, never restated here.
        // ConfigPathResolutionFenceArchitectureTests already probes path literals appearing in
        // consumer code; this asserts the complementary direction - that every property in the
        // annotated graph is addressable - which is what reflection-derived write keys require (#3532).
        //
        // GetAvailablePaths walks a live INSTANCE and stops at a null child, so a default-constructed
        // PlatformConfig would enumerate only its shallow paths and this fence would pass vacuously
        // over air. The graph is materialised first so the walk actually descends.
        var graph = Materialise(typeof(PlatformConfig), new HashSet<Type>());
        graph.ShouldNotBeNull();

        var resolver = new ConfigPathResolver();
        var available = resolver.GetAvailablePaths(graph!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        available.Count.ShouldBeGreaterThan(
            100,
            "Non-vacuity: the resolver must enumerate the materialised config graph. A small set " +
            $"means the walk stopped early and the assertion below proves nothing; got {available.Count}.");

        var unreachable = new List<string>();
        CollectAnnotatedPaths(typeof(PlatformConfig), string.Empty, new HashSet<Type>(), available, unreachable);

        unreachable.ShouldBeEmpty(
            "Every annotated configuration property must sit at a path IConfigPathResolver can " +
            "address. An unreachable property cannot be read or written by path, so the settings UI " +
            "and the reflection-derived write keys in #3532 cannot target it (#2764, #3533).\n" +
            "Unreachable:\n  " + string.Join("\n  ", unreachable));
    }

    [Fact]
    public void MaterialisedConfigGraph_SurvivesASerializerRoundTrip_ByteIdentically()
    {
        // Clause 4: the writer persists a document; if serialising the DTO graph is not stable then a
        // no-op save rewrites the file with different bytes, defeating the writer's own no-op
        // detection and producing spurious backups on every write.
        //
        // PersistOptions is deliberately mirrored from JsonConfigurationWriter rather than referenced,
        // because the writer's copy is private. If they diverge this test still asserts a real
        // property (round-trip stability); it simply stops speaking for the writer.
        var options = new JsonSerializerOptions { WriteIndented = true };

        var graph = Materialise(typeof(PlatformConfig), new HashSet<Type>());
        graph.ShouldNotBeNull();

        var first = JsonSerializer.Serialize(graph, typeof(PlatformConfig), options);
        var rehydrated = JsonSerializer.Deserialize(first, typeof(PlatformConfig), options);
        rehydrated.ShouldNotBeNull();
        var second = JsonSerializer.Serialize(rehydrated, typeof(PlatformConfig), options);

        first.Length.ShouldBeGreaterThan(
            500,
            "Non-vacuity: an empty or near-empty document would make the equality below trivially " +
            $"true; got {first.Length} chars.");

        second.ShouldBe(
            first,
            "Serialising the configuration graph must be stable: serialize -> deserialize -> " +
            "serialize has to produce identical bytes. If it does not, every save rewrites the file " +
            "even when nothing changed, so the writer's no-op check never fires and each write " +
            "generates a backup of an unchanged document.");
    }

    [Fact]
    public void UnmodelledKeys_AreLostByATypedRoundTrip_WhichIsWhyTheWriterCannotBeTyped()
    {
        // This is the gating precondition for the DTO-diff writer (#3532), pinned as an executable
        // fact rather than a comment.
        //
        // #2816: a whole-document write collapsed the channels section to {"enabled": true},
        // destroying Service Bus settings and two Telegram bot tokens. Teams was silently dead for
        // four days and the credentials were unrecoverable. ChannelConfig.AdditionalSettings
        // ([JsonExtensionData]) was the fix - for that ONE type.
        //
        // Every other config class still drops keys it does not model. So a writer that round-trips
        // through the typed graph cannot be safe until either every class carries [JsonExtensionData]
        // or the writer diffs and applies only changed keys, leaving unknown ones untouched. This
        // test fails the day that stops being true, which is exactly when #3532 can relax.
        const string withUnknownKey = """
            {
              "gateway": {
                "listenUrl": "http://localhost:5000",
                "aKeyNoDtoModels": "must-not-vanish"
              }
            }
            """;

        var typed = JsonSerializer.Deserialize<PlatformConfig>(
            withUnknownKey,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        typed.ShouldNotBeNull();

        var reserialised = JsonSerializer.Serialize(typed, new JsonSerializerOptions { WriteIndented = true });

        reserialised.Contains("aKeyNoDtoModels", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            "If an unmodelled key now SURVIVES a typed round-trip, the config DTOs gained " +
            "[JsonExtensionData] coverage and this pin is obsolete - delete it and revisit #3532, " +
            "because a typed whole-document write may finally be safe.");

        // The modelled sibling must survive, or the test proves nothing about key loss specifically.
        // Compared case-insensitively on purpose: these DTOs carry no [JsonPropertyName] and the
        // options here set no naming policy, so the emitted casing is an implementation detail. What
        // is being asserted is that the KEY survived, not how it was cased.
        reserialised.Contains("listenUrl", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            "Discrimination guard: a modelled key must survive the same round-trip, otherwise the " +
            "assertion above would pass simply because serialisation produced nothing useful.");
    }

    /// <summary>
    /// Builds a fully-populated instance of a config type so the resolver's instance walk descends
    /// the whole graph. Dictionaries and collections are left empty - their paths are addressed by
    /// key at runtime, not by a fixed segment, so seeding fake keys would assert a fiction.
    /// </summary>
    private static object? Materialise(Type type, HashSet<Type> ancestry)
    {
        if (!ancestry.Add(type))
            return null;

        try
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
                return null;

            var instance = Activator.CreateInstance(type);
            if (instance is null)
                return null;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !IsSettable(property))
                    continue;

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (!IsConfigPoco(propertyType) ||
                    TryGetDictionaryValueType(propertyType, out _) ||
                    TryGetEnumerableElementType(propertyType, out _))
                {
                    continue;
                }

                if (property.GetValue(instance) is not null)
                    continue;

                var child = Materialise(propertyType, ancestry);
                if (child is not null)
                    property.SetValue(instance, child);
            }

            return instance;
        }
        finally
        {
            ancestry.Remove(type);
        }
    }

    /// <summary>
    /// Derives the dotted path of each annotated leaf top-down, mirroring how the binder addresses
    /// configuration. Dictionary and collection members are recorded at their own path but not
    /// descended, because their children are addressed by runtime key or index.
    /// </summary>
    private static void CollectAnnotatedPaths(
        Type type,
        string prefix,
        HashSet<Type> ancestry,
        HashSet<string> available,
        List<string> unreachable)
    {
        if (!ancestry.Add(type))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var segment = ToCamelCase(property.Name);
            var path = string.IsNullOrEmpty(prefix) ? segment : $"{prefix}.{segment}";

            if (IsSettable(property) && property.GetCustomAttribute<ConfigFieldAttribute>() is not null)
            {
                if (!available.Contains(path))
                    unreachable.Add($"{type.Name}.{property.Name} -> '{path}'");
            }

            var isDictionary = TryGetDictionaryValueType(propertyType, out _);
            var isCollection = TryGetEnumerableElementType(propertyType, out _);

            if (!isDictionary && !isCollection && IsConfigPoco(propertyType))
                CollectAnnotatedPaths(propertyType, path, ancestry, available, unreachable);
        }

        ancestry.Remove(type);
    }

    private static string ToCamelCase(string name)
        => string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class UnannotatedRootFixture
    {
        public UnannotatedLeafFixture? Leaf { get; set; }
        public Dictionary<string, DictionaryValueFixture>? Targets { get; set; }
    }

    private sealed class UnannotatedLeafFixture
    {
        public int RetryCount { get; set; }
    }

    private sealed class DictionaryValueFixture
    {
        public string? Endpoint { get; set; }
    }

    private sealed class WellAnnotatedFixture
    {
        [ConfigField(Group = "Retries")]
        public int RetryCount { get; set; }

        [ConfigField(Group = "Network")]
        public string? Endpoint { get; set; }
    }

    private sealed class DisplayFixture
    {
        [ConfigField]
        public int NoDisplayAtAll { get; set; }

        [ConfigField]
        [Display(Name = "Has a name")]
        public int NameButNoDescription { get; set; }

        [ConfigField]
        [Display(Name = "Has a name", Description = "   ")]
        public int WhitespaceDescription { get; set; }

        [ConfigField]
        [Display(Name = "Retry count", Description = "How many times a failed call is retried before it is surfaced as an error.")]
        public int FullyDescribed { get; set; }
    }

    private sealed class NonConfigurationMemberFixture
    {
        public bool IsEnabled => true;

        [JsonIgnore]
        public string? RuntimeOnly { get; set; }

        public string this[int index] => index.ToString();
    }
}
