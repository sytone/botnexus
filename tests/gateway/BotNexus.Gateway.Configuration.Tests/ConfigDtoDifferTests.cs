using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Pins the DTO-diff write contract (#3532): a write names the keys it changes, and everything else
/// under the document is provably untouched.
/// </summary>
/// <remarks>
/// These tests exist because the whole-document writer they replace could not express the difference
/// between "unchanged" and "not supplied". <see cref="Write_ToOneChannel_DoesNotTouchSiblingSecrets"/>
/// is a direct reconstruction of #2816, where that ambiguity destroyed two bot tokens.
/// </remarks>
public sealed class ConfigDtoDifferTests
{
    private sealed class ChannelDto
    {
        public bool Enabled { get; set; }
    }

    private sealed class AgentDto
    {
        public string? Model { get; set; }
        public string? Provider { get; set; }
    }

    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Write_ToOneChannel_DoesNotTouchSiblingSecrets()
    {
        // The #2816 document: a channel section carrying credentials the caller's DTO does not model.
        var current = Doc("""
            {
              "channels": {
                "telegram": { "enabled": false, "botToken": "secret-telegram" },
                "teams":    { "enabled": true,  "serviceBus": "Endpoint=sb://real" }
              }
            }
            """);

        var change = ConfigDtoDiffer.Diff(
            current,
            new ChannelDto { Enabled = true },
            "channels.telegram",
            ConfigDiffOptions.Additive);

        change.Upserts.Select(u => u.Path).ShouldBe(["channels.telegram.enabled"]);

        // The whole point: nothing outside the named subtree appears in the write at all.
        change.Upserts.ShouldAllBe(u => !u.Path.StartsWith("channels.teams"));
        change.Removals.ShouldBeEmpty();
    }

    [Fact]
    public void Diff_PrefixIsSegmentAware_SoSimilarlyNamedSiblingsAreNotInScope()
    {
        // 'agents.novaBackup' must not be treated as inside 'agents.nova'. A naive StartsWith would
        // scope it in and then remove every one of its keys as "absent from the DTO".
        var current = Doc("""
            {
              "agents": {
                "nova":       { "model": "old",     "provider": "anthropic" },
                "novaBackup": { "model": "backup",  "provider": "openai" }
              }
            }
            """);

        var change = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = "new", Provider = "anthropic" },
            "agents.nova");

        change.Upserts.Select(u => u.Path).ShouldBe(["agents.nova.model"]);
        change.Removals.ShouldBeEmpty();
    }

    [Fact]
    public void Diff_UnchangedDto_ProducesNoWriteAtAll()
    {
        var current = Doc("""{ "agents": { "nova": { "model": "sonnet", "provider": "anthropic" } } }""");

        var change = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = "sonnet", Provider = "anthropic" },
            "agents.nova");

        change.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_KeyAbsentFromDto_IsExpressedAsAnExplicitRemoval()
    {
        // Deletion in the eight keyed dictionaries is visible only as absence, so it must be stated.
        var current = Doc("""
            { "agents": { "nova": { "model": "sonnet", "provider": "anthropic", "legacyField": 7 } } }
            """);

        var change = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = "sonnet", Provider = "anthropic" },
            "agents.nova");

        change.Removals.ShouldBe(["agents.nova.legacyField"]);
        change.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public void Diff_ExplicitNull_IsAWriteAndNotARemoval()
    {
        // Unset means "inherit"; explicit null means "suppress". Collapsing them hands a world default
        // back to an agent that deliberately declined it.
        var current = Doc("""{ "agents": { "nova": { "model": "sonnet", "provider": "anthropic" } } }""");

        var change = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = null, Provider = "anthropic" },
            "agents.nova");

        var entry = change.Upserts.ShouldHaveSingleItem();
        entry.Path.ShouldBe("agents.nova.model");
        entry.State.ShouldBe(ConfigValueState.ExplicitNull);
        change.Removals.ShouldBeEmpty();
    }

    [Fact]
    public void Diff_DistinguishesUnsetFromExplicitNull_WhenComparingAgainstStored()
    {
        // Stored value is an explicit null; the DTO also carries null. That is genuinely unchanged.
        var current = Doc("""{ "agents": { "nova": { "model": null, "provider": "anthropic" } } }""");

        var unchanged = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = null, Provider = "anthropic" },
            "agents.nova");

        unchanged.IsEmpty.ShouldBeTrue();

        // ...but moving from explicit null to a value must be a write.
        var changed = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = "sonnet", Provider = "anthropic" },
            "agents.nova");

        changed.Upserts.ShouldHaveSingleItem().State.ShouldBe(ConfigValueState.Value);
    }

    [Fact]
    public void Diff_NewSubtree_WritesEveryKeyAndRemovesNothing()
    {
        var current = Doc("""{ "agents": { "nova": { "model": "sonnet" } } }""");

        var change = ConfigDtoDiffer.Diff(
            current,
            new AgentDto { Model = "opus", Provider = "anthropic" },
            "agents.farnsworth");

        change.Upserts.Select(u => u.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["agents.farnsworth.model", "agents.farnsworth.provider"]);
        change.Removals.ShouldBeEmpty();
    }

    [Fact]
    public void Describe_ReportsCountsButNeverValues()
    {
        // Diagnostics are exactly where a connection string leaks into a log file (#3469).
        var current = Doc("""{ "channels": { "telegram": { "botToken": "super-secret-value" } } }""");

        var change = ConfigDtoDiffer.Diff(
            current,
            new ChannelDto { Enabled = true },
            "channels.telegram");

        var described = change.Describe();

        described.ShouldNotContain("super-secret-value");
        described.ShouldContain("channels.telegram");
        described.ShouldContain("1 removed");
    }

    [Fact]
    public void Diff_AgainstEmptyDocument_IsAPureInsert()
    {
        var change = ConfigDtoDiffer.Diff(
            current: null,
            new AgentDto { Model = "opus", Provider = "anthropic" },
            "agents.nova");

        change.Upserts.Count.ShouldBe(2);
        change.Removals.ShouldBeEmpty();
    }

    [Fact]
    public void Diff_ComparesStateNotJustValue_SoUnsetAndExplicitNullStayDistinct()
    {
        // Non-vacuity note for a surviving mutant, recorded rather than hidden. Deleting the
        // `before.State == entry.State` clause left every other test green, because the flattener only
        // ever emits ExplicitNull (value null) or Value (value non-null) - across THAT pair, comparing
        // value alone happens to agree with comparing state. It is an equivalent mutant for documents
        // alone. The state clause is load-bearing where an entry arrives from the STORE, which also
        // carries Unset with a null value. This pins the discriminating pair directly instead of
        // claiming a kill that did not happen.
        var explicitNull = new ConfigEntry("agents.nova.model", ConfigValueState.ExplicitNull, null);
        var unset = new ConfigEntry("agents.nova.model", ConfigValueState.Unset, null);

        // Identical values, opposite meaning: suppress the inherited value versus inherit it.
        explicitNull.Value.ShouldBe(unset.Value);
        explicitNull.State.ShouldNotBe(unset.State);
        explicitNull.ShouldNotBe(unset);
    }

    [Fact]
    public void Diff_EmptyObjectValue_IsALeafNotAVanishedBranch()
    {
        // The flattener treats {} as a value. If the differ dropped it, a store that lost the key would
        // diff clean - so an empty object must compare as an unchanged leaf.
        var current = Doc("""{ "agents": { "nova": { "tools": {} } } }""");

        var change = ConfigDtoDiffer.Diff(current, new { Tools = new { } }, "agents.nova");

        change.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_ProjectsCamelCaseKeys_MatchingEveryOtherPersistencePath()
    {
        // Regression guard. The first version of this differ projected PascalCase, so it matched nothing
        // in the stored document: every property looked new and every stored key looked removed, turning
        // a one-field edit into "delete the subtree, re-add it under keys nothing reads".
        var change = ConfigDtoDiffer.Diff(
            current: null,
            new AgentDto { Model = "opus", Provider = "anthropic" },
            "agents.nova");

        change.Upserts.Select(u => u.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["agents.nova.model", "agents.nova.provider"]);
        change.Upserts.ShouldAllBe(u => !u.Path.Contains("Model", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_RootScopedScalarDto_IsRefused()
    {
        // A scalar cannot describe the whole document; accepting it would replace everything with a leaf.
        Should.Throw<ArgumentException>(() => ConfigDtoDiffer.Diff(new JsonObject(), 42, string.Empty));
    }
}
