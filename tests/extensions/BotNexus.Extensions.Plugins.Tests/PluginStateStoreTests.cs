using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the installed-plugin record store. The record is the only thing that knows which files a
/// plugin owns, so losing or truncating it orphans the content permanently.
/// </summary>
public sealed class PluginStateStoreTests : IDisposable
{
    private readonly string _root;
    private readonly PluginStateStore _store;

    public PluginStateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new PluginStateStore(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static InstalledPlugin Record(string name, bool updatesEnabled = true) => new()
    {
        Name = name,
        Source = $"https://example.com/{name}.git",
        ResolvedVersion = "v1",
        InstalledAtUtc = DateTimeOffset.UnixEpoch,
        UpdatesEnabled = updatesEnabled,
        Files = [".botnexus-plugin/plugin.json"],
    };

    [Fact]
    public void ReadingAnAbsentStateFileYieldsNoPluginsRatherThanThrowing()
    {
        Assert.Empty(_store.Read());
        Assert.Null(_store.Find("anything"));
    }

    [Fact]
    public void UpsertPersistsAcrossStoreInstances()
    {
        _store.Upsert(Record("alpha"));

        var reread = new PluginStateStore(_root).Find("alpha");

        Assert.NotNull(reread);
        Assert.Equal("v1", reread!.ResolvedVersion);
        Assert.Equal([".botnexus-plugin/plugin.json"], reread.Files);
    }

    [Fact]
    public void UpsertReplacesTheRecordForTheSameNameRatherThanDuplicatingIt()
    {
        _store.Upsert(Record("alpha"));
        _store.Upsert(Record("alpha") with { ResolvedVersion = "v2" });

        var all = _store.Read();
        Assert.Single(all);
        Assert.Equal("v2", all[0].ResolvedVersion);
    }

    [Fact]
    public void DeleteRemovesOnlyTheNamedRecordAndReportsWhetherItExisted()
    {
        _store.Upsert(Record("alpha"));
        _store.Upsert(Record("beta"));

        Assert.True(_store.Delete("alpha"));
        Assert.False(_store.Delete("alpha"));
        Assert.Equal(["beta"], _store.Read().Select(p => p.Name));
    }

    // The default matters: a record written without an explicit preference must read back as
    // update-enabled, because pinning is opt-in.
    [Fact]
    public void AnUpdatePreferenceRoundTripsInBothStates()
    {
        _store.Upsert(Record("alpha", updatesEnabled: true));
        _store.Upsert(Record("beta", updatesEnabled: false));

        Assert.True(_store.Find("alpha")!.UpdatesEnabled);
        Assert.False(_store.Find("beta")!.UpdatesEnabled);
    }

    // A write must not leave its temp file behind - a stray .tmp next to the state file would be
    // picked up by anything scanning the plugin root.
    [Fact]
    public void WriteLeavesNoTemporaryFileBehind()
    {
        _store.Upsert(Record("alpha"));

        Assert.True(File.Exists(_store.StatePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
