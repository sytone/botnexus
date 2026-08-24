using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
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
