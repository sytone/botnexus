using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Skills.Tests;

/// <summary>
/// #3495: the Skills extension bound its per-agent config with a bare
/// <c>JsonSerializer.Deserialize&lt;T&gt;(element.GetRawText())</c>, and <c>System.Text.Json</c> is
/// case-sensitive by default. The documented camelCase key <c>allowSharedSkillManagement</c>
/// therefore never bound and silently kept its <c>false</c> default, so a fleet with the flag set
/// true everywhere had every <c>skill_manage scope=shared</c> write refused.
/// </summary>
/// <remarks>
/// <para>
/// The central test here is deliberately REFLECTIVE over <see cref="SkillsConfig"/> rather than a
/// list of per-field assertions (acceptance criterion 2). A per-field list would have to be edited
/// every time a field is added, which means the very next field added is the one that ships
/// unbindable - exactly the class of defect this issue is. Reflection makes coverage automatic:
/// a new property is covered the moment it exists.
/// </para>
/// <para>
/// The comparison is round-trip based. Every settable property on a fresh instance is set to a
/// value that DIFFERS from its own default, the instance is serialised, its keys are recursively
/// camelCased, and the camelCase document is bound back through the production seam. Any property
/// that fails to bind falls back to its default, which by construction differs from the value that
/// was written - so the round-trip comparison fails.
/// </para>
/// </remarks>
public sealed class SkillsConfigCaseInsensitiveBindingTests
{
    /// <summary>
    /// The bug, at the exact granularity the reporter hit it: camelCase in an agent descriptor,
    /// read through the same seam the write gate reads.
    /// </summary>
    [Fact]
    public void CamelCaseAllowSharedSkillManagement_BindsTrue_AtTheGate()
    {
        var descriptor = DescriptorWith("""
            { "allowSharedSkillManagement": true }
            """);

        var config = SkillsExtensionJson.ResolveSkillsConfig(descriptor);

        config.ShouldNotBeNull();
        config.AllowSharedSkillManagement.ShouldBeTrue(
            "A camelCase allowSharedSkillManagement:true must reach the shared-scope write gate. " +
            "Before #3495 this silently bound to nothing and defaulted false, so the descriptor API " +
            "reported true while the gate enforced false.");
    }

    /// <summary>
    /// Acceptance criterion 2: EVERY <see cref="SkillsConfig"/> field binds case-insensitively,
    /// asserted by reflection so a newly added field is covered without editing this test.
    /// </summary>
    [Fact]
    public void EverySkillsConfigProperty_BindsFromCamelCase()
    {
        var seeded = SeedWithNonDefaultValues(new SkillsConfig());
        var pascalJson = JsonSerializer.Serialize(seeded);
        var camelJson = ToCamelCaseKeys(JsonNode.Parse(pascalJson)!).ToJsonString();

        var bound = SkillsExtensionJson.Bind<SkillsConfig>(
            JsonDocument.Parse(camelJson).RootElement);

        bound.ShouldNotBeNull();
        JsonSerializer.Serialize(bound).ShouldBe(
            pascalJson,
            "Every SkillsConfig property must survive a camelCase round-trip. Each property was " +
            "seeded to a value that differs from its own default, so a property that failed to " +
            "bind reverts to that default and shows up as a difference here. If this fails after " +
            "adding a property, the property is not binding case-insensitively - do NOT relax this " +
            "assertion, fix the binding.\ncamelCase input: " + camelJson);
    }

    /// <summary>
    /// Anti-vacuity for the reflective test. A round-trip test proves nothing if the seeding
    /// silently covered zero properties, if the camelCase transform was a no-op, or if the seeded
    /// values happened to equal the defaults it is supposed to differ from.
    /// </summary>
    [Fact]
    public void ReflectiveBindingHarness_IsNotVacuous()
    {
        var properties = SettableProperties();

        properties.Count.ShouldBeGreaterThan(
            5,
            "Reflection must find the real SkillsConfig surface; an empty or tiny set means the " +
            "harness stopped covering anything.");

        var seeded = SeedWithNonDefaultValues(new SkillsConfig());
        var fresh = new SkillsConfig();
        foreach (var property in properties)
        {
            var seededValue = JsonSerializer.Serialize(property.GetValue(seeded));
            var defaultValue = JsonSerializer.Serialize(property.GetValue(fresh));
            seededValue.ShouldNotBe(
                defaultValue,
                $"Property '{property.Name}' was seeded with its own default value, so a total " +
                "binding failure would be indistinguishable from success. Extend " +
                nameof(NonDefaultValue) + " to produce a distinct value for its type.");
        }

        var pascalJson = JsonSerializer.Serialize(seeded);
        var camelJson = ToCamelCaseKeys(JsonNode.Parse(pascalJson)!).ToJsonString();
        camelJson.ShouldNotBe(
            pascalJson,
            "The camelCase transform must actually change the document, or the test is asserting " +
            "PascalCase binds to PascalCase - which was never in doubt.");

        // And the case-sensitive binder really does lose the data, which is the defect itself.
        var caseSensitive = JsonSerializer.Deserialize<SkillsConfig>(camelJson);
        JsonSerializer.Serialize(caseSensitive).ShouldNotBe(
            pascalJson,
            "Default (case-sensitive) options must still drop camelCase keys. If this ever stops " +
            "being true the platform default changed and this fence is measuring nothing.");
    }

    /// <summary>
    /// Acceptance criterion 3, behavioural half: the shared instance is genuinely configured for
    /// case-insensitive binding. The architecture fence proves every call site USES it; this
    /// proves using it is worth something.
    /// </summary>
    [Fact]
    public void SharedOptionsInstance_IsCaseInsensitive()
        => SkillsExtensionJson.Options.PropertyNameCaseInsensitive.ShouldBeTrue(
            "SkillsExtensionJson.Options is the single seam every Skills deserialization routes " +
            "through; if it is not case-insensitive the fence guards an empty promise.");

    /// <summary>
    /// A malformed config block must yield null rather than throwing - a bad operator edit must
    /// not take the agent's whole tool contribution down. This is pre-existing behaviour the
    /// refactor to a shared seam must preserve.
    /// </summary>
    [Fact]
    public void MalformedConfig_BindsToNull_RatherThanThrowing()
    {
        var descriptor = DescriptorWith("""
            { "maxLoadedSkills": "not-a-number" }
            """);

        SkillsExtensionJson.ResolveSkillsConfig(descriptor).ShouldBeNull();
    }

    /// <summary>An agent with no skills config block binds to null, not to a phantom instance.</summary>
    [Fact]
    public void AbsentConfigBlock_BindsToNull()
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
        };

        SkillsExtensionJson.ResolveSkillsConfig(descriptor).ShouldBeNull();
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static AgentDescriptor DescriptorWith(string skillsConfigJson) => new()
    {
        AgentId = AgentId.From("test-agent"),
        DisplayName = "Test Agent",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        ExtensionConfig = new Dictionary<string, JsonElement>
        {
            [SkillsExtensionJson.ExtensionId] = JsonDocument.Parse(skillsConfigJson).RootElement,
        },
    };

    private static IReadOnlyList<PropertyInfo> SettableProperties() =>
        typeof(SkillsConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Sets every settable property to a value that differs from the value it already holds, so a
    /// property that fails to bind is always detectable.
    /// </summary>
    private static SkillsConfig SeedWithNonDefaultValues(SkillsConfig config)
    {
        foreach (var property in SettableProperties())
            property.SetValue(config, NonDefaultValue(property.PropertyType, property.GetValue(config)));

        return config;
    }

    private static object? NonDefaultValue(Type type, object? current)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool))
            return !(bool)(current ?? false);

        if (underlying == typeof(int))
            return (int)(current ?? 0) + 17;

        if (underlying == typeof(long))
            return (long)(current ?? 0L) + 17L;

        if (underlying == typeof(string))
            return current as string == "seeded-value" ? "seeded-value-2" : "seeded-value";

        if (underlying.IsEnum)
        {
            var values = Enum.GetValues(underlying).Cast<object>().ToList();
            return values.FirstOrDefault(v => !Equals(v, current)) ?? values[0];
        }

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = underlying.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(underlying)!;
            list.Add(NonDefaultValue(elementType, null));
            return list;
        }

        if (underlying.IsClass)
        {
            var instance = Activator.CreateInstance(underlying)!;
            foreach (var property in underlying
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.CanWrite))
            {
                property.SetValue(
                    instance,
                    NonDefaultValue(property.PropertyType, property.GetValue(instance)));
            }

            return instance;
        }

        throw new NotSupportedException(
            $"SkillsConfig gained a property of type '{type}' that the #3495 reflective binding " +
            "harness cannot seed. Teach NonDefaultValue about it - do not exclude the property, " +
            "or it ships without case-insensitivity coverage.");
    }

    /// <summary>Recursively lower-cases the first character of every property name in the tree.</summary>
    private static JsonNode ToCamelCaseKeys(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj.ToList())
                {
                    result[CamelCase(key)] = value is null ? null : ToCamelCaseKeys(value.DeepClone());
                }

                return result;
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array.ToList())
                    result.Add(item is null ? null : ToCamelCaseKeys(item.DeepClone()));

                return result;
            }

            default:
                return node.DeepClone();
        }
    }

    private static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
