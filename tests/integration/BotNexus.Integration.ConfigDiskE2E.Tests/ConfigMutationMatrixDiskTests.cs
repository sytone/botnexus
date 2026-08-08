using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// The physical-file mutation matrix for #2066: every production config mutation entry point
/// driven against a real <c>config.json</c> on a real filesystem, with the resulting document
/// diffed against the maximal seed so only the intended semantic delta is permitted.
/// </summary>
/// <remarks>
/// These are the acceptance tests the previous mock-filesystem suite could not be: the writer
/// here performs genuine temp-file creation, genuine <c>File.Move</c> replacement over an
/// existing inode, and genuine backup copies, and the assertions read the bytes back off disk
/// rather than out of an in-memory dictionary.
/// </remarks>
public sealed class ConfigMutationMatrixDiskTests
{
    /// <summary>
    /// Updating a section through the config-UI PUT path must change only the edited scalar.
    /// Every other key in the maximal document - including the JSON the typed model cannot bind -
    /// must survive byte-equivalently on disk.
    /// </summary>
    [Fact]
    public async Task UpdateSection_OnPhysicalFile_ChangesOnlyTheEditedPath()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var gateway = before["gateway"]!.DeepClone().AsObject();
        gateway["logLevel"] = "Debug";

        await home.Writer.UpdateSectionAsync("gateway", gateway);

        var after = home.ReadFromDisk();
        JsonDelta.Compute(before, after).ShouldBe(["gateway.logLevel"]);
        after["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Debug");
    }

    /// <summary>
    /// The UI GET serves redacted secrets. PUTting that redacted payload straight back must
    /// restore the real values from disk (#1955) - verified here against the actual file, so a
    /// clobber would be visible as raw <c>"***"</c> text in the persisted bytes.
    /// </summary>
    [Fact]
    public async Task UpdateSection_WithRedactedPayload_LeavesNoPlaceholderTextOnDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var wrapper = new JsonObject { ["gateway"] = before["gateway"]!.DeepClone() };
        ConfigSecretMerge.Redact(wrapper);
        var redactedGateway = wrapper["gateway"]!.AsObject();
        redactedGateway["listenUrl"] = "http://localhost:6006";

        await home.Writer.UpdateSectionAsync("gateway", redactedGateway.DeepClone());

        var rawText = home.ReadRawText();
        rawText.ShouldNotContain(ConfigSecretMerge.Placeholder);

        var after = home.ReadFromDisk();
        JsonDelta.Compute(before, after).ShouldBe(["gateway.listenUrl"]);
        after["gateway"]!["apiKeys"]!["primary"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("gw-primary-REAL-secret");
        after["gateway"]!["sessionStore"]!["connectionString"]!.GetValue<string>()
            .ShouldBe("Data Source=REAL-sessions.db");
    }

    /// <summary>
    /// A partial section payload (the shape a typed UI form produces) must not delete the
    /// collection subtrees it omits (#1954). Asserted on disk across two independent collections.
    /// </summary>
    [Fact]
    public async Task UpdateSection_WithOmittedCollections_PreservesThemOnDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var partialChannels = new JsonObject
        {
            ["telegram"] = new JsonObject { ["enabled"] = false }
        };

        await home.Writer.UpdateSectionAsync("channels", partialChannels);

        var after = home.ReadFromDisk();
        JsonDelta.Compute(before, after).ShouldBe(["channels.telegram.enabled"]);

        var telegramBots = after["channels"]!["telegram"]!["bots"]!.AsObject();
        telegramBots.Count.ShouldBe(2);
        telegramBots["ops"]!["token"]!.GetValue<string>().ShouldBe("654321:REAL-ops-token");
        after["channels"]!["serviceBus"]!["queues"]!.AsObject().Count.ShouldBe(2);
    }

    /// <summary>
    /// The non-merging overload is the delete-by-omission path (LocationsController). It must
    /// genuinely replace the section on disk so an omitted entry is removed - the counterpart
    /// guarantee to the merge path, and equally load-bearing.
    /// </summary>
    [Fact]
    public async Task UpdateSection_WithMergeDisabled_RemovesOmittedEntriesOnDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var replacement = new JsonObject
        {
            ["telegram"] = before["channels"]!["telegram"]!.DeepClone()
        };

        await home.Writer.UpdateSectionAsync("channels", replacement, merge: false);

        var after = home.ReadFromDisk();
        after["channels"]!.AsObject().ContainsKey("serviceBus").ShouldBeFalse();
        JsonDelta.Compute(before, after).ShouldBe(["channels.serviceBus"]);
    }

    /// <summary>
    /// The per-entry PUT path must scope its merge to the single keyed entry: siblings in the
    /// same section are untouched and the entry's own omitted keys survive.
    /// </summary>
    [Fact]
    public async Task UpdateSectionEntry_OnPhysicalFile_ScopesDeltaToThatEntry()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var entry = new JsonObject
        {
            ["defaultModel"] = "gpt-4.1",
            ["apiKey"] = ConfigSecretMerge.Placeholder
        };

        await home.Writer.UpdateSectionEntryAsync("providers", "github-copilot", entry);

        var after = home.ReadFromDisk();
        JsonDelta.Compute(before, after).ShouldBe(["providers.github-copilot.defaultModel"]);
        after["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-copilot-REAL-secret");
        after["providers"]!["github-copilot"]!["models"]!.AsArray().Count.ShouldBe(2);
        after["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-anthropic-REAL-secret");
    }

    /// <summary>
    /// Removing a keyed entry must delete exactly that entry from the physical document and
    /// nothing else - notably not the reserved <c>agents.defaults</c> sibling.
    /// </summary>
    [Fact]
    public async Task RemoveSectionEntry_OnPhysicalFile_RemovesOnlyThatEntry()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        await home.Writer.RemoveSectionEntryAsync("agents", "builder");

        var after = home.ReadFromDisk();
        after["agents"]!.AsObject().ContainsKey("builder").ShouldBeFalse();
        after["agents"]!.AsObject().ContainsKey("defaults").ShouldBeTrue();
        after["agents"]!["assistant"]!["toolIds"]!.AsArray().Count.ShouldBe(3);

        JsonDelta.Compute(before, after).ShouldBe(["agents.builder"]);
    }

    /// <summary>
    /// JSON the typed model cannot bind - the <c>$schema</c> pointer and a vendor block with an
    /// array and nested objects - must survive an unrelated mutation. This is the extension-JSON
    /// guarantee: config.json is a shared document, not a private serialization of one model.
    /// </summary>
    [Fact]
    public async Task Mutation_PreservesUnknownAndExtensionJsonOnDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        await home.Writer.MutateAsync(
            root => root["cron"]!["tickIntervalSeconds"] = 120,
            "test-unknown-json");

        var after = home.ReadFromDisk();
        JsonDelta.Compute(before, after).ShouldBe(["cron.tickIntervalSeconds"]);

        after["$schema"]!.GetValue<string>().ShouldBe("https://botnexus.dev/schema/config.json");
        after["customVendorBlock"]!["unknownArray"]!.AsArray().Count.ShouldBe(3);
        after["customVendorBlock"]!["nested"]!["deep"]!["value"]!.GetValue<string>()
            .ShouldBe("preserve-me");
        after["agents"]!["assistant"]!["extensions"]!["botnexus-skills"]!["allow"]!
            .AsArray().Count.ShouldBe(2);
        after["gateway"]!["extensions"]!["defaults"]!["botnexus-skills"]!["root"]!
            .GetValue<string>().ShouldBe("skills");
    }

    /// <summary>
    /// <c>agents.defaults</c> is a reserved pseudo-agent whose values seed every agent. Editing it
    /// must land on disk without being mistaken for a real agent entry or disturbing the agents
    /// that inherit from it.
    /// </summary>
    [Fact]
    public async Task Mutation_OfAgentsDefaults_PersistsAndLeavesNamedAgentsIntact()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var defaults = before["agents"]!["defaults"]!.DeepClone().AsObject();
        defaults["memory"]!["promptInjection"] = "none";

        await home.Writer.UpdateSectionEntryAsync("agents", "defaults", defaults);

        var after = home.ReadFromDisk();
        JsonDelta.Compute(before, after).ShouldBe(["agents.defaults.memory.promptInjection"]);
        after["agents"]!["defaults"]!["toolIds"]!.AsArray().Count.ShouldBe(2);
        after["agents"]!["assistant"]!["provider"]!.GetValue<string>().ShouldBe("github-copilot");
    }

    /// <summary>
    /// Replacing the whole document through the typed model still loses JSON the typed
    /// <see cref="PlatformConfig"/> graph does not model, because it serialises the graph wholesale.
    /// Pinning that behaviour on a physical file documents the real blast radius of the typed
    /// replace path instead of leaving callers to discover it in production.
    /// </summary>
    /// <remarks>
    /// #2816 narrowed this considerably. The loss is no longer <em>silent</em>: the
    /// destructive-section guard now refuses any typed replace that would drop a populated
    /// top-level section the caller did not name, so this test has to declare
    /// <c>customVendorBlock</c> to reach the behaviour it pins at all. Previously it did not, and
    /// the same unguarded write shape destroyed a production <c>channels</c> section. The
    /// assertions below are unchanged; only the caller's declaration of intent is new.
    /// </remarks>
    [Fact]
    public async Task UpdatePlatformConfig_TypedReplace_PersistsModelledStateToDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var config = await home.Writer.ReadPlatformConfigAsync();
        config.Gateway.ShouldNotBeNull();
        config.Gateway!.LogLevel = "Warning";

        await home.Writer.UpdatePlatformConfigAsync(
            config,
            "test-typed-replace",
            CancellationToken.None,
            expectedRevision: null,
            namedSections: ["customVendorBlock"]);

        var after = home.ReadFromDisk();
        after["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Warning");
        after["gateway"]!["apiKeys"]!["primary"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("gw-primary-REAL-secret");
        after["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-copilot-REAL-secret");
    }

    /// <summary>
    /// A no-op write must not touch the file at all (#2114). On a real filesystem that means the
    /// last-write timestamp is unchanged, no backup is taken, and no reload storm is triggered -
    /// none of which an in-memory filesystem can attest to.
    /// </summary>
    [Fact]
    public async Task NoOpWrite_DoesNotTouchThePhysicalFileOrCreateBackups()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var originalText = home.ReadRawText();
        var originalWriteTime = File.GetLastWriteTimeUtc(home.ConfigPath);

        // Ensure a distinguishable timestamp would be observable if a write happened.
        await Task.Delay(50);

        await home.Writer.MutateAsync(_ => { }, "test-noop");

        home.ReadRawText().ShouldBe(originalText);
        File.GetLastWriteTimeUtc(home.ConfigPath).ShouldBe(originalWriteTime);
        home.ListBackups().ShouldBeEmpty();
    }
}
