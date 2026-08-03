using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Behaviour-parity pins for configuration merging across the #2765 project extraction.
///
/// <para>
/// <b>Why this exists.</b> #2765 is a move plus a boundary, not a redesign - so the risk is not that
/// merging is wrong, it is that merging changes <em>silently</em> while 228 consuming files carry on
/// compiling. The extraction moved 57 files and changed two type locations; nothing in the compiler's
/// output would distinguish "moved correctly" from "moved and subtly altered". These tests pin the
/// five inheritance cases named in the issue's Behaviour parity section so a semantic change during
/// the move fails by name rather than surfacing later as an agent quietly acquiring a default it had
/// explicitly declined.
/// </para>
///
/// <para>
/// <b>Tri-state is the load-bearing case.</b> Inheritance is three-valued: a key <em>absent</em> from
/// the agent document means inherit, a key present with a <c>null</c> value means suppress the
/// inherited value, and a key with a value means override. Collapsing the first two is the highest-risk
/// item in #2646, because a relational column cannot represent the distinction without deliberate
/// modelling. #2706 fixed <see cref="ExtensionConfigMerger"/> to honour it; this pins that it survives
/// the move, so the SQLite work (#2646) starts from a proven baseline rather than an assumed one.
/// </para>
/// </summary>
public sealed class ExtensionConfigMergerParityTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Dictionary<string, JsonElement> Doc(string raw)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(raw).RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>Case 1: a key the agent never mentions is inherited from world defaults.</summary>
    [Fact]
    public void FieldUnset_InheritsTheWorldDefault()
    {
        var merged = ExtensionConfigMerger.Merge(
            Doc("""{ "alpha": { "timeout": 30 } }"""),
            Doc("""{ }"""));

        merged.ShouldContainKey("alpha");
        merged["alpha"].GetProperty("timeout").GetInt32().ShouldBe(30);
    }

    /// <summary>
    /// Case 2: a key present with an explicit <c>null</c> suppresses the inherited value.
    ///
    /// <para>
    /// This is the case that distinguishes tri-state from two-state, and the one a naive relational
    /// column erases. If this test ever passes with the value 30, inheritance has silently collapsed
    /// and every agent that explicitly declined a world default has had it handed back.
    /// </para>
    /// </summary>
    [Fact]
    public void FieldExplicitlyNull_SuppressesTheInheritedValue()
    {
        var merged = ExtensionConfigMerger.Merge(
            Doc("""{ "alpha": { "timeout": 30 } }"""),
            Doc("""{ "alpha": null }"""));

        merged.ContainsKey("alpha").ShouldBeFalse(
            "an explicit null must suppress the inherited world default, not be ignored in favour of it. " +
            "Seeing the world value here means unset and explicitly-null have been collapsed (#2706).");
    }

    /// <summary>Case 3: a value on the agent wins over the world default.</summary>
    [Fact]
    public void FieldSet_OverridesTheWorldDefault()
    {
        var merged = ExtensionConfigMerger.Merge(
            Doc("""{ "alpha": { "timeout": 30 } }"""),
            Doc("""{ "alpha": { "timeout": 90 } }"""));

        merged["alpha"].GetProperty("timeout").GetInt32().ShouldBe(90);
    }

    /// <summary>
    /// Case 4: overriding one member of a nested object leaves its siblings inherited.
    ///
    /// <para>
    /// Objects merge recursively; a partial override must not act as a wholesale replacement, or every
    /// unspecified sibling silently disappears.
    /// </para>
    /// </summary>
    [Fact]
    public void NestedObject_PartiallyOverridden_KeepsUnspecifiedSiblings()
    {
        var merged = ExtensionConfigMerger.Merge(
            Doc("""{ "alpha": { "timeout": 30, "retries": 5 } }"""),
            Doc("""{ "alpha": { "timeout": 90 } }"""));

        merged["alpha"].GetProperty("timeout").GetInt32().ShouldBe(90);
        merged["alpha"].GetProperty("retries").GetInt32().ShouldBe(
            5,
            "a partial object override must merge recursively, leaving unspecified siblings inherited.");
    }

    /// <summary>
    /// Case 5: arrays are replaced wholesale, never element-merged.
    ///
    /// <para>
    /// This is existing behaviour and worth pinning precisely <em>because</em> it is the one people
    /// expect to be different. An agent narrowing a list must get exactly its list, not the union.
    /// </para>
    /// </summary>
    [Fact]
    public void Array_IsReplacedWholesale_NotElementMerged()
    {
        var merged = ExtensionConfigMerger.Merge(
            Doc("""{ "alpha": { "tools": ["read", "write", "exec"] } }"""),
            Doc("""{ "alpha": { "tools": ["read"] } }"""));

        var tools = merged["alpha"].GetProperty("tools").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        tools.ShouldBe(["read"],
            "arrays replace wholesale. Producing a union would silently re-grant tools an agent " +
            "deliberately narrowed away.");
    }
}
