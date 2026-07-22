using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #2114: <see cref="PlatformConfigWriter"/> must detect no-op mutations and avoid
/// backing up, replacing, or otherwise touching config.json when the resulting canonical
/// JSON is identical to what is already on disk.
/// </summary>
public sealed class PlatformConfigWriterNoOpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "botnexus-writer-noop-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly string _backupsDir;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public PlatformConfigWriterNoOpTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
        _backupsDir = Path.Combine(_dir, "backups");
    }

    [Fact]
    public async Task MutateAsync_WhenMutationProducesIdenticalJson_DoesNotBackupOrReplace()
    {
        var initial = new JsonObject
        {
            ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5005" }
        };
        await File.WriteAllTextAsync(
            _configPath,
            initial.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var backup = new ConfigBackupService(_backupsDir, _fileSystem);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup);

        var before = File.GetLastWriteTimeUtc(_configPath);
        var beforeBytes = await File.ReadAllBytesAsync(_configPath);
        await Task.Delay(20);

        // A mutation that reads and rewrites the same value -> no effective change.
        await writer.MutateAsync(root =>
        {
            root["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5005" };
        }, "noop-test");

        var after = File.GetLastWriteTimeUtc(_configPath);
        var afterBytes = await File.ReadAllBytesAsync(_configPath);

        after.ShouldBe(before, "A no-op mutation must not replace config.json.");
        afterBytes.ShouldBe(beforeBytes);
        (Directory.Exists(_backupsDir) && Directory.GetFiles(_backupsDir, "config-*.json").Length > 0)
            .ShouldBeFalse("A no-op mutation must not create a backup.");
    }

    [Fact]
    public async Task MutateAsync_WhenMutationChangesJson_BacksUpAndReplaces()
    {
        var initial = new JsonObject
        {
            ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5005" }
        };
        await File.WriteAllTextAsync(
            _configPath,
            initial.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var backup = new ConfigBackupService(_backupsDir, _fileSystem);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup);

        await writer.MutateAsync(root =>
        {
            root["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:6006" };
        }, "real-change");

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        root["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:6006");
        Directory.GetFiles(_backupsDir, "config-*.json").Length.ShouldBe(1,
            "A real change must create exactly one backup.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
