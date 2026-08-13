using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Acceptance coverage for the atomic dirty-path config save with optimistic concurrency
/// (issue #2059), driven through the production <see cref="PlatformConfigWriter"/> against a real
/// <c>config.json</c> on a real filesystem, and read back through the real configuration provider.
/// </summary>
/// <remarks>
/// <para>
/// These tests are deliberately in the physical-disk suite rather than a unit project. The defect
/// being fixed is a <em>lost update</em>: it only exists when two saves interleave against one
/// committed document. An in-memory fake can be made to exhibit or hide that at will, so it would
/// prove nothing about the property under test.
/// </para>
/// <para>
/// The non-vacuity anchor for the whole file is
/// <see cref="Patch_OneField_LeavesEveryOtherSectionByteIdentical"/> together with
/// <see cref="Patch_ConcurrentEditToAnotherSection_IsNotClobbered"/>: the first proves the write
/// happened, the second proves it was <em>narrow</em>. A save that wrote everything would pass the
/// first alone, which is exactly how the defect survived.
/// </para>
/// </remarks>
public sealed class ConfigAtomicPatchDiskTests
{
    /// <summary>
    /// AC: a one-field change writes that field and touches nothing else. Every other top-level
    /// section must be byte-identical to what was on disk before the save.
    /// </summary>
    [Fact]
    public async Task Patch_OneField_LeavesEveryOtherSectionByteIdentical()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var result = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
            "test-one-field");

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        var after = home.ReadFromDisk();
        after["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Debug");

        foreach (var key in before.Select(kv => kv.Key).Where(k => k != "gateway"))
        {
            JsonNode.DeepEquals(before[key], after[key])
                .ShouldBeTrue($"section '{key}' was rewritten by a save that only edited gateway.logLevel");
        }

        // Within gateway itself, only the addressed leaf moved.
        JsonNode.DeepEquals(before["gateway"]!["apiKeys"], after["gateway"]!["apiKeys"]).ShouldBeTrue();
        after["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:5005");
    }

    /// <summary>
    /// AC: concurrent NON-conflicting edits both survive. This is the exact scenario the old
    /// whole-section save loop destroyed - editing a gateway field also rewrote providers from a
    /// stale snapshot, reverting the other writer.
    /// </summary>
    [Fact]
    public async Task Patch_ConcurrentEditToAnotherSection_IsNotClobbered()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        // Writer A loads the page (snapshot + revision) but has not saved yet.
        var (_, revisionA) = await home.Writer.ReadWithRevisionAsync();

        // Writer B commits a change to an unrelated section in the meantime.
        var writerB = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("providers.anthropic.baseUrl", JsonValue.Create("https://b.example"))],
            "test-writer-b");
        writerB.Success.ShouldBeTrue(string.Join("; ", writerB.Errors));

        // Writer A now saves its own, non-overlapping field. It quotes the STALE revision, so this
        // must be refused rather than silently reverting writer B.
        await Should.ThrowAsync<PlatformConfigConcurrencyException>(() =>
            home.Writer.ApplyPatchAsync(
                [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
                "test-writer-a-stale",
                revisionA));

        // Writer B's change is intact and writer A's was NOT applied.
        var disk = home.ReadFromDisk();
        disk["providers"]!["anthropic"]!["baseUrl"]!.GetValue<string>().ShouldBe("https://b.example");
        disk["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Information");

        // Re-reading gives a fresh revision, and the same save then succeeds without disturbing B.
        var (_, revisionA2) = await home.Writer.ReadWithRevisionAsync();
        var retry = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
            "test-writer-a-retry",
            revisionA2);
        retry.Success.ShouldBeTrue(string.Join("; ", retry.Errors));

        var final = home.ReadFromDisk();
        final["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Debug");
        final["providers"]!["anthropic"]!["baseUrl"]!.GetValue<string>().ShouldBe("https://b.example");
    }

    /// <summary>
    /// AC: a concurrent CONFLICTING edit - both writers touching the same field - is rejected as a
    /// conflict, not resolved by last-writer-wins.
    /// </summary>
    [Fact]
    public async Task Patch_ConflictingEditToSameField_IsRejectedAsConflict()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var (_, staleRevision) = await home.Writer.ReadWithRevisionAsync();

        var winner = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Warning"))],
            "test-conflict-winner");
        winner.Success.ShouldBeTrue(string.Join("; ", winner.Errors));

        var exception = await Should.ThrowAsync<PlatformConfigConcurrencyException>(() =>
            home.Writer.ApplyPatchAsync(
                [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Trace"))],
                "test-conflict-loser",
                staleRevision));

        exception.ExpectedRevision.ShouldBe(staleRevision);
        exception.ActualRevision.ShouldNotBe(staleRevision);

        // The winner's value survives: the loser did not silently overwrite it.
        home.ReadFromDisk()["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Warning");
    }

    /// <summary>
    /// AC: a changed field materialises a section that is absent from the raw document. The old
    /// save path filtered its write set by the raw document's existing top-level keys, so editing
    /// a default-only section could never persist - the edit vanished with a success message.
    /// </summary>
    [Fact]
    public async Task Patch_MaterializesSectionAbsentFromRawDocument()
    {
        const string minimal = """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:5005" }
            }
            """;

        using var home = new ConfigHomeFixture(minimal);
        home.ReadFromDisk().ContainsKey("cron").ShouldBeFalse("precondition: cron must be absent");

        var result = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("cron.tickIntervalSeconds", JsonValue.Create(120))],
            "test-materialize");

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        var disk = home.ReadFromDisk();
        disk.ContainsKey("cron").ShouldBeTrue("editing a default-only section must be able to create it");
        disk["cron"]!["tickIntervalSeconds"]!.GetValue<int>().ShouldBe(120);

        // A runtime consumer reading through the real configuration provider sees it too.
        using var consumer = home.BuildRuntimeConsumer();
        consumer.ReloadNow().Cron!.TickIntervalSeconds.ShouldBe(120);
    }

    /// <summary>
    /// AC: partial-failure prevention. A batch whose LAST operation is unapplicable must write
    /// nothing at all - not the operations that preceded it.
    /// </summary>
    /// <remarks>
    /// The old loop committed section by section and broke out of the loop on the first failure,
    /// leaving the document half-saved with a "Failed to save X" message that gave no hint which
    /// earlier sections had already landed.
    /// </remarks>
    [Fact]
    public async Task Patch_FailingOperation_CommitsNothingFromTheBatch()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadRawText();

        var result = await home.Writer.ApplyPatchAsync(
            [
                new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug")),
                new ConfigPatchOperation("providers.anthropic.enabled", JsonValue.Create(true)),
                // providers.github-copilot.models has 3 fewer entries than this index addresses.
                new ConfigPatchOperation("providers.github-copilot.models[9]", JsonValue.Create("nope")),
            ],
            "test-partial-failure");

        result.Success.ShouldBeFalse("an unapplicable operation must fail the whole batch");
        result.Errors.ShouldNotBeEmpty();

        // The two operations that preceded the failure must NOT be on disk.
        home.ReadRawText().ShouldBe(before, "a rejected batch must leave the file byte-for-byte unchanged");
    }

    /// <summary>
    /// AC: unknown fields and sections the typed model does not bind survive a patch untouched.
    /// </summary>
    [Fact]
    public async Task Patch_PreservesUnknownFieldsAndSections()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();

        var result = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
            "test-unknown-fields");
        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        var after = home.ReadFromDisk();
        JsonNode.DeepEquals(before["customVendorBlock"], after["customVendorBlock"])
            .ShouldBeTrue("an unmodelled top-level section must survive a patch verbatim");
        after["$schema"]!.GetValue<string>().ShouldBe("https://botnexus.dev/schema/config.json");
        JsonNode.DeepEquals(before["gateway"]!["extensions"], after["gateway"]!["extensions"])
            .ShouldBeTrue("an unmodelled nested subtree must survive a patch verbatim");
    }

    /// <summary>
    /// AC: an unknown field the operator ADDS through a patch is persisted rather than dropped by
    /// a typed round-trip.
    /// </summary>
    [Fact]
    public async Task Patch_AddsUnknownFieldWithoutTypedRoundTripLoss()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var result = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("customVendorBlock.newKey", JsonValue.Create("added"))],
            "test-add-unknown");
        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        home.ReadFromDisk()["customVendorBlock"]!["newKey"]!.GetValue<string>().ShouldBe("added");
    }

    /// <summary>
    /// AC: dictionaries. Adding, editing and removing dictionary entries in one batch commits
    /// exactly those changes and leaves sibling entries alone.
    /// </summary>
    [Fact]
    public async Task Patch_DictionaryEntries_AddEditAndRemoveAtomically()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var result = await home.Writer.ApplyPatchAsync(
            [
                new ConfigPatchOperation("providers.openai", new JsonObject
                {
                    ["enabled"] = true,
                    ["defaultModel"] = "gpt-4.1",
                }),
                new ConfigPatchOperation("providers.anthropic.enabled", JsonValue.Create(true)),
                new ConfigPatchOperation("channels.telegram.bots.ops", Remove: true),
            ],
            "test-dictionaries");

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        var disk = home.ReadFromDisk();
        disk["providers"]!["openai"]!["defaultModel"]!.GetValue<string>().ShouldBe("gpt-4.1");
        disk["providers"]!["anthropic"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        (disk["channels"]!["telegram"]!["bots"] as JsonObject)!.ContainsKey("ops").ShouldBeFalse();

        // The sibling that was not addressed is intact, including its real token.
        disk["channels"]!["telegram"]!["bots"]!["main"]!["token"]!.GetValue<string>()
            .ShouldBe("123456:REAL-telegram-token");
        disk["providers"]!["github-copilot"]!["defaultModel"]!.GetValue<string>().ShouldBe("claude-sonnet-4");
    }

    /// <summary>
    /// AC: lists. Replacing a whole array and editing one element by index both work, and an
    /// unrelated array is untouched.
    /// </summary>
    [Fact]
    public async Task Patch_Lists_ReplaceWholeArrayAndEditByIndex()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var result = await home.Writer.ApplyPatchAsync(
            [
                new ConfigPatchOperation("providers.github-copilot.models", new JsonArray("a", "b", "c")),
                new ConfigPatchOperation("gateway.cors.allowedOrigins[0]", JsonValue.Create("http://example")),
            ],
            "test-lists");

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        var disk = home.ReadFromDisk();
        disk["providers"]!["github-copilot"]!["models"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ShouldBe(["a", "b", "c"]);
        disk["gateway"]!["cors"]!["allowedOrigins"]![0]!.GetValue<string>().ShouldBe("http://example");
        JsonNode.DeepEquals(
            disk["customVendorBlock"]!["unknownArray"], new JsonArray(1, 2, 3)).ShouldBeTrue();
    }

    /// <summary>
    /// AC: secrets. A field the UI rendered as the redaction placeholder and submitted back
    /// verbatim must NOT overwrite the real on-disk secret.
    /// </summary>
    /// <remarks>
    /// This is the #1955 contract restated for the patch path. It is the single most damaging way
    /// a save can "succeed": the operator sees Saved, and the provider credential is now the string
    /// <c>***</c>.
    /// </remarks>
    [Fact]
    public async Task Patch_RedactedSecretPlaceholder_DoesNotOverwriteRealSecret()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var result = await home.Writer.ApplyPatchAsync(
            [
                // Exactly what a form round-trip produces: the real edit plus the masked sibling.
                new ConfigPatchOperation("providers.github-copilot", new JsonObject
                {
                    ["enabled"] = true,
                    ["apiKey"] = ConfigSecretMerge.Placeholder,
                    ["defaultModel"] = "gpt-4.1",
                    ["models"] = new JsonArray("claude-sonnet-4", "gpt-4.1"),
                }),
            ],
            "test-secrets");

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        var disk = home.ReadFromDisk();
        disk["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-copilot-REAL-secret", "a round-tripped placeholder must never replace a real secret");
        disk["providers"]!["github-copilot"]!["defaultModel"]!.GetValue<string>().ShouldBe("gpt-4.1");
    }

    /// <summary>
    /// AC: a genuinely NEW secret value the operator typed is written. The placeholder guard must
    /// not become a blanket refusal to ever change a secret.
    /// </summary>
    [Fact]
    public async Task Patch_RealSecretValue_IsWritten()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var result = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("providers.anthropic.apiKey", JsonValue.Create("sk-anthropic-NEW"))],
            "test-secret-rotate");

        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));
        home.ReadFromDisk()["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-anthropic-NEW");
    }

    /// <summary>
    /// A patch with no revision quoted still works (the concurrency check is opt-in), and returns
    /// the revision now on disk so the caller can chain the next save.
    /// </summary>
    [Fact]
    public async Task Patch_ReturnsCommittedRevisionUsableForTheNextSave()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var first = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
            "test-chain-1");
        first.Success.ShouldBeTrue(string.Join("; ", first.Errors));
        first.Revision.ShouldNotBeNullOrWhiteSpace();

        var second = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Trace"))],
            "test-chain-2",
            first.Revision);

        second.Success.ShouldBeTrue("the revision returned by a commit must be current");
        home.ReadFromDisk()["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Trace");
    }

    /// <summary>
    /// The destructive-section guard still applies: a patch that edits a field inside a section is
    /// not thereby entitled to flatten another section.
    /// </summary>
    [Fact]
    public async Task Patch_CannotEmptyAnUndeclaredSection()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadRawText();

        var result = await home.Writer.ApplyPatchAsync(
            [
                new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug")),
                // A bare-root remove of channels IS declared, so to test the guard we instead empty
                // channels from inside a gateway-scoped edit path.
                new ConfigPatchOperation("channels.telegram", Remove: true),
                new ConfigPatchOperation("channels.serviceBus", Remove: true),
            ],
            "test-guard");

        result.Success.ShouldBeFalse("emptying every key of an undeclared section must be refused");
        home.ReadRawText().ShouldBe(before);
    }

    /// <summary>
    /// After a commit the runtime consumer reloads and observes the change, so the page's
    /// post-commit re-read is reading a document the platform has actually adopted.
    /// </summary>
    [Fact]
    public async Task Patch_IsObservedByTheRuntimeConfigurationConsumer()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();
        consumer.Monitor.CurrentValue.Gateway!.LogLevel.ShouldBe("Information");

        var result = await home.Writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
            "test-reload");
        result.Success.ShouldBeTrue(string.Join("; ", result.Errors));

        // Assert on CONTENT via an explicit reload rather than on watcher timing: the OS debounces
        // filesystem notifications, and this test is about the committed document reaching the
        // runtime graph, not about how quickly the watcher fires (asserted separately elsewhere).
        consumer.ReloadNow().Gateway!.LogLevel.ShouldBe("Debug");
    }
}
