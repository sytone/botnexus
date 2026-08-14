using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// The canonical-path read/write surface every consumer outside this project uses (#2887).
/// </summary>
/// <remarks>
/// <para>
/// The clause under test throughout is AC3: <b>a path the resolver does not recognise yields an
/// explicit failure, never <see langword="null"/></b>. That distinction is the whole point. A null
/// return is indistinguishable from "not configured", which is precisely how #2764 produced one
/// check that could never fire and a sibling that reported a healthy platform as broken on every
/// run. Both read exactly like success.
/// </para>
/// <para>
/// Behaviour parity with the raw primitives is asserted alongside: field unset, field explicitly
/// null, field set, and a nested object partially overridden must each resolve to the same value
/// they did before the migration.
/// </para>
/// </remarks>
public sealed class ConfigDocumentTests
{
    private const string ListenUrlPath = "gateway.listenUrl";
    private const string SummarizationModelPath = "gateway.compaction.summarizationModel";

    // ── AC3: unrecognised paths fail explicitly ────────────────────────────────────────

    [Theory]
    // The #2764 defect itself: a root-level compaction block binds to nothing.
    [InlineData("compaction.summarizationModel")]
    // A misspelled property of a real section.
    [InlineData("gateway.listenUrlz")]
    // A section that does not exist at all.
    [InlineData("notASection.value")]
    // A plausible-looking nesting that the typed graph does not model.
    [InlineData("gateway.compaction.summarisationModel")]
    public void TrySet_UnrecognisedPath_FailsWithAnExplicitError(string path)
    {
        var config = ConfigDocument.Empty();

        config.TrySet(path, "value", out var error).ShouldBeFalse(
            $"'{path}' is not bound by PlatformConfig; writing it would produce an inert config block");
        error.ShouldNotBeNullOrWhiteSpace();
        error.ShouldContain(path);

        config.RootKeys.ShouldBeEmpty("a rejected write must not touch the document");
    }

    [Fact]
    public void Read_UnrecognisedPath_ThrowsRatherThanReturningNull()
    {
        var config = ConfigDocument.Empty();

        // The sad path that matters: returning null here would be indistinguishable from
        // "configured but unset", which is the #2764 failure mode exactly.
        Should.Throw<InvalidOperationException>(() => config.TryGetString("compaction.summarizationModel", out _));
        Should.Throw<InvalidOperationException>(() => config.GetBool("gateway.notAThing"));
        Should.Throw<InvalidOperationException>(() => config.HasObject("nope.nope"));
    }

    [Fact]
    public void TryRemove_UnrecognisedPath_FailsExplicitly()
    {
        var config = ConfigDocument.Parse("""{ "gateway": { "listenUrl": "http://localhost:5005" } }""");

        config.TryRemove("gateway.notAThing", out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();

        // The document is untouched by the refusal.
        config.TryGetString(ListenUrlPath, out var url).ShouldBeTrue();
        url.ShouldBe("http://localhost:5005");
    }

    [Fact]
    public void MalformedPath_IsRejectedBeforeAnythingIsWritten()
    {
        var config = ConfigDocument.Empty();

        config.TrySet("agents.my]agent.model", "gpt-4", out var error).ShouldBeFalse();
        error.ShouldContain("unmatched ']'");
        config.RootKeys.ShouldBeEmpty();
    }

    [Fact]
    public void EmptyPath_IsRejected()
    {
        var config = ConfigDocument.Empty();

        config.TrySet("  ", "x", out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    // ── AC3 companion: recognised paths still work, including free-form regions ────────

    [Theory]
    [InlineData(ListenUrlPath)]
    [InlineData(SummarizationModelPath)]
    [InlineData("gateway.defaultAgentId")]
    [InlineData("agents.assistant.model")]
    [InlineData("providers.github-copilot.apiKey")]
    [InlineData("gateway.locations.notes.path")]
    [InlineData("gateway.cors.allowedOrigins[0]")]
    [InlineData("cron.jobs.nightly.schedule")]
    // FeatureManagement is a Dictionary<string, JsonElement>: any flag name is addressable.
    [InlineData("FeatureManagement.GatewayDevOriginEnforcement")]
    // Free-form regions: extension settings and agent metadata are deliberately unmodelled, so
    // any key beneath them is legitimate. Refusing these would break real configuration.
    [InlineData("gateway.extensions.defaults.botnexus-skills.enabled")]
    [InlineData("agents.assistant.extensions.some-ext.someSetting")]
    public void RecognisedPaths_AreAccepted(string path)
    {
        var config = ConfigDocument.Empty();

        config.TrySet(path, "value", out var error).ShouldBeTrue(error);
        error.ShouldBeEmpty();
    }

    // ── Behaviour parity: unset / explicit null / set / partial override ───────────────

    [Fact]
    public void TryGetString_FieldUnset_ReportsAbsentWithoutFailing()
    {
        var config = ConfigDocument.Parse("""{ "gateway": { } }""");

        config.TryGetString(ListenUrlPath, out var value).ShouldBeFalse();
        value.ShouldBeNull();
    }

    [Fact]
    public void TryGetString_FieldExplicitlyNull_ReportsAbsentButExists()
    {
        var config = ConfigDocument.Parse("""{ "gateway": { "listenUrl": null } }""");

        config.TryGetString(ListenUrlPath, out var value).ShouldBeFalse();
        value.ShouldBeNull();

        // The explicit null IS on disk, and the surface can say so - a distinction a bare
        // null-returning read cannot express.
        config.Exists(ListenUrlPath).ShouldBeTrue();
    }

    [Fact]
    public void TryGetString_FieldSet_ReturnsThePersistedValue()
    {
        var config = ConfigDocument.Parse("""{ "gateway": { "listenUrl": "http://0.0.0.0:5005" } }""");

        config.TryGetString(ListenUrlPath, out var value).ShouldBeTrue();
        value.ShouldBe("http://0.0.0.0:5005");
    }

    [Fact]
    public void TryPatch_NestedObjectPartiallyOverridden_LeavesUnsuppliedFieldsAlone()
    {
        var config = ConfigDocument.Parse("""
            { "providers": { "p": { "apiKey": "keep-me", "defaultModel": "old", "unknownField": "canary" } } }
            """);

        config.TryPatchEntry("providers", "p", new ConfigValueMap().Set("defaultModel", "new"), out var error)
            .ShouldBeTrue(error);

        config.TryGetString("providers.p.apiKey", out var apiKey).ShouldBeTrue();
        apiKey.ShouldBe("keep-me");
        config.TryGetString("providers.p.defaultModel", out var model).ShouldBeTrue();
        model.ShouldBe("new");
        // A field the CLI does not model must survive verbatim (#2057).
        config.ToJsonString().ShouldContain("canary");
    }

    [Fact]
    public void TrySet_MatchesExistingKeyCasing_RatherThanCreatingASibling()
    {
        var config = ConfigDocument.Parse("""{ "Gateway": { "ListenUrl": "old" } }""");

        config.TrySet(ListenUrlPath, "new", out var error).ShouldBeTrue(error);

        config.RootKeys.ShouldBe(["Gateway"]);
        config.TryGetString(ListenUrlPath, out var value).ShouldBeTrue();
        value.ShouldBe("new");
    }

    [Fact]
    public void EntryKeys_AreTreatedAsLiteralsNotPaths()
    {
        var config = ConfigDocument.Empty();

        // A location name containing a dot must address ONE entry, not be re-split into segments.
        config.TrySetEntry("gateway.locations", "my.location", new ConfigValueMap().Set("type", "filesystem"), out var error)
            .ShouldBeTrue(error);

        config.GetEntryKeys("gateway.locations").ShouldBe(["my.location"]);
    }

    [Fact]
    public void FindEntryKey_ReportsTheOnDiskCasing()
    {
        var config = ConfigDocument.Parse("""{ "providers": { "GitHub-Copilot": { "enabled": true } } }""");

        config.FindEntryKey("providers", "github-copilot").ShouldBe("GitHub-Copilot");
        config.FindEntryKey("providers", "absent").ShouldBeNull();
    }

    [Fact]
    public void TryRemoveEntry_AbsentEntry_IsASuccessfulNoOp()
    {
        var config = ConfigDocument.Parse("""{ "providers": { "p": { "enabled": true } } }""");

        config.TryRemoveEntry("providers", "not-there", out var error).ShouldBeTrue(error);
        config.GetEntryKeys("providers").ShouldBe(["p"]);
    }

    // ── The payload type refuses values it cannot faithfully represent ─────────────────

    [Fact]
    public void ConfigValueMap_RejectsAnUnsupportedValueType()
    {
        var config = ConfigDocument.Empty();
        var patch = new ConfigValueMap().Set("enabled", new object());

        config.TryPatchEntry("providers", "p", patch, out var error).ShouldBeFalse(
            "an unrepresentable value must fail loudly rather than being silently stringified");
        error.ShouldContain("unsupported type");
    }

    [Fact]
    public void ConfigValueMap_SupportsScalarsListsAndNesting()
    {
        var config = ConfigDocument.Empty();
        var patch = new ConfigValueMap()
            .Set("enabled", true)
            .Set("contextWindow", 128_000)
            .Set("apiKey", "secret")
            .Set("models", new[] { "a", "b" });

        config.TrySetEntry("providers", "p", patch, out var error).ShouldBeTrue(error);

        config.GetBool("providers.p.enabled").ShouldBe(true);
        config.GetInt("providers.p.contextWindow").ShouldBe(128_000);
        config.GetStringList("providers.p.models").ShouldBe(["a", "b"]);
    }

    [Fact]
    public void ConfigValueMap_SetIfNotNull_OmitsNullsEntirely()
    {
        var map = new ConfigValueMap()
            .SetIfNotNull("apiKey", null)
            .SetIfNotNull("baseUrl", "http://localhost");

        map.Count.ShouldBe(1);
    }

    // ── Fresh-install composition ─────────────────────────────────────────────────────

    [Fact]
    public void CreateForFreshInstall_EmitsTheReservedAgentsDefaultsBlock()
    {
        var config = ConfigDocument.CreateForFreshInstall(new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["assistant"] = new() { Provider = "github-copilot", Model = "gpt-4.1", Enabled = true }
            }
        });

        // `defaults` is reserved and lifted out by the loader, so it is injected after
        // serialisation - PlatformConfig.AgentDefaults is [JsonIgnore].
        config.GetEntryKeys("agents").ShouldContain("defaults");
        config.GetBool("agents.defaults.memory.enabled").ShouldBe(true);
        config.GetBool("agents.defaults.heartbeat.quietHours.enabled").ShouldBe(true);
        config.GetEntryKeys("agents").ShouldContain("assistant");
    }

    [Fact]
    public void CreateForFreshInstall_WithNoAgents_EmitsNoAgentsBlock()
    {
        var config = ConfigDocument.CreateForFreshInstall(new PlatformConfig());

        config.RootKeys.ShouldNotContain("agents");
    }

    [Fact]
    public void ReplaceWith_DiscardsTheExistingDocumentEntirely()
    {
        var config = ConfigDocument.Parse("""{ "gateway": { "listenUrl": "old" }, "unknownRoot": 1 }""");
        var replacement = ConfigDocument.Parse("""{ "gateway": { "listenUrl": "new" } }""");

        config.ReplaceWith(replacement);

        config.RootKeys.ShouldBe(["gateway"]);
        config.TryGetString(ListenUrlPath, out var url).ShouldBeTrue();
        url.ShouldBe("new");
    }
}
