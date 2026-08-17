using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration.Inheritance;
using BotNexus.Gateway.Configuration.Shadow;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Behavioural coverage for the shared inheritance overlay engine (#2425).
///
/// <para>
/// <b>Why these tests assert on values rather than on merge internals.</b> The defect class this
/// engine exists to eliminate is a merge helper that silently omits a property (#2137, #2423). A test
/// that asserted "the merge helper was called" would pass against exactly that bug. Every case here
/// therefore asserts the effective value an operator would observe.
/// </para>
/// </summary>
public sealed class ConfigInheritanceEngineTests
{
    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    private static ConfigInheritanceEngine Engine(params (string Path, ConfigInheritancePolicy Policy)[] policies)
    {
        var map = policies.ToDictionary(p => p.Path, p => p.Policy, StringComparer.Ordinal);
        return new ConfigInheritanceEngine(new MapConfigPolicyResolver(map));
    }

    private static ConfigOverlayResult Overlay(
        ConfigInheritanceEngine engine,
        string defaultsJson,
        string agentJson)
        => engine.Overlay(new[]
        {
            new ConfigLayer("agents.defaults", Doc(defaultsJson)),
            new ConfigLayer("agents.test-agent", Doc(agentJson)),
        });

    // -------------------------------------------------------------------------
    // AC1 - standard policies
    // -------------------------------------------------------------------------

    [Fact]
    public void ScalarOverride_ChildValueWins()
    {
        var engine = Engine(("toolTimeoutSeconds", ConfigInheritancePolicy.ScalarOverride));

        var result = Overlay(engine, """{"toolTimeoutSeconds": 30}""", """{"toolTimeoutSeconds": 90}""");

        result.Document["toolTimeoutSeconds"]!.GetValue<int>().ShouldBe(90);
    }

    [Fact]
    public void ScalarOverride_AbsentChildInheritsParent()
    {
        var engine = Engine(("toolTimeoutSeconds", ConfigInheritancePolicy.ScalarOverride));

        var result = Overlay(engine, """{"toolTimeoutSeconds": 30}""", """{"model": "gpt-5"}""");

        result.Document["toolTimeoutSeconds"]!.GetValue<int>().ShouldBe(30);
    }

    [Fact]
    public void DeepMerge_ChildSetsOneMember_AndInheritsTheRest()
    {
        // The behaviour #2137 names explicitly: setting only enabled:false must not discard an
        // inherited intervalMinutes or quietHours block.
        var engine = Engine(("heartbeat", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(
            engine,
            """{"heartbeat": {"enabled": true, "intervalMinutes": 30, "ackMaxChars": 300}}""",
            """{"heartbeat": {"enabled": false}}""");

        var heartbeat = result.Document["heartbeat"]!.AsObject();
        heartbeat["enabled"]!.GetValue<bool>().ShouldBeFalse("the child explicitly set it");
        heartbeat["intervalMinutes"]!.GetValue<int>().ShouldBe(30, "the child never mentioned it");
        heartbeat["ackMaxChars"]!.GetValue<int>().ShouldBe(300, "#2423 - this field was silently dropped before");
    }

    [Fact]
    public void DeepMerge_NestedObjectsMergeFieldByField()
    {
        var engine = Engine(("heartbeat", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(
            engine,
            """{"heartbeat": {"quietHours": {"enabled": true, "start": "23:00", "end": "07:00"}}}""",
            """{"heartbeat": {"quietHours": {"start": "22:00"}}}""");

        var quiet = result.Document["heartbeat"]!["quietHours"]!.AsObject();
        quiet["start"]!.GetValue<string>().ShouldBe("22:00");
        quiet["end"]!.GetValue<string>().ShouldBe("07:00");
        quiet["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void ReplaceAsUnit_ChildListReplacesEntirely_AndDoesNotUnion()
    {
        // Unioning a child's narrow allowlist with an inherited broad one grants access the child was
        // written to deny. That is a security regression, not a merge convenience.
        var engine = Engine(("toolIds", ConfigInheritancePolicy.ReplaceAsUnit));

        var result = Overlay(
            engine,
            """{"toolIds": ["read", "write", "exec"]}""",
            """{"toolIds": ["read"]}""");

        var tools = result.Document["toolIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        tools.ShouldBe(new[] { "read" });
        tools.ShouldNotContain("exec", "an inherited broad permission must not survive a narrowing override");
    }

    [Fact]
    public void ReplaceAsUnit_NestedObjectIsReplacedWholesale_NotMerged()
    {
        var engine = Engine(("fileAccess", ConfigInheritancePolicy.ReplaceAsUnit));

        var result = Overlay(
            engine,
            """{"fileAccess": {"allowedReadPaths": ["/a"], "deniedPaths": ["/secret"]}}""",
            """{"fileAccess": {"allowedReadPaths": ["/b"]}}""");

        var access = result.Document["fileAccess"]!.AsObject();
        access["allowedReadPaths"]!.AsArray()[0]!.GetValue<string>().ShouldBe("/b");
        access.ContainsKey("deniedPaths").ShouldBeFalse("replace-as-unit means the child's block stands alone; a half-inherited security block is incoherent");
    }

    [Fact]
    public void KeyedMerge_ParentOnlyKeysSurvive_AndChildKeysWin()
    {
        var engine = Engine(("extensions", ConfigInheritancePolicy.KeyedMerge));

        var result = Overlay(
            engine,
            """{"extensions": {"telegram": {"enabled": true}, "slack": {"enabled": true}}}""",
            """{"extensions": {"telegram": {"enabled": false}}}""");

        var ext = result.Document["extensions"]!.AsObject();
        ext["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeFalse("the child addressed this key");
        ext["slack"]!["enabled"]!.GetValue<bool>().ShouldBeTrue("a key the child never mentioned must survive");
    }

    [Fact]
    public void LocalOnly_ParentValueIsNotInherited()
    {
        // An operator who sets displayName at the shared defaults layer has made a mistake. Honouring it
        // would give every agent the same identity - silently, and uniformly.
        var engine = Engine(("displayName", ConfigInheritancePolicy.LocalOnly));

        var result = Overlay(engine, """{"displayName": "Shared Default Name"}""", """{"model": "gpt-5"}""");

        result.Document.ContainsKey("displayName").ShouldBeFalse("a local-only property must never be supplied by a lower layer");
    }

    [Fact]
    public void LocalOnly_OwnLayerValueIsKept()
    {
        var engine = Engine(("displayName", ConfigInheritancePolicy.LocalOnly));

        var result = Overlay(engine, """{"displayName": "Shared"}""", """{"displayName": "Farnsworth"}""");

        result.Document["displayName"]!.GetValue<string>().ShouldBe("Farnsworth");
    }

    [Fact]
    public void RuntimeOnly_ParentValueIsNotInherited()
    {
        var engine = Engine(("kind", ConfigInheritancePolicy.RuntimeOnly));

        var result = Overlay(engine, """{"kind": "builtin"}""", """{"model": "gpt-5"}""");

        result.Document.ContainsKey("kind").ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // AC2 - absent vs explicit null
    // -------------------------------------------------------------------------

    [Fact]
    public void ExplicitNull_SuppressesInheritedValue()
    {
        // The tri-state distinction. A relational NULL column cannot express this, which is why the
        // engine reads the raw document rather than a bound POCO (#2646, #2766).
        var engine = Engine(("memory", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(engine, """{"memory": {"enabled": true}}""", """{"memory": null}""");

        result.Document.ContainsKey("memory").ShouldBeTrue("the key is present...");
        result.Document["memory"].ShouldBeNull("...with an explicit null, which suppresses rather than inherits");
    }

    [Fact]
    public void AbsentKey_InheritsRatherThanSuppressing()
    {
        var engine = Engine(("memory", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(engine, """{"memory": {"enabled": true}}""", """{"model": "gpt-5"}""");

        result.Document["memory"]!["enabled"]!.GetValue<bool>().ShouldBeTrue("absence means inherit - the opposite of explicit null");
    }

    [Fact]
    public void ExplicitNull_AndAbsent_ProduceDifferentResults()
    {
        // Guards the collapse directly: if these two ever agree, the tri-state has been lost.
        var engine = Engine(("memory", ConfigInheritancePolicy.DeepMerge));

        var suppressed = Overlay(engine, """{"memory": {"enabled": true}}""", """{"memory": null}""");
        var inherited = Overlay(engine, """{"memory": {"enabled": true}}""", """{"model": "gpt-5"}""");

        suppressed.Document.ToJsonString().ShouldNotBe(
            inherited.Document.ToJsonString(),
            "collapsing explicit-null into absent hands a world default to an agent that deliberately declined it");
    }

    // -------------------------------------------------------------------------
    // AC3 - falsy explicit values must override
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("false", "true")]
    [InlineData("0", "42")]
    [InlineData("\"\"", "\"set\"")]
    [InlineData("[]", "[\"a\"]")]
    [InlineData("{}", "{\"a\": 1}")]
    public void ExplicitFalsyValue_OverridesInheritedValue(string childValue, string parentValue)
    {
        // Every one of these is a legitimate operator intent that a naive `child ?? parent` or
        // `if (value != default)` merge silently discards.
        var engine = Engine(("setting", ConfigInheritancePolicy.ScalarOverride));

        var result = Overlay(engine, $$"""{"setting": {{parentValue}}}""", $$"""{"setting": {{childValue}}}""");

        result.Document["setting"]!.ToJsonString().ShouldBe(
            JsonNode.Parse(childValue)!.ToJsonString(),
            "an explicit falsy value is a decision, not an absence");
    }

    // -------------------------------------------------------------------------
    // AC4 - list order and duplicates
    // -------------------------------------------------------------------------

    [Fact]
    public void List_PreservesOrderAndDuplicates()
    {
        var engine = Engine(("items", ConfigInheritancePolicy.ReplaceAsUnit));

        var result = Overlay(engine, """{"items": []}""", """{"items": ["b", "a", "b"]}""");

        result.Document["items"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray()
            .ShouldBe(new[] { "b", "a", "b" }, "order is significant and duplicates are preserved verbatim");
    }

    // -------------------------------------------------------------------------
    // AC5 - dictionaries select behaviour by policy
    // -------------------------------------------------------------------------

    [Fact]
    public void Dictionary_ReplacementVersusKeyedMerge_IsSelectedByPolicy()
    {
        const string parent = """{"map": {"a": 1, "b": 2}}""";
        const string child = """{"map": {"a": 9}}""";

        var replaced = Overlay(Engine(("map", ConfigInheritancePolicy.ReplaceAsUnit)), parent, child);
        var merged = Overlay(Engine(("map", ConfigInheritancePolicy.KeyedMerge)), parent, child);

        replaced.Document["map"]!.AsObject().ContainsKey("b").ShouldBeFalse("replacement drops parent-only keys");
        merged.Document["map"]!["b"]!.GetValue<int>().ShouldBe(2, "keyed merge preserves them");
        merged.Document["map"]!["a"]!.GetValue<int>().ShouldBe(9);
    }

    // -------------------------------------------------------------------------
    // AC7 - secrets inherit without materialising into the child
    // -------------------------------------------------------------------------

    [Fact]
    public void InheritedSecret_IsNotWrittenBackIntoTheChildLayer()
    {
        // If the engine mutated its input, an inherited API key would be copied into the agent's own
        // block and then persisted there by the next save - duplicating a secret into a wider blast
        // radius and freezing it against later rotation at the defaults layer.
        var engine = Engine(("apiKey", ConfigInheritancePolicy.ScalarOverride));

        var defaults = Doc("""{"apiKey": "super-secret"}""");
        var agent = Doc("""{"model": "gpt-5"}""");

        var result = engine.Overlay(new[]
        {
            new ConfigLayer("agents.defaults", defaults),
            new ConfigLayer("agents.test-agent", agent),
        });

        result.Document["apiKey"]!.GetValue<string>().ShouldBe("super-secret", "it is still inherited");
        agent.ContainsKey("apiKey").ShouldBeFalse("but never materialised into the child layer");
    }

    [Fact]
    public void Overlay_DoesNotMutateAnyInputLayer()
    {
        var engine = Engine(("heartbeat", ConfigInheritancePolicy.DeepMerge));

        var defaults = Doc("""{"heartbeat": {"enabled": true, "intervalMinutes": 30}}""");
        var agent = Doc("""{"heartbeat": {"enabled": false}}""");
        var defaultsBefore = defaults.ToJsonString();
        var agentBefore = agent.ToJsonString();

        var result = engine.Overlay(new[]
        {
            new ConfigLayer("agents.defaults", defaults),
            new ConfigLayer("agents.test-agent", agent),
        });

        defaults.ToJsonString().ShouldBe(defaultsBefore, "a shared defaults layer is read by every agent");
        agent.ToJsonString().ShouldBe(agentBefore);

        // And the result must not alias the inputs - mutating it must not reach back.
        result.Document["heartbeat"]!["intervalMinutes"] = 999;
        defaults["heartbeat"]!["intervalMinutes"]!.GetValue<int>().ShouldBe(30);
    }

    // -------------------------------------------------------------------------
    // AC8 - provenance
    // -------------------------------------------------------------------------

    [Fact]
    public void Provenance_ReportsTheSupplyingLayerPerProperty()
    {
        var engine = Engine(
            ("heartbeat", ConfigInheritancePolicy.DeepMerge),
            ("model", ConfigInheritancePolicy.ScalarOverride));

        var result = Overlay(
            engine,
            """{"heartbeat": {"enabled": true, "intervalMinutes": 30}, "model": "gpt-4"}""",
            """{"heartbeat": {"enabled": false}}""");

        result.GetProvenance("heartbeat.enabled")!.LayerName.ShouldBe("agents.test-agent");
        result.GetProvenance("heartbeat.intervalMinutes")!.LayerName.ShouldBe("agents.defaults");
        result.GetProvenance("model")!.LayerName.ShouldBe("agents.defaults");
    }

    [Fact]
    public void Provenance_ReportsPolicyAndState()
    {
        var engine = Engine(("memory", ConfigInheritancePolicy.ScalarOverride));

        var result = Overlay(engine, """{"memory": {"enabled": true}}""", """{"memory": null}""");

        var provenance = result.GetProvenance("memory")!;
        provenance.State.ShouldBe(ConfigValueState.ExplicitNull);
        provenance.Policy.ShouldBe(ConfigInheritancePolicy.ScalarOverride);
    }

    [Fact]
    public void Provenance_IsNullForAPropertyNoLayerSupplied()
    {
        var engine = Engine(("model", ConfigInheritancePolicy.ScalarOverride));

        var result = Overlay(engine, """{"model": "gpt-5"}""", """{}""");

        result.GetProvenance("nonexistent").ShouldBeNull();
    }

    // -------------------------------------------------------------------------
    // AC9 - validation reports path AND layer
    // -------------------------------------------------------------------------

    [Fact]
    public void Validation_NamesBothTheEffectivePathAndTheSupplyingLayer()
    {
        // Reporting only the path sends an operator hunting through their own agent block for a value
        // that came from the shared defaults layer.
        var engine = Engine(("heartbeat", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(
            engine,
            """{"heartbeat": {"intervalMinutes": -5}}""",
            """{"heartbeat": {"enabled": true}}""");

        var errors = ConfigLayerValidator.Validate(result, new Dictionary<string, Func<JsonNode?, string?>>
        {
            ["heartbeat.intervalMinutes"] = node =>
                node!.GetValue<int>() <= 0 ? "must be greater than zero" : null,
        });

        errors.Count.ShouldBe(1);
        errors[0].Path.ShouldBe("heartbeat.intervalMinutes");
        errors[0].LayerName.ShouldBe("agents.defaults", "that is the layer an operator must edit");
        errors[0].ToString().ShouldContain("agents.defaults");
        errors[0].ToString().ShouldContain("heartbeat.intervalMinutes");
    }

    [Fact]
    public void Validation_AttributesTheErrorToTheOverridingLayerWhenTheChildSuppliedIt()
    {
        var engine = Engine(("heartbeat", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(
            engine,
            """{"heartbeat": {"intervalMinutes": 30}}""",
            """{"heartbeat": {"intervalMinutes": -1}}""");

        var errors = ConfigLayerValidator.Validate(result, new Dictionary<string, Func<JsonNode?, string?>>
        {
            ["heartbeat.intervalMinutes"] = node =>
                node!.GetValue<int>() <= 0 ? "must be greater than zero" : null,
        });

        errors.Count.ShouldBe(1);
        errors[0].LayerName.ShouldBe("agents.test-agent");
    }

    [Fact]
    public void Validation_PassesWhenEveryRuleIsSatisfied()
    {
        var engine = Engine(("heartbeat", ConfigInheritancePolicy.DeepMerge));

        var result = Overlay(engine, """{"heartbeat": {"intervalMinutes": 30}}""", """{}""");

        var errors = ConfigLayerValidator.Validate(result, new Dictionary<string, Func<JsonNode?, string?>>
        {
            ["heartbeat.intervalMinutes"] = node =>
                node!.GetValue<int>() <= 0 ? "must be greater than zero" : null,
        });

        errors.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Layer stack semantics
    // -------------------------------------------------------------------------

    [Fact]
    public void ThreeLayers_ResolveInPrecedenceOrder()
    {
        var engine = Engine(("setting", ConfigInheritancePolicy.ScalarOverride));

        var result = engine.Overlay(new[]
        {
            new ConfigLayer("platform", Doc("""{"setting": "platform", "onlyPlatform": 1}""")),
            new ConfigLayer("agents.defaults", Doc("""{"setting": "defaults"}""")),
            new ConfigLayer("agents.test-agent", Doc("""{}""")),
        });

        result.Document["setting"]!.GetValue<string>().ShouldBe("defaults", "the highest layer that set it wins");
        result.Document["onlyPlatform"]!.GetValue<int>().ShouldBe(1);
        result.GetProvenance("setting")!.LayerName.ShouldBe("agents.defaults");
    }

    [Fact]
    public void NullLayerDocument_IsSkippedRatherThanTreatedAsEmpty()
    {
        var engine = Engine(("setting", ConfigInheritancePolicy.ScalarOverride));

        var result = engine.Overlay(new[]
        {
            new ConfigLayer("agents.defaults", Doc("""{"setting": "inherited"}""")),
            new ConfigLayer("agents.test-agent", null),
        });

        result.Document["setting"]!.GetValue<string>().ShouldBe("inherited");
    }

    [Fact]
    public void EmptyLayerStack_ProducesAnEmptyDocument()
    {
        var engine = Engine();

        var result = engine.Overlay(Array.Empty<ConfigLayer>());

        result.Document.Count.ShouldBe(0);
        result.Provenance.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Policy resolution
    // -------------------------------------------------------------------------

    [Fact]
    public void PolicyDeclaredOnAParent_GovernsItsUnclassifiedChildren()
    {
        // Without ancestor walking, an author would have to restate DeepMerge on every leaf of a
        // merged block - reintroducing exactly the hand-maintained list this replaces.
        var resolver = new MapConfigPolicyResolver(new Dictionary<string, ConfigInheritancePolicy>
        {
            ["heartbeat"] = ConfigInheritancePolicy.DeepMerge,
        });

        resolver.GetPolicy("heartbeat.quietHours.start").ShouldBe(ConfigInheritancePolicy.DeepMerge);
    }

    [Fact]
    public void MoreSpecificPolicy_WinsOverAnAncestor()
    {
        var resolver = new MapConfigPolicyResolver(new Dictionary<string, ConfigInheritancePolicy>
        {
            ["heartbeat"] = ConfigInheritancePolicy.DeepMerge,
            ["heartbeat.quietHours"] = ConfigInheritancePolicy.ReplaceAsUnit,
        });

        resolver.GetPolicy("heartbeat.quietHours").ShouldBe(ConfigInheritancePolicy.ReplaceAsUnit);
    }

    [Fact]
    public void UnclassifiedPath_ResolvesToNullRatherThanAnImpliedDefault()
    {
        // A silently-defaulted classification is indistinguishable from a considered one, which is the
        // drift #2137 documents. The resolver reports ignorance; the #2424 fitness test punishes it.
        var resolver = new MapConfigPolicyResolver(new Dictionary<string, ConfigInheritancePolicy>());

        resolver.GetPolicy("anything").ShouldBeNull();
    }
}
