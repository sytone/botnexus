using System.Reflection;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for inheritance-policy classification coverage (#2424).
///
/// <para>
/// <b>Why this exists.</b> Inheritance behaviour was previously implied by whichever branch of
/// <see cref="AgentConfigMerger"/> happened to mention a property. A property nobody remembered to add
/// took the agent-local value and silently discarded the inherited one - indistinguishable, from
/// outside, from a property that was deliberately agent-local. #2423 is the worked example: an
/// inherited <c>activeHours</c> block vanished from the effective descriptor and the heartbeat cron
/// provisioner baked the resulting unrestricted schedule in without complaint.
/// </para>
///
/// <para>
/// <b>What this fence changes.</b> Declaring the intent turns a silent omission into a failing test at
/// the moment a property is added, while the author still knows what the property means. The failure
/// names the exact property path so the fix is a one-line edit rather than an investigation.
/// </para>
///
/// <para>
/// <b>Scope is deliberately <see cref="AgentDefinitionConfig"/> only.</b> That is precisely what #2424
/// asks for, and it is the domain whose inheritance semantics are actually specified today. Widening
/// the fence to the whole <c>PlatformConfig</c> graph would demand a policy on hundreds of properties
/// belonging to domains whose layering has not yet been decided - that audit is #2430, and forcing the
/// declaration before the decision would produce exactly the reflexive, unconsidered annotations this
/// fence exists to prevent.
/// </para>
///
/// <para>
/// <b>There is no baseline.</b> Unlike the <c>[ConfigField]</c> fence, this one starts at full coverage
/// because the classifying change annotates every property in the same commit. A baseline would be a
/// mechanism for tolerating unclassified properties, and none need tolerating.
/// </para>
/// </summary>
public sealed class ConfigInheritancePolicyFenceArchitectureTests
{
    [Fact]
    public void EveryAgentDefinitionProperty_CarriesAnInheritanceClassification()
    {
        var unclassified = FindUnclassifiedProperties(typeof(AgentDefinitionConfig));

        unclassified.ShouldBeEmpty(
            "Every settable property on AgentDefinitionConfig must declare a [ConfigInheritance] " +
            "policy (#2424). Without one, its layering behaviour is whatever a merge helper happens " +
            "to do, and an omission is invisible until an operator's inherited value silently " +
            "disappears (#2423). Choose the policy that describes what SHOULD happen - use LocalOnly " +
            "with a justification if the property is deliberately not inheritable.\nUnclassified:\n  " +
            string.Join("\n  ", unclassified));
    }

    [Fact]
    public void LocalOnlyAndRuntimeOnlyClassifications_CarryAJustification()
    {
        // These two assert a deliberate exception rather than a default behaviour, so a future reader
        // cannot reconstruct the reasoning from the policy name alone. Requiring the justification is
        // what stops LocalOnly becoming a convenient way to opt out of thinking about a property.
        var missing = ConfigInheritanceRegistry.GetClassifications(typeof(AgentDefinitionConfig))
            .Where(c => c.Policy is ConfigInheritancePolicy.LocalOnly or ConfigInheritancePolicy.RuntimeOnly)
            .Where(c => string.IsNullOrWhiteSpace(c.Justification))
            .Select(c => $"{c.PropertyPath} ({c.Policy})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            "LocalOnly and RuntimeOnly declare that a property deliberately does NOT inherit. That is " +
            "an assertion a future reader cannot verify from the policy name, so it must record why. " +
            "Add Justification = \"...\".\nMissing justification:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void CustomClassifications_NameAStrategy()
    {
        var missing = ConfigInheritanceRegistry.GetClassifications(typeof(AgentDefinitionConfig))
            .Where(c => c.Policy == ConfigInheritancePolicy.Custom)
            .Where(c => string.IsNullOrWhiteSpace(c.Strategy))
            .Select(c => c.PropertyPath)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            "A Custom policy means 'none of the declared policies describe this'. Without a named " +
            "Strategy it says only that the behaviour is unusual, which is not a classification.\n" +
            "Missing strategy:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void SecurityBoundaryBlocks_AreClassifiedReplaceAsUnit()
    {
        // Access policies and tool policies are coherent only as complete units. Deep-merging one across
        // layers can widen access beyond what EITHER layer authorised - a child's narrow allowlist
        // unioned with an inherited broad one grants exactly what the child was written to withhold.
        // This pins the specific policy rather than merely requiring that some policy exists, because
        // "classified, but classified wrongly" is the failure mode that would actually reach production.
        string[] securityBoundaryProperties =
        [
            nameof(AgentDefinitionConfig.FileAccess),
            nameof(AgentDefinitionConfig.ToolPolicy),
            nameof(AgentDefinitionConfig.SessionAccess),
            nameof(AgentDefinitionConfig.ConversationAccess),
        ];

        foreach (var propertyName in securityBoundaryProperties)
        {
            var classification = ConfigInheritanceRegistry
                .GetClassification(typeof(AgentDefinitionConfig), propertyName);

            classification.ShouldNotBeNull(
                $"{propertyName} is a security boundary and must carry an explicit classification.");

            classification.Policy.ShouldBe(
                ConfigInheritancePolicy.ReplaceAsUnit,
                $"{propertyName} bounds what an agent may reach. Merging it property-by-property " +
                "across layers can produce an effective policy that neither the defaults layer nor " +
                "the agent authored, and the drift is silently permissive rather than restrictive.");
        }
    }

    [Fact]
    public void Registry_ReportsDeclaredPolicyAndJustification()
    {
        // AC4: classification metadata must be queryable at runtime, not merely present in source for a
        // test to read. Downstream consumers (the merge engine, provenance reporting, the config UI) all
        // depend on this being a real query surface.
        var classification = ConfigInheritanceRegistry
            .GetClassification(typeof(AgentDefinitionConfig), nameof(AgentDefinitionConfig.Heartbeat));

        classification.ShouldNotBeNull();
        classification.Policy.ShouldBe(ConfigInheritancePolicy.DeepMerge);
        classification.PropertyPath.ShouldBe("AgentDefinitionConfig.Heartbeat");

        var localOnly = ConfigInheritanceRegistry
            .GetClassification(typeof(AgentDefinitionConfig), nameof(AgentDefinitionConfig.DisplayName));

        localOnly.ShouldNotBeNull();
        localOnly.Policy.ShouldBe(ConfigInheritancePolicy.LocalOnly);
        localOnly.Justification.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Fence_FlagsAnUnclassifiedProperty_AndIsNotVacuous()
    {
        // Vacuity guard. If the walker cannot flag an unclassified property on a synthetic type, then
        // EveryAgentDefinitionProperty_CarriesAnInheritanceClassification passing proves nothing at all.
        var unclassified = FindUnclassifiedProperties(typeof(PartiallyClassifiedFixture));

        unclassified.ShouldContain(
            $"{nameof(PartiallyClassifiedFixture)}.{nameof(PartiallyClassifiedFixture.Unclassified)}",
            "Vacuity guard: the walker must flag a settable property with no [ConfigInheritance].");

        unclassified.ShouldNotContain(
            $"{nameof(PartiallyClassifiedFixture)}.{nameof(PartiallyClassifiedFixture.Classified)}",
            "Positive pin: a classified property must not be reported, so the fence cannot be " +
            "satisfied by over-reporting.");
    }

    [Fact]
    public void Fence_IgnoresNonConfigurationMembers()
    {
        // Negative pin, mirroring the [ConfigField] fence: get-only computed members, [JsonIgnore]
        // members and indexers are never read from config.json, so they have no layering behaviour to
        // declare and demanding one would be noise.
        var unclassified = FindUnclassifiedProperties(typeof(NonConfigurationMemberFixture));

        unclassified.ShouldBeEmpty(
            "Get-only, [JsonIgnore] and indexer members are not settable configuration and cannot " +
            "participate in inheritance.\nOffenders:\n  " + string.Join("\n  ", unclassified));
    }

    private static IReadOnlyList<string> FindUnclassifiedProperties(Type type)
    {
        var unclassified = new List<string>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            if (property.SetMethod is not { IsPublic: true })
                continue;

            if (property.GetCustomAttribute<ConfigInheritanceAttribute>(inherit: false) is null)
                unclassified.Add($"{type.Name}.{property.Name}");
        }

        unclassified.Sort(StringComparer.Ordinal);
        return unclassified;
    }

    private sealed class PartiallyClassifiedFixture
    {
        [ConfigInheritance(ConfigInheritancePolicy.ScalarOverride)]
        public int Classified { get; set; }

        public int Unclassified { get; set; }
    }

    private sealed class NonConfigurationMemberFixture
    {
        public bool IsEnabled => true;

        [JsonIgnore]
        public string? RuntimeOnly { get; set; }

        public string this[int index] => index.ToString();
    }
}
