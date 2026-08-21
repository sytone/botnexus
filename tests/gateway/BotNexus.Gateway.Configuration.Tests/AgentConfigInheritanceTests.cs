using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Agent inheritance semantics as executed by the shared engine (#3485 D2).
/// </summary>
/// <remarks>
/// <para>
/// These began as a parity suite comparing the engine against the hand-written
/// <c>AgentConfigMerger</c>, which is how the divergences were found. With the merger deleted the
/// comparison target no longer exists, so each case now states its expectation absolutely.
/// </para>
/// <para>
/// The larger historical corpus lives in <c>AgentInheritanceBehaviourTests</c> - 723 lines
/// accumulated across #2137, #2423 and #2429, retargeted at the engine rather than deleted with the
/// implementation they were written against.
/// </para>
/// </remarks>
public sealed class AgentConfigInheritanceTests
{
    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    private static AgentDefinitionConfig Overlay(string defaultsJson, string agentJson)
        => AgentConfigInheritance.Overlay(Doc(defaultsJson), Doc(agentJson)).Effective;

    /// <summary>
    /// The headline case: a block set only in defaults must reach the agent whole.
    /// </summary>
    [Fact]
    public void DeepMergeBlock_PresentOnlyInDefaults_IsInherited()
    {
        var result = Overlay(
            """{ "heartbeat": { "enabled": true, "intervalMinutes": 30 } }""",
            """{ "displayName": "alpha" }""");

        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.Enabled.ShouldBe(true);
        result.Heartbeat.IntervalMinutes.ShouldBe(30);
    }

    /// <summary>
    /// #2423: setting ONE member of a block must not discard the siblings. This is the defect the
    /// whole inheritance effort exists to prevent.
    /// </summary>
    [Fact]
    public void DeepMergeBlock_AgentSetsOneMember_InheritsTheRest()
    {
        var result = Overlay(
            """{ "heartbeat": { "enabled": true, "intervalMinutes": 30 } }""",
            """{ "heartbeat": { "intervalMinutes": 5 } }""");

        result.Heartbeat!.IntervalMinutes.ShouldBe(5);
        result.Heartbeat.Enabled.ShouldBe(true, "the unmentioned sibling is inherited");
    }

    /// <summary>
    /// Explicit null suppresses rather than inherits - the distinction a bound POCO destroys.
    /// </summary>
    [Fact]
    public void ExplicitNull_SuppressesTheInheritedBlock()
    {
        var result = Overlay(
            """{ "memory": { "enabled": true } }""",
            """{ "memory": null }""");

        result.Memory.ShouldBeNull();
    }

    /// <summary>
    /// Absent means inherit, which must NOT be confused with the explicit-null case above. Without
    /// this pair the suppression test would pass against an engine that never inherits at all.
    /// </summary>
    [Fact]
    public void AbsentKey_InheritsRatherThanSuppresses()
    {
        var result = Overlay(
            """{ "memory": { "enabled": true } }""",
            """{ "displayName": "alpha" }""");

        result.Memory.ShouldNotBeNull();
        result.Memory!.Enabled.ShouldBe(true);
    }

    /// <summary>
    /// A list replaces wholesale rather than concatenating.
    /// </summary>
    [Fact]
    public void ToolIds_AgentValueReplacesWholesale()
    {
        var result = Overlay(
            """{ "toolIds": ["read", "write", "exec"] }""",
            """{ "toolIds": ["read"] }""");

        result.ToolIds.ShouldBe(["read"]);
    }

    /// <summary>
    /// An explicitly empty list survives as empty rather than falling back to defaults - "no tools"
    /// is a deliberate instruction, not an absence.
    /// </summary>
    [Fact]
    public void ToolIds_ExplicitEmptyList_IsNotTreatedAsAbsent()
    {
        var result = Overlay(
            """{ "toolIds": ["read", "write"] }""",
            """{ "toolIds": [] }""");

        result.ToolIds.ShouldNotBeNull();
        result.ToolIds!.Count.ShouldBe(0);
    }

    /// <summary>
    /// Identity fields never inherit. A display name set at the shared defaults layer would otherwise
    /// hand every agent the same identity.
    /// </summary>
    [Fact]
    public void IdentityFields_AreNeverInherited()
    {
        var result = Overlay(
            """{ "displayName": "shared", "emoji": "X" }""",
            """{ "displayName": "alpha" }""");

        result.DisplayName.ShouldBe("alpha");
        result.Emoji.ShouldBeNull("emoji is LocalOnly - a world-level value must not reach an agent");
    }

    /// <summary>A scalar set only in defaults is inherited.</summary>
    [Fact]
    public void Scalar_PresentOnlyInDefaults_IsInherited()
    {
        Overlay("""{ "toolTimeoutSeconds": 120 }""", """{ "displayName": "alpha" }""")
            .ToolTimeoutSeconds.ShouldBe(120);
    }

    /// <summary>A scalar set on the agent overrides the default.</summary>
    [Fact]
    public void Scalar_AgentValueOverrides()
    {
        Overlay("""{ "toolTimeoutSeconds": 120 }""", """{ "toolTimeoutSeconds": 30 }""")
            .ToolTimeoutSeconds.ShouldBe(30);
    }

    /// <summary>
    /// Nested deep-merge two levels down (memory.search), where a hand-written merger is most likely
    /// to have stopped recursing - and where the old one did (#3497).
    /// </summary>
    [Fact]
    public void NestedBlock_TwoLevelsDeep_MergesRatherThanReplaces()
    {
        var result = Overlay(
            """{ "memory": { "enabled": true, "search": { "defaultTopK": 10, "maxTopK": 50 } } }""",
            """{ "memory": { "search": { "defaultTopK": 3 } } }""");

        result.Memory!.Search!.DefaultTopK.ShouldBe(3);
        result.Memory.Enabled.ShouldBe(true);

        // #3497: the hand-written merger rebuilt this object from a two-property list and reset
        // maxTopK to its CLR default of 100, discarding a configured safety limit.
        result.Memory.Search.MaxTopK.ShouldBe(50, "the configured limit is preserved");
    }

    /// <summary>
    /// Every settable property of the nested search block survives, so a property added later cannot
    /// be silently dropped the way <c>MaxTopK</c> and <c>MaxLimit</c> were (#3497 AC4).
    /// </summary>
    [Fact]
    public void NestedBlock_PreservesEveryConfiguredProperty()
    {
        var search = Overlay(
            """{ "memory": { "search": { "defaultTopK": 7, "maxTopK": 40, "maxLimit": 25 } } }""",
            """{ "displayName": "alpha" }""").Memory!.Search!;

        search.DefaultTopK.ShouldBe(7);
        search.MaxTopK.ShouldBe(40);
        search.MaxLimit.ShouldBe(25);
    }

    /// <summary>
    /// A security boundary replaces as a unit: an agent setting one path list does NOT inherit the
    /// others, because a partially-inherited policy grants access neither layer authorised.
    /// </summary>
    [Fact]
    public void SecurityBoundary_ReplacesAsAUnit()
    {
        var result = Overlay(
            """{ "fileAccess": { "allowedReadPaths": ["/data"], "allowedWritePaths": ["/out"] } }""",
            """{ "fileAccess": { "allowedWritePaths": ["/tmp"] } }""");

        result.FileAccess!.AllowedWritePaths.ShouldBe(["/tmp"]);
        result.FileAccess.AllowedReadPaths.ShouldBeNull(
            "inheriting the world read allowlist would widen access beyond what the agent declared");
    }

    /// <summary>
    /// Provenance is the capability the hand-written merger could not offer: it answers "which layer
    /// supplied this value" without re-running the merge.
    /// </summary>
    [Fact]
    public void Provenance_IdentifiesTheSupplyingLayer()
    {
        var result = AgentConfigInheritance.Overlay(
            Doc("""{ "toolTimeoutSeconds": 120 }"""),
            Doc("""{ "displayName": "alpha" }"""));

        result.Overlay.GetProvenance("toolTimeoutSeconds")!.LayerName
            .ShouldBe(AgentConfigInheritance.DefaultsLayerName);
        result.Overlay.GetProvenance("displayName")!.LayerName
            .ShouldBe(AgentConfigInheritance.AgentLayerName);
    }

    /// <summary>
    /// Non-vacuity: the policy map must be keyed by the paths the engine ACTUALLY looks up.
    /// </summary>
    /// <remarks>
    /// This assertion caught the real defect during development. The first implementation camelCased
    /// the registry's type-qualified <c>PropertyPath</c> wholesale, producing keys like
    /// <c>agentDefinitionConfig.Heartbeat</c>. Every lookup missed, the engine silently fell back to
    /// ScalarOverride for every property, and ten of twelve cases still passed - scalars behave
    /// identically under either policy, so only nested blocks exposed it. Asserting the map is merely
    /// non-empty would have passed too.
    /// </remarks>
    [Fact]
    public void PolicyMap_IsKeyedByDocumentPaths_NotTypeQualifiedNames()
    {
        var map = AgentConfigInheritance.PolicyMap;

        map.Count.ShouldBeGreaterThan(20);

        map.ShouldContainKey("heartbeat");
        map.ShouldContainKey("displayName");
        map.ShouldContainKey("toolIds");

        map.Keys.ShouldNotContain(k => k.Contains('.'), "policy keys are leaf document paths");

        map["heartbeat"].ShouldBe(ConfigInheritancePolicy.DeepMerge);
        map["displayName"].ShouldBe(ConfigInheritancePolicy.LocalOnly);
    }
}
