namespace BotNexus.SourceGenerators.Tests;

using System;
using System.Linq;
using Shouldly;

/// <summary>
/// Parser contract for <c>feature-flags.json</c> (#2769).
/// <para>
/// The sad paths carry most of the weight here. The whole value of generating the inventory is
/// that a bad declaration becomes a build error instead of a flag that silently evaluates to its
/// default, so "malformed input throws" is the feature, not defensive housekeeping.
/// </para>
/// </summary>
public class FeatureFlagJsonParserTests
{
    private const string ValidFlag = """
        {
          "flags": [
            {
              "featureName": "GatewayDevOriginEnforcement",
              "description": "Dev-mode browser Origin guard.",
              "owner": "sytone",
              "dateAdded": "2026-07-01",
              "defaultState": false
            }
          ]
        }
        """;

    [Fact]
    public void ParseJson_ReadsEveryDeclaredProperty()
    {
        var flags = FeatureFlagJsonParser.ParseJson(ValidFlag);

        var flag = flags.ShouldHaveSingleItem();
        flag.FeatureName.ShouldBe("GatewayDevOriginEnforcement");
        flag.Description.ShouldBe("Dev-mode browser Origin guard.");
        flag.Owner.ShouldBe("sytone");
        flag.DateAdded.ShouldBe(new DateTime(2026, 7, 1));
        flag.DefaultState.ShouldBeFalse();
        flag.DateRetired.ShouldBeNull();
        flag.IgnoreFlagAge.ShouldBeFalse();
    }

    [Fact]
    public void ParseJson_ReadsOptionalRetirementAndAgeOptOut()
    {
        var json = """
            {
              "flags": [
                {
                  "featureName": "OldFlag",
                  "description": "Retired.",
                  "owner": "sytone",
                  "dateAdded": "2025-01-01",
                  "defaultState": true,
                  "dateRetired": "2026-02-03",
                  "ignoreFlagAge": true
                }
              ]
            }
            """;

        var flag = FeatureFlagJsonParser.ParseJson(json).ShouldHaveSingleItem();

        flag.DefaultState.ShouldBeTrue();
        flag.DateRetired.ShouldBe(new DateTime(2026, 2, 3));
        flag.IgnoreFlagAge.ShouldBeTrue();
    }

    [Fact]
    public void ParseJson_TreatsAnExplicitNullDateRetiredAsLive()
    {
        // The canonical feature-flags.json spells a live flag as "dateRetired": null rather than
        // omitting the key. A parser that only handled the omitted form would mark every flag in
        // the real file retired - and retirement emits [Obsolete] at every call site.
        var json = ValidFlag.Replace(
            "\"defaultState\": false",
            "\"defaultState\": false,\n      \"dateRetired\": null");

        FeatureFlagJsonParser.ParseJson(json).ShouldHaveSingleItem().DateRetired.ShouldBeNull();
    }

    // ── Sad paths: every one of these must be a build error, never an empty inventory ──────

    [Fact]
    public void ParseJson_MalformedJson_Throws()
    {
        var exception = Should.Throw<ArgumentException>(
            () => FeatureFlagJsonParser.ParseJson("{ \"flags\": [ { \"featureName\": "));

        exception.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void ParseJson_EmptyContent_Throws()
    {
        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson("   "))
            .Message.ShouldContain("empty");
    }

    [Fact]
    public void ParseJson_MissingFlagsArray_Throws()
    {
        // A file with no 'flags' property is far more likely a misconfigured AdditionalFiles entry
        // pointing at some other JSON than a deliberate statement that no flags exist.
        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson("{ \"features\": [] }"))
            .Message.ShouldContain("'flags' array");
    }

    [Fact]
    public void ParseJson_DuplicateFeatureName_ThrowsAndNamesTheFlag()
    {
        var json = """
            {
              "flags": [
                { "featureName": "Dup", "description": "a", "owner": "sytone", "dateAdded": "2026-01-01", "defaultState": false },
                { "featureName": "Dup", "description": "b", "owner": "sytone", "dateAdded": "2026-01-02", "defaultState": true }
              ]
            }
            """;

        var exception = Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json));

        exception.Message.ShouldContain("Duplicate");
        exception.Message.ShouldContain("Dup");
    }

    [Fact]
    public void ParseJson_DuplicateFeatureNameDifferingOnlyByCase_Throws()
    {
        // Case-insensitive, because the generated members would collide on case anyway; catching
        // it here names the flag instead of emitting a duplicate-member error in generated source
        // the author cannot edit.
        var json = """
            {
              "flags": [
                { "featureName": "Flag", "description": "a", "owner": "sytone", "dateAdded": "2026-01-01", "defaultState": false },
                { "featureName": "FLAG", "description": "b", "owner": "sytone", "dateAdded": "2026-01-02", "defaultState": false }
              ]
            }
            """;

        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json))
            .Message.ShouldContain("Duplicate");
    }

    [Fact]
    public void ParseJson_MissingOwner_ThrowsAndNamesTheFlag()
    {
        var json = """
            {
              "flags": [
                { "featureName": "NoOwner", "description": "a", "dateAdded": "2026-01-01", "defaultState": false }
              ]
            }
            """;

        var exception = Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json));

        exception.Message.ShouldContain("owner");
        exception.Message.ShouldContain("NoOwner");
    }

    [Fact]
    public void ParseJson_MissingDescription_ThrowsAndNamesTheFlag()
    {
        var json = """
            {
              "flags": [
                { "featureName": "NoDescription", "owner": "sytone", "dateAdded": "2026-01-01", "defaultState": false }
              ]
            }
            """;

        var exception = Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json));

        exception.Message.ShouldContain("description");
        exception.Message.ShouldContain("NoDescription");
    }

    [Fact]
    public void ParseJson_EmptyOwner_Throws()
    {
        // Present-but-blank is the same defect as absent: nobody to ask about the flag.
        var json = ValidFlag.Replace("\"owner\": \"sytone\"", "\"owner\": \"  \"");

        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json))
            .Message.ShouldContain("owner");
    }

    [Fact]
    public void ParseJson_MissingDefaultState_Throws()
    {
        var json = """
            {
              "flags": [
                { "featureName": "NoDefault", "description": "a", "owner": "sytone", "dateAdded": "2026-01-01" }
              ]
            }
            """;

        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json))
            .Message.ShouldContain("defaultState");
    }

    [Fact]
    public void ParseJson_NonBooleanDefaultState_Throws()
    {
        // "false" as a string is the classic hand-edit mistake, and silently coercing it would
        // make the file say one thing and the build do another.
        var json = ValidFlag.Replace("\"defaultState\": false", "\"defaultState\": \"false\"");

        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json))
            .Message.ShouldContain("boolean");
    }

    [Fact]
    public void ParseJson_BadDateFormat_ThrowsAndNamesTheProperty()
    {
        var json = ValidFlag.Replace("\"dateAdded\": \"2026-07-01\"", "\"dateAdded\": \"01/07/2026\"");

        var exception = Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json));

        exception.Message.ShouldContain("dateAdded");
        exception.Message.ShouldContain("yyyy-MM-dd");
    }

    [Fact]
    public void ParseJson_BadRetirementDateFormat_Throws()
    {
        var json = ValidFlag.Replace(
            "\"defaultState\": false",
            "\"defaultState\": false,\n      \"dateRetired\": \"soon\"");

        Should.Throw<ArgumentException>(() => FeatureFlagJsonParser.ParseJson(json))
            .Message.ShouldContain("dateRetired");
    }

    [Fact]
    public void ParseJson_EmptyFlagsArray_IsAcceptedRatherThanRejected()
    {
        // An explicitly empty array is a legible statement ("no flags declared"); unlike a missing
        // 'flags' property it cannot be a misconfigured AdditionalFiles pointing at another file.
        FeatureFlagJsonParser.ParseJson("{ \"flags\": [] }").ShouldBeEmpty();
    }

    [Fact]
    public void ParseJson_IgnoresUnknownProperties()
    {
        // feature-flags.json carries $comment and $-prefixed rationale keys. Rejecting unknown
        // properties would make documenting a decision in the file itself a build break.
        var json = ValidFlag.Replace(
            "\"defaultState\": false",
            "\"defaultState\": false,\n      \"$why\": \"rationale kept next to the declaration\"");

        FeatureFlagJsonParser.ParseJson(json).ShouldHaveSingleItem()
            .FeatureName.ShouldBe("GatewayDevOriginEnforcement");
    }

    [Fact]
    public void ParseJson_PreservesEveryDeclaredFlag()
    {
        var json = """
            {
              "flags": [
                { "featureName": "Bravo", "description": "b", "owner": "sytone", "dateAdded": "2026-01-02", "defaultState": false },
                { "featureName": "Alpha", "description": "a", "owner": "sytone", "dateAdded": "2026-01-01", "defaultState": true }
              ]
            }
            """;

        FeatureFlagJsonParser.ParseJson(json)
            .Select(flag => flag.FeatureName)
            .ShouldBe(["Bravo", "Alpha"]);
    }
}
