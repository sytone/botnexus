using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// End-to-end cover for the DTO-diff write path (#3532) against a real file and a real SQLite store.
/// </summary>
/// <remarks>
/// <see cref="ConfigDtoDifferTests"/> pins the diff in isolation. These tests exist because the property
/// that matters is not "the change set is correct" but "the bytes on disk still contain the credential
/// afterwards" - which only a real round-trip can demonstrate.
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
            // A leaked handle must not fail an otherwise-passing test; the temp directory is disposable.
        }
    }

    private sealed class ChannelDto
    {
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// The #2816 reconstruction, end to end: enabling one channel must not disturb the credentials
    /// stored beside it.
    /// </summary>
    [Fact]
    public async Task EnablingOneChannel_LeavesSiblingCredentialsOnDisk()
    {
        var fileSystem = new FileSystem();
        await File.WriteAllTextAsync(_configPath, """
            {
              "channels": {
                "telegram": { "enabled": false, "botToken": "tg-secret" },
                "teams": { "enabled": true, "serviceBus": "Endpoint=sb://real" }
              }
            }
            """);

        var writer = new JsonConfigurationWriter(_configPath, fileSystem);

        var changes = await writer.ApplyAsync(
            new ChannelDto { Enabled = true },
            "channels.telegram",
            reason: "test",
            ConfigDiffOptions.Additive);

        changes.Upserts.ShouldHaveSingleItem().Path.ShouldBe("channels.telegram.enabled");

        var after = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();

        // The change landed...
        after["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        // ...and neither credential was collateral damage. This is the assertion #2816 needed.
        after["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("tg-secret");
        after["channels"]!["teams"]!["serviceBus"]!.GetValue<string>().ShouldBe("Endpoint=sb://real");
    }

    /// <summary>
    /// A no-op apply must not rewrite the file, because an unchanged save would otherwise be
    /// indistinguishable from a real one in the backup history and the mtime.
    /// </summary>
    [Fact]
    public async Task UnchangedApply_DoesNotTouchTheFile()
    {
        var fileSystem = new FileSystem();
        await File.WriteAllTextAsync(_configPath, """{ "channels": { "telegram": { "enabled": true } } }""");

        var before = File.GetLastWriteTimeUtc(_configPath);
        await Task.Delay(50);

        var writer = new JsonConfigurationWriter(_configPath, fileSystem);
        var changes = await writer.ApplyAsync(
            new ChannelDto { Enabled = true }, "channels.telegram", "test", ConfigDiffOptions.Additive);

        changes.IsEmpty.ShouldBeTrue();
        File.GetLastWriteTimeUtc(_configPath).ShouldBe(before);
    }

    /// <summary>
    /// The store applies only the changed rows, leaving every other key exactly as it was.
    /// </summary>
    [Fact]
    public async Task StoreApply_TouchesOnlyTheChangedRow()
    {
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(JsonNode.Parse("""
            {
              "channels": {
                "telegram": { "enabled": false, "botToken": "tg-secret" },
                "teams": { "enabled": true }
              }
            }
            """)!.AsObject());

        var writer = new SqliteConfigurationWriter(store);
        await writer.ApplyAsync(
            new ChannelDto { Enabled = true }, "channels.telegram", "test", ConfigDiffOptions.Additive);

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
    public async Task RemovingTheLastKey_PrunesTheEmptiedParentOnly()
    {
        var document = JsonNode.Parse("""
            { "agents": { "retired": { "model": "old" }, "nova": { "model": "sonnet" } } }
            """)!.AsObject();

        var changes = new ConfigChangeSet("agents.retired", [], ["agents.retired.model"]);
        ConfigDocumentPatcher.Apply(document, changes);

        document["agents"]!.AsObject().ContainsKey("retired").ShouldBeFalse();

        // The sibling is untouched - pruning stops the moment a parent still has children.
        document["agents"]!["nova"]!["model"]!.GetValue<string>().ShouldBe("sonnet");

        await Task.CompletedTask;
    }

    /// <summary>
    /// A fan-out apply reaches both backends, and each converges even when they started out of step.
    /// </summary>
    [Fact]
    public async Task FanOutApply_ConvergesABackendThatWasBehind()
    {
        var fileSystem = new FileSystem();
        await File.WriteAllTextAsync(_configPath, """{ "channels": { "telegram": { "enabled": false } } }""");

        // The store starts EMPTY - the JSON-to-SQLite transition state.
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        var fanOut = new FanOutConfigurationWriter(
            [new JsonConfigurationWriter(_configPath, fileSystem), new SqliteConfigurationWriter(store)]);

        await fanOut.ApplyAsync(
            new ChannelDto { Enabled = true }, "channels.telegram", "test", ConfigDiffOptions.Additive);

        var file = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        file["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        // The lagging store caught up rather than staying silently behind.
        var entries = await store.ReadEntriesAsync();
        entries["channels.telegram.enabled"].Value.ShouldBe("true");
    }
}
