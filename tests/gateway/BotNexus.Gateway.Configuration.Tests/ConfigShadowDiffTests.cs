using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Tests for the shadow-mode diff (#2766 AC3, AC4).
///
/// <para>
/// <b>What these are really pinning.</b> The diff is the deliverable, not the migration: a migration
/// that completes without throwing proves nothing, because it can drop a key, collapse a tri-state or
/// invent an entry and still finish happily. These tests assert the diff <em>notices</em> - and the
/// AC4 group specifically asserts it notices the one failure a relational store is most likely to
/// introduce, where an explicit null and an unset key become indistinguishable.
/// </para>
/// </summary>
public sealed class ConfigShadowDiffTests
{
    private static JsonObject Obj(string raw) => JsonNode.Parse(raw)!.AsObject();

    [Fact]
    public void IdenticalDocuments_ProduceACleanReport()
    {
        var doc = """{ "gateway": { "compaction": { "enabled": true, "threshold": 10 } } }""";

        var report = ConfigShadowDiff.Compare(Obj(doc), Obj(doc));

        report.IsClean.ShouldBeTrue();
        report.SourceKeyCount.ShouldBe(2);
        report.StoreKeyCount.ShouldBe(2);
    }

    /// <summary>
    /// AC3: a key the store lost is reported as missing, naming its path.
    /// </summary>
    [Fact]
    public void KeyPresentInSourceButNotStore_IsReportedAsMissingFromStore()
    {
        var report = ConfigShadowDiff.Compare(
            Obj("""{ "gateway": { "compaction": { "enabled": true, "threshold": 10 } } }"""),
            Obj("""{ "gateway": { "compaction": { "enabled": true } } }"""));

        report.IsClean.ShouldBeFalse();
        var diff = report.Differences.ShouldHaveSingleItem();
        diff.Path.ShouldBe("gateway.compaction.threshold");
        diff.Kind.ShouldBe(ConfigDiffKind.MissingFromStore);
        diff.Source.State.ShouldBe(ConfigValueState.Value);
        diff.Store.State.ShouldBe(ConfigValueState.Unknown);
    }

    /// <summary>AC3: a key the store invented is reported as extra.</summary>
    [Fact]
    public void KeyPresentInStoreButNotSource_IsReportedAsExtraInStore()
    {
        var report = ConfigShadowDiff.Compare(
            Obj("""{ "gateway": { "enabled": true } }"""),
            Obj("""{ "gateway": { "enabled": true, "invented": 1 } }"""));

        var diff = report.Differences.ShouldHaveSingleItem();
        diff.Path.ShouldBe("gateway.invented");
        diff.Kind.ShouldBe(ConfigDiffKind.ExtraInStore);
    }

    /// <summary>AC3: a changed value is reported with both sides, so the report is actionable.</summary>
    [Fact]
    public void KeyWithDifferentValue_IsReportedWithBothSides()
    {
        var report = ConfigShadowDiff.Compare(
            Obj("""{ "gateway": { "threshold": 10 } }"""),
            Obj("""{ "gateway": { "threshold": 99 } }"""));

        var diff = report.Differences.ShouldHaveSingleItem();
        diff.Kind.ShouldBe(ConfigDiffKind.ValueDiffers);
        diff.Source.Value.ShouldBe("10");
        diff.Store.Value.ShouldBe("99");
    }

    /// <summary>
    /// AC4, and the single most important assertion in this file: an explicitly null key must not
    /// compare equal to an unset one.
    ///
    /// <para>
    /// Both sides have "no value". A diff comparing values alone would find them equal and report
    /// clean - which is precisely how a relational store that maps both onto <c>NULL</c> would pass its
    /// own verification while silently handing every agent back a world default it had declined.
    /// The comparison therefore checks state before value.
    /// </para>
    /// </summary>
    [Fact]
    public void ExplicitNullInSource_DoesNotCompareEqualToUnsetInStore()
    {
        // Both documents keep a sibling key so `agents.alpha` stays a branch on both sides. Without it
        // the store's `alpha` collapses to an empty-object LEAF, which is a second, unrelated
        // difference and obscures the tri-state finding this test exists to pin.
        var report = ConfigShadowDiff.Compare(
            Obj("""{ "agents": { "alpha": { "model": "x", "memory": null } } }"""),
            Obj("""{ "agents": { "alpha": { "model": "x" } } }"""));

        report.IsClean.ShouldBeFalse(
            "an explicit null and an absent key are different configuration states: null suppresses " +
            "the inherited value, absence inherits it. A store that collapses them must not diff clean.");

        var diff = report.Differences.ShouldHaveSingleItem();
        diff.Path.ShouldBe("agents.alpha.memory");
        diff.Source.State.ShouldBe(ConfigValueState.ExplicitNull);
        diff.Store.State.ShouldBe(ConfigValueState.Unknown);
    }

    /// <summary>AC4: the four states are distinguished, not merely null-vs-non-null.</summary>
    [Fact]
    public void FourStates_AreDistinguished()
    {
        var flattened = ConfigDocumentFlattener.Flatten(
            Obj("""{ "a": null, "b": 1, "c": { } }"""));

        flattened["a"].State.ShouldBe(ConfigValueState.ExplicitNull);
        flattened["b"].State.ShouldBe(ConfigValueState.Value);
        // An empty object is a leaf with a value. Treating it as a branch would make it vanish from the
        // flattened form, so a store that dropped it entirely would diff clean.
        flattened["c"].State.ShouldBe(ConfigValueState.Value);
        flattened.ContainsKey("d").ShouldBeFalse("an absent key must not appear as an entry at all");
    }

    /// <summary>
    /// AC4, and the assertion that makes state comparison load-bearing rather than redundant.
    ///
    /// <para>
    /// A relational store reports its own states directly rather than reconstructing a JSON document,
    /// and it is the only side able to say <see cref="ConfigValueState.Unset"/> - JSON cannot express
    /// "present and unset", so the flattener never emits it. Here the store has mapped an explicit null
    /// onto <c>Unset</c>, which is exactly what a nullable column does: both sides carry a null
    /// <see cref="ConfigEntry.Value"/>, so a comparison on values alone finds them EQUAL and reports
    /// clean. Only comparing state catches it.
    /// </para>
    ///
    /// <para>
    /// This is the failure that would silently hand every agent back a world default it had explicitly
    /// declined, with no exception and no log line.
    /// </para>
    /// </summary>
    [Fact]
    public void StoreReportingUnsetWhereSourceIsExplicitNull_IsADifference()
    {
        var source = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["agents.alpha.memory"] = new("agents.alpha.memory", ConfigValueState.ExplicitNull, Value: null),
        };
        var store = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["agents.alpha.memory"] = new("agents.alpha.memory", ConfigValueState.Unset, Value: null),
        };

        var report = ConfigShadowDiff.CompareEntries(source, store);

        report.IsClean.ShouldBeFalse(
            "both sides carry a null value, so a value-only comparison finds them equal. Explicit-null " +
            "suppresses an inherited value and unset inherits it - a store collapsing them onto one " +
            "relational NULL must not diff clean.");

        var diff = report.Differences.ShouldHaveSingleItem();
        diff.Kind.ShouldBe(ConfigDiffKind.ValueDiffers);
        diff.Source.State.ShouldBe(ConfigValueState.ExplicitNull);
        diff.Store.State.ShouldBe(ConfigValueState.Unset);
    }

    /// <summary>
    /// The negative control for the test above: identical states with identical values compare clean,
    /// so the state check is discriminating rather than simply always reporting a difference.
    /// </summary>
    [Fact]
    public void StoreReportingTheSameStateAndValue_ComparesClean()
    {
        var entries = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["agents.alpha.memory"] = new("agents.alpha.memory", ConfigValueState.ExplicitNull, Value: null),
        };

        ConfigShadowDiff.CompareEntries(entries, entries).IsClean.ShouldBeTrue();
    }

    /// <summary>
    /// Arrays are leaves: configuration replaces them wholesale, so their identity is the whole value.
    /// </summary>
    [Fact]
    public void Array_IsComparedWholesale_NotElementwise()
    {
        var report = ConfigShadowDiff.Compare(
            Obj("""{ "tools": ["read", "write"] }"""),
            Obj("""{ "tools": ["read"] }"""));

        var diff = report.Differences.ShouldHaveSingleItem();
        diff.Path.ShouldBe("tools");
        diff.Kind.ShouldBe(ConfigDiffKind.ValueDiffers);
    }

    /// <summary>
    /// The report states its input size, so a clean result can be distinguished from a comparison that
    /// never saw anything. A sweep reporting zero over an empty input is indistinguishable from a
    /// broken sweep unless the input count is stated.
    /// </summary>
    [Fact]
    public void Report_StatesItsInputCounts()
    {
        var report = ConfigShadowDiff.Compare(Obj("{ }"), Obj("{ }"));

        report.IsClean.ShouldBeTrue();
        report.SourceKeyCount.ShouldBe(0);
        report.Summary.ShouldContain("0 source keys");
    }
}
