using System.Collections;
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
/// <b>Baseline.</b> 210 pre-existing violations could not be fixed in one change, so they are captured
/// in <see cref="Baseline"/> - one entry per property, so partial progress is measurable and visible in
/// a diff. The baseline may <b>shrink but never grow</b>: annotating a property and deleting its entry
/// is always allowed; adding an entry to silence a new property is not. The count is asserted exactly so
/// a bulk suppression cannot hide in a large diff. Drawdown to zero is tracked by #3231; the first batch
/// (<c>GatewaySettingsConfig</c> and <c>CronJobConfig</c>, 36 properties) took it from 210 to 174.
/// </para>
/// </summary>
public sealed class ConfigFieldCoverageFenceArchitectureTests
{
    /// <summary>
    /// Pre-existing unannotated properties, one entry per <c>Type.Property</c>, captured 2026-08-01.
    ///
    /// <para>
    /// <b>This list may only shrink.</b> Annotate the property with <c>[ConfigField]</c> and delete its
    /// line. Never add an entry to silence a newly added property - that is precisely the drift this
    /// fence exists to stop, and <see cref="Baseline_DoesNotGrow"/> will fail if you try.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Baseline = LoadBaseline();

    /// <summary>
    /// Exact expected baseline size. Asserted separately from the contents so that a bulk suppression
    /// (for example pasting in a hundred new entries) is visible as a one-line numeric change in review
    /// rather than being buried in a large diff.
    /// </summary>
    private const int ExpectedBaselineCount = 174;

    [Fact]
    public void EveryConfigProperty_CarriesConfigFieldAttribute()
    {
        var violations = FindUnannotatedProperties(typeof(PlatformConfig))
            .Where(v => !Baseline.Contains(v.Key))
            .ToList();

        violations.ShouldBeEmpty(
            "Every public settable property on a type reachable from PlatformConfig must carry " +
            "[ConfigField]. An unannotated property is invisible to the settings UI and may have no " +
            "resolvable config path (#2701). Annotate it, or - only if it is genuinely not " +
            "configuration - keep it off the config graph.\nOffenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    [Fact]
    public void Baseline_DoesNotGrow()
    {
        Baseline.Count.ShouldBe(
            ExpectedBaselineCount,
            $"The [ConfigField] baseline must only shrink. Expected {ExpectedBaselineCount} entries. " +
            "If you annotated properties, lower ExpectedBaselineCount to match. If this number went " +
            "UP, a new unannotated property was silenced instead of fixed - annotate it instead.");
    }

    [Fact]
    public void Baseline_ContainsNoStaleEntries()
    {
        var live = FindUnannotatedProperties(typeof(PlatformConfig))
            .Select(v => v.Key)
            .ToHashSet(StringComparer.Ordinal);

        var stale = Baseline.Where(b => !live.Contains(b)).OrderBy(b => b, StringComparer.Ordinal).ToList();

        stale.ShouldBeEmpty(
            "These baseline entries no longer correspond to an unannotated property - the property was " +
            "annotated, renamed or removed. Delete them from the baseline (and lower " +
            "ExpectedBaselineCount) so the baseline keeps measuring real remaining work.\nStale:\n  " +
            string.Join("\n  ", stale));
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

    private static HashSet<string> LoadBaseline()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ConfigFieldCoverageFenceArchitectureTests).Assembly.Location)!;
        var path = Path.Combine(assemblyDir, "ConfigFieldCoverageBaseline.baseline");

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"ConfigField coverage baseline not found at '{path}'. It must be copied to the output " +
                "directory (CopyToOutputDirectory) or the fence cannot distinguish pre-existing " +
                "violations from new ones.", path);

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
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

    private sealed class NonConfigurationMemberFixture
    {
        public bool IsEnabled => true;

        [JsonIgnore]
        public string? RuntimeOnly { get; set; }

        public string this[int index] => index.ToString();
    }
}
