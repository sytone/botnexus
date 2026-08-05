using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #2816. A 2026-07-31 production write reduced the whole <c>channels</c> section of
/// config.json to <c>{"enabled": true}</c>, destroying a Service Bus connection block and two
/// Telegram bot tokens, and reported success. These tests pin the two independent defences added
/// in response: the writer-level destructive-section guard (clause 3, and its non-straitjacket
/// clause 5), the collateral-damage property (clause 2), and the round-trip safety of
/// <see cref="ChannelConfig"/> itself (clause 4).
/// </summary>
public sealed class ConfigSectionDestructiveWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-2816-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public ConfigSectionDestructiveWriteTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
    }

    /// <summary>
    /// The shape that was actually destroyed: a populated <c>channels</c> section whose real
    /// content is NESTED (telegram bot tokens, Service Bus queues), which the pre-#2816
    /// <c>ChannelConfig</c> could not represent at all.
    /// </summary>
    private const string SourceJson = """
    {
      "version": 1,
      "gateway": { "listenUrl": "http://localhost:5005" },
      "providers": {
        "github-copilot": { "enabled": true, "apiKey": "sk-copilot-REAL" }
      },
      "channels": {
        "servicebus": {
          "enabled": true,
          "namespace": "contoso.servicebus.windows.net",
          "connectionString": "Endpoint=sb://contoso/;SharedAccessKey=REAL-KEY",
          "queues": {
            "inbound": { "name": "inbound-q", "maxConcurrent": 4 },
            "outbound": { "name": "outbound-q", "maxConcurrent": 2 }
          }
        },
        "telegram": {
          "enabled": true,
          "bots": {
            "main": { "token": "123456:REAL-telegram-token", "name": "MainBot" },
            "ops": { "token": "654321:REAL-ops-token", "name": "OpsBot" }
          }
        }
      },
      "agents": {
        "assistant": { "provider": "github-copilot", "model": "gpt-4.1" }
      }
    }
    """;

    private PlatformConfigWriter CreateWriter() => new(_configPath, _fileSystem);

    private async Task SeedAsync() => await File.WriteAllTextAsync(_configPath, SourceJson);

    private static string CanonicalChannels(string documentJson)
        => JsonNode.Parse(documentJson)!.AsObject()["channels"]!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    // ---------------------------------------------------------------------------------------
    // Clause 2 - a mutation naming only 'providers' cannot alter 'channels'.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// #2816 acceptance criterion 2: a targeted provider mutation must leave the populated
    /// <c>channels.servicebus</c> block - nested settings and all - byte-for-byte identical.
    /// </summary>
    [Fact]
    public async Task ProviderOnlyMutation_LeavesChannelsSectionByteForByteIdentical()
    {
        await SeedAsync();
        var before = CanonicalChannels(await File.ReadAllTextAsync(_configPath));

        var errors = await CreateWriter().MutateValidatedAsync(
            root =>
            {
                var providers = root["providers"]!.AsObject();
                providers["anthropic"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["apiKey"] = "sk-anthropic-REAL"
                };
                return null;
            },
            "before-provider-update");

        errors.ShouldBeEmpty();

        var afterDocument = await File.ReadAllTextAsync(_configPath);
        JsonNode.Parse(afterDocument)!["providers"]!["anthropic"].ShouldNotBeNull(
            "the mutation the caller actually asked for must have been applied");

        CanonicalChannels(afterDocument).ShouldBe(
            before,
            "#2816: a mutation that names only 'providers' must not alter 'channels' in any way.");
    }

    // ---------------------------------------------------------------------------------------
    // Clause 3 - the destructive-write guard.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// #2816 acceptance criterion 3, the exact incident shape: a write that flattens the whole
    /// <c>channels</c> section to <c>{"enabled": true}</c> while claiming to be a provider update
    /// must be REFUSED, the file left byte-unchanged, and the error must name 'channels'.
    /// </summary>
    [Fact]
    public async Task GuardRejects_WriteThatFlattensUnnamedChannelsSection()
    {
        await SeedAsync();
        var original = await File.ReadAllTextAsync(_configPath);

        var errors = await CreateWriter().MutateValidatedAsync(
            root =>
            {
                // The 2026-07-31 damage, reproduced exactly.
                root["channels"] = new JsonObject { ["enabled"] = true };
                return null;
            },
            "before-provider-update");

        errors.ShouldNotBeEmpty("#2816: the destructive candidate must be rejected, not applied.");
        errors[0].ShouldContain("channels", Case.Insensitive,
            "the operator must be told which section the refused write would have destroyed.");

        (await File.ReadAllTextAsync(_configPath)).ShouldBe(
            original,
            "#2816: a rejected write must leave config.json byte-for-byte unchanged.");
    }

    /// <summary>
    /// #2816 acceptance criterion 3: outright REMOVAL of an unnamed populated section is refused
    /// on the same terms as flattening it.
    /// </summary>
    [Fact]
    public async Task GuardRejects_WriteThatDropsUnnamedPopulatedSection()
    {
        await SeedAsync();
        var original = await File.ReadAllTextAsync(_configPath);

        var errors = await CreateWriter().MutateValidatedAsync(
            root =>
            {
                root.Remove("channels");
                return null;
            },
            "before-provider-update");

        errors.ShouldNotBeEmpty();
        errors[0].ShouldContain("channels", Case.Insensitive);
        (await File.ReadAllTextAsync(_configPath)).ShouldBe(original);
    }

    /// <summary>
    /// #2816 acceptance criterion 3, applied to the raw <c>MutateAsync</c> path, which has no
    /// error-list return channel and must therefore THROW rather than return quietly - returning
    /// quietly is indistinguishable from success and is the silent failure mode the issue is about.
    /// </summary>
    [Fact]
    public async Task GuardRejects_OnRawMutatePath_ByThrowing_AndLeavesFileUnchanged()
    {
        await SeedAsync();
        var original = await File.ReadAllTextAsync(_configPath);

        var exception = await Should.ThrowAsync<PlatformConfigSectionGuardException>(
            () => CreateWriter().MutateAsync(
                root => root["channels"] = new JsonObject { ["enabled"] = true },
                "before-provider-update"));

        exception.Message.ShouldContain("channels", Case.Insensitive);
        (await File.ReadAllTextAsync(_configPath)).ShouldBe(original);
    }

    /// <summary>
    /// #2816 acceptance criterion 3, applied to the typed whole-document replace
    /// (<c>UpdatePlatformConfigAsync</c>) - the write shape that produced the incident. Even with a
    /// deliberately lossy typed graph the guard must stop the document reaching disk.
    /// </summary>
    [Fact]
    public async Task GuardRejects_TypedWholeDocumentReplaceThatEmptiesChannels()
    {
        await SeedAsync();
        var original = await File.ReadAllTextAsync(_configPath);
        var writer = CreateWriter();

        var config = await writer.ReadPlatformConfigAsync();
        // Simulate the collapse: the caller hands back a snapshot with no channels at all.
        config.Channels = null;

        var exception = await Should.ThrowAsync<PlatformConfigSectionGuardException>(
            () => writer.UpdatePlatformConfigAsync(config, "typed-replace"));

        exception.Message.ShouldContain("channels", Case.Insensitive);
        (await File.ReadAllTextAsync(_configPath)).ShouldBe(original);
    }

    // ---------------------------------------------------------------------------------------
    // Clause 5 - the guard is not a straitjacket.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// #2816 acceptance criterion 5: a mutation that explicitly NAMES the section it is removing
    /// still succeeds. Without this the guard would make legitimate section removal impossible and
    /// would be disabled by the first operator who hit it.
    /// </summary>
    [Fact]
    public async Task GuardAllows_RemovalOfExplicitlyNamedSection()
    {
        await SeedAsync();

        var errors = await CreateWriter().MutateValidatedAsync(
            root =>
            {
                root.Remove("channels");
                return null;
            },
            "channels-remove",
            CancellationToken.None,
            namedSections: ["channels"]);

        errors.ShouldBeEmpty("#2816 clause 5: naming the section makes the removal legitimate.");

        var after = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        after.ContainsKey("channels").ShouldBeFalse();
        after["providers"].ShouldNotBeNull("only the named section may be removed.");
    }

    /// <summary>
    /// #2816 acceptance criterion 5: naming a section entitles the caller to destroy THAT section
    /// only. Collateral destruction of a second, unnamed section is still refused.
    /// </summary>
    [Fact]
    public async Task GuardStillRejects_UnnamedSection_WhenAnotherSectionIsNamed()
    {
        await SeedAsync();
        var original = await File.ReadAllTextAsync(_configPath);

        var errors = await CreateWriter().MutateValidatedAsync(
            root =>
            {
                root.Remove("channels");
                root.Remove("providers");
                return null;
            },
            "channels-remove",
            CancellationToken.None,
            namedSections: ["channels"]);

        errors.ShouldNotBeEmpty();
        errors[0].ShouldContain("providers", Case.Insensitive);
        errors[0].Contains("'channels'", StringComparison.Ordinal).ShouldBeFalse(
            "the named section is legitimate; only the unnamed one is the complaint.");
        (await File.ReadAllTextAsync(_configPath)).ShouldBe(original);
    }

    /// <summary>
    /// #2816: the guard must not fire on ordinary edits. Emptying a section by removing its last
    /// entry through the section-scoped API names that section, so it is permitted; and rewriting
    /// a section's contents is not destruction at all.
    /// </summary>
    [Fact]
    public async Task GuardAllows_OrdinaryEdits_ThatDoNotDestroyASection()
    {
        await SeedAsync();
        var writer = CreateWriter();

        // Removing the last provider empties 'providers' - but the API named it.
        await writer.RemoveSectionEntryAsync("providers", "github-copilot");

        var after = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        after["providers"]!.AsObject().Count.ShouldBe(0);
        after["channels"]!["telegram"]!["bots"]!["main"]!["token"]!.GetValue<string>()
            .ShouldBe("123456:REAL-telegram-token", "unrelated sections must be untouched.");
    }

    /// <summary>
    /// #2816: clearing a top-level SCALAR is an ordinary single-value edit and is deliberately
    /// outside the guard's scope. Pinned so a future reader does not "tighten" the guard into
    /// blocking routine edits, which is how guards get deleted. Asserted through the guard directly
    /// rather than the writer, so schema validation of the chosen key cannot confound the result.
    /// </summary>
    [Fact]
    public void GuardAllows_ClearingATopLevelScalar()
    {
        var current = JsonNode.Parse(SourceJson)!.AsObject();
        var candidate = JsonNode.Parse(SourceJson)!.AsObject();
        candidate["apiKey"] = null;
        candidate.Remove("version");

        ConfigSectionGuard.FindDestroyedSections(current, candidate, namedSections: null)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// #2816: rewriting a section's values in place, or removing SOME of its entries, is an
    /// ordinary edit and must pass. The guard fires only when a section is dropped, emptied, or
    /// stripped of every key it held.
    /// </summary>
    [Fact]
    public void GuardAllows_PartialEditOfAnUnnamedSection()
    {
        var current = JsonNode.Parse(SourceJson)!.AsObject();
        var candidate = JsonNode.Parse(SourceJson)!.AsObject();
        candidate["channels"]!.AsObject().Remove("telegram");
        candidate["channels"]!["servicebus"]!["namespace"] = "other.servicebus.windows.net";

        ConfigSectionGuard.FindDestroyedSections(current, candidate, namedSections: null)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// #2816: the precise incident shape, asserted on the guard itself - <c>channels</c> replaced
    /// by <c>{"enabled": true}</c> is still a non-empty object, so an emptiness-only test would
    /// have permitted the very write that destroyed production credentials.
    /// </summary>
    [Fact]
    public void Guard_TreatsFlattenedButNonEmptySection_AsDestroyed()
    {
        var current = JsonNode.Parse(SourceJson)!.AsObject();
        var candidate = JsonNode.Parse(SourceJson)!.AsObject();
        candidate["channels"] = new JsonObject { ["enabled"] = true };

        ConfigSectionGuard.FindDestroyedSections(current, candidate, namedSections: null)
            .ShouldBe(["channels"]);
    }

    // ---------------------------------------------------------------------------------------
    // Clause 4 - ChannelConfig round-trip safety.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// #2816 acceptance criterion 4: a typed round-trip of a config whose <c>channels</c> entries
    /// carry NESTED, non-string content must preserve that content. This fails against
    /// <c>ChannelConfig</c> as it stood before #2816 (Type/Enabled/Dictionary&lt;string,string&gt;
    /// only), where the whole entry collapsed to <c>{"enabled": true}</c>.
    /// </summary>
    [Fact]
    public async Task TypedRoundTrip_PreservesNestedChannelSettings()
    {
        await SeedAsync();
        var writer = CreateWriter();

        var config = await writer.ReadPlatformConfigAsync();

        // Round-trip through the typed graph exactly as the whole-document write path does.
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var channels = JsonNode.Parse(json)!.AsObject()["channels"]!.AsObject();

        var serviceBus = channels["servicebus"]!.AsObject();
        serviceBus["connectionString"]!.GetValue<string>()
            .ShouldBe("Endpoint=sb://contoso/;SharedAccessKey=REAL-KEY",
                "#2816: the Service Bus connection string was destroyed by exactly this round-trip.");
        serviceBus["namespace"]!.GetValue<string>().ShouldBe("contoso.servicebus.windows.net");
        serviceBus["queues"]!["inbound"]!["maxConcurrent"]!.GetValue<int>().ShouldBe(4,
            "a nested, non-string setting must survive the typed graph.");

        var telegram = channels["telegram"]!.AsObject();
        telegram["bots"]!["main"]!["token"]!.GetValue<string>()
            .ShouldBe("123456:REAL-telegram-token", "#2816: both bot tokens were destroyed.");
        telegram["bots"]!["ops"]!["token"]!.GetValue<string>()
            .ShouldBe("654321:REAL-ops-token");
    }

    /// <summary>
    /// #2816 acceptance criterion 4, at the <see cref="ChannelConfig"/> level directly: an entry
    /// deserialised from a rich channel block and serialised straight back must not lose anything.
    /// </summary>
    [Fact]
    public void ChannelConfig_RoundTrip_DoesNotCollapseToEnabledOnly()
    {
        const string entryJson = """
        {
          "enabled": true,
          "botToken": "123:abc",
          "allowedChatIds": [12345, 67890],
          "bots": { "main": { "token": "REAL", "name": "MainBot" } }
        }
        """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var entry = JsonSerializer.Deserialize<ChannelConfig>(entryJson, options)!;
        var round = JsonNode.Parse(JsonSerializer.Serialize(entry, options))!.AsObject();

        round["botToken"]!.GetValue<string>().ShouldBe("123:abc");
        round["allowedChatIds"]!.AsArray().Count.ShouldBe(2);
        round["bots"]!["main"]!["token"]!.GetValue<string>().ShouldBe("REAL");
        round["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
