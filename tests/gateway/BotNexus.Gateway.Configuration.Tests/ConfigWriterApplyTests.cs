using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// End-to-end cover for the change-set write path (#3532) against a real file and a real SQLite store.
/// </summary>
/// <remarks>
/// <see cref="ConfigWriteMatrixTests"/> covers shape-by-shape behaviour across both backends. These
/// tests exist for the properties that only a real round-trip can show: that the bytes on disk still
/// contain the credential afterwards, and that a no-op genuinely does not touch the file.
/// </remarks>
public sealed class ConfigWriterApplyTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;

    public ConfigWriterApplyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
        _storePath = Path.Combine(_directory, "config.db");
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(_storePath);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle must not fail an otherwise-passing test.
        }
    }

    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// The #2816 reconstruction, end to end: enabling one channel must not disturb the credentials
    /// stored beside it.
    /// </summary>
    [Fact]
    public async Task EnablingOneChannel_LeavesSiblingCredentialsOnDisk()
    {
        await File.WriteAllTextAsync(_configPath, """
            {
              "channels": {
                "telegram": { "enabled": false, "botToken": "tg-secret" },
                "teams": { "enabled": true, "serviceBus": "Endpoint=sb://real" }
              }
            }
            """);

        var before = Doc(await File.ReadAllTextAsync(_configPath));
        var after = before.DeepClone().AsObject();
        after["channels"]!["telegram"]!["enabled"] = true;

        var changes = ConfigDocumentDiffer.Diff(before, after);
        changes.Upserts.ShouldHaveSingleItem().Path.ShouldBe("channels.telegram.enabled");

        var writer = new JsonConfigurationWriter(_configPath, new FileSystem());
        await writer.ApplyChangeSetAsync(changes, "test");

        var result = Doc(await File.ReadAllTextAsync(_configPath));

        // The change landed...
        result["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        // ...and neither credential was collateral damage. This is the assertion #2816 needed.
        result["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("tg-secret");
        result["channels"]!["teams"]!["serviceBus"]!.GetValue<string>().ShouldBe("Endpoint=sb://real");
    }

    /// <summary>
    /// An empty change set must not rewrite the file, because an unchanged save would otherwise be
    /// indistinguishable from a real one in the backup history and the mtime.
    /// </summary>
    [Fact]
    public async Task EmptyChangeSet_DoesNotTouchTheFile()
    {
        await File.WriteAllTextAsync(_configPath, """{ "channels": { "telegram": { "enabled": true } } }""");

        var document = Doc(await File.ReadAllTextAsync(_configPath));
        var changes = ConfigDocumentDiffer.Diff(document, document.DeepClone().AsObject());
        changes.IsEmpty.ShouldBeTrue();

        // A sentinel timestamp rather than a sleep: any real write moves it, and asserting equality is
        // deterministic where waiting for the clock to tick is not.
        var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_configPath, sentinel);

        await new JsonConfigurationWriter(_configPath, new FileSystem())
            .ApplyChangeSetAsync(changes, "test");

        File.GetLastWriteTimeUtc(_configPath).ShouldBe(sentinel);
    }

    /// <summary>
    /// The store applies only the changed rows, leaving every other key exactly as it was.
    /// </summary>
    [Fact]
    public async Task StoreApply_TouchesOnlyTheChangedRow()
    {
        var before = Doc("""
            {
              "channels": {
                "telegram": { "enabled": false, "botToken": "tg-secret" },
                "teams": { "enabled": true }
              }
            }
            """);

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(before);

        var after = before.DeepClone().AsObject();
        after["channels"]!["telegram"]!["enabled"] = true;

        await new SqliteConfigurationWriter(store)
            .ApplyChangeSetAsync(ConfigDocumentDiffer.Diff(before, after), "test");

        var entries = await store.ReadEntriesAsync();

        entries["channels.telegram.enabled"].Value.ShouldBe("true");
        entries["channels.telegram.botToken"].Value.ShouldBe("\"tg-secret\"");
        entries["channels.teams.enabled"].Value.ShouldBe("true");
    }

    /// <summary>
    /// Removing the last key under a section prunes the emptied parent, so it reads back as absent
    /// rather than as a configured-but-empty section.
    /// </summary>
    [Fact]
    public void RemovingTheLastKey_PrunesTheEmptiedParentOnly()
    {
        var document = Doc("""
            { "agents": { "retired": { "model": "old" }, "nova": { "model": "sonnet" } } }
            """);

        ConfigDocumentPatcher.Apply(document, new ConfigChangeSet([], ["agents.retired.model"]));

        document["agents"]!.AsObject().ContainsKey("retired").ShouldBeFalse();

        // The sibling is untouched - pruning stops the moment a parent still has children.
        document["agents"]!["nova"]!["model"]!.GetValue<string>().ShouldBe("sonnet");
    }

    /// <summary>
    /// A fan-out apply reaches both backends, so a store that started empty catches up rather than
    /// silently staying behind - the JSON-to-SQLite transition state.
    /// </summary>
    [Fact]
    public async Task FanOutApply_ConvergesABackendThatWasBehind()
    {
        await File.WriteAllTextAsync(_configPath, """{ "channels": { "telegram": { "enabled": false } } }""");

        var before = Doc(await File.ReadAllTextAsync(_configPath));
        var after = before.DeepClone().AsObject();
        after["channels"]!["telegram"]!["enabled"] = true;

        // The store starts EMPTY.
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        var fanOut = new FanOutConfigurationWriter(
            [new JsonConfigurationWriter(_configPath, new FileSystem()), new SqliteConfigurationWriter(store)]);

        await fanOut.ApplyChangeSetAsync(ConfigDocumentDiffer.Diff(before, after), "test");

        var file = Doc(await File.ReadAllTextAsync(_configPath));
        file["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        var entries = await store.ReadEntriesAsync();
        entries["channels.telegram.enabled"].Value.ShouldBe("true");
    }
}
