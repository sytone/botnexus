using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the plugin install / update / remove lifecycle (#2681).
/// </summary>
/// <remarks>
/// The load-bearing tests here are the three that pin behaviour a reasonable implementation gets
/// wrong: a pinned plugin must not be touched even when the source moved; removal must delete the
/// recorded file set and nothing else; and a fault mid-materialisation must leave no directory at
/// all. Each asserts the negative case directly rather than merely asserting the happy path.
/// </remarks>
public sealed class PluginLifecycleManagerTests : IDisposable
{
    private readonly string _root;
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly PluginLifecycleManager _manager;

    public PluginLifecycleManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new PluginStateStore(_root);
        _manager = new PluginLifecycleManager(_store, _fetcher);
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
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static Dictionary<string, string> PluginContent(
        string name,
        string? version = null,
        params (string Path, string Content)[] extra)
    {
        var manifest = version is null
            ? $$"""{ "name": "{{name}}" }"""
            : $$"""{ "name": "{{name}}", "version": "{{version}}" }""";

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".botnexus-plugin/plugin.json"] = manifest,
        };

        foreach (var (path, content) in extra)
        {
            files[path] = content;
        }

        return files;
    }

    // AC1 - install materialises content on disk AND records the resolved version.
    [Fact]
    public async Task InstallMaterialisesContentAndRecordsResolvedVersion()
    {
        _fetcher.Enqueue("a1b2c3d4", PluginContent(
            "hello-world",
            version: "1.0.0",
            ("skills/greet/SKILL.md", "# greet")));

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
        });

        Assert.Equal(PluginOperationOutcome.Installed, result.Outcome);

        var pluginDir = Path.Combine(_root, "hello-world");
        Assert.True(File.Exists(Path.Combine(pluginDir, ".botnexus-plugin", "plugin.json")));
        Assert.Equal("# greet", File.ReadAllText(Path.Combine(pluginDir, "skills", "greet", "SKILL.md")));

        var record = _store.Find("hello-world");
        Assert.NotNull(record);
        Assert.Equal("a1b2c3d4", record!.ResolvedVersion);
        Assert.Equal("1.0.0", record.ManifestVersion);
        Assert.Equal("https://example.com/hello.git", record.Source);
    }

    // AC1 - the recorded version is the RESOLVED revision, not the requested reference. Recording
    // "main" would make "has the source moved?" unanswerable.
    [Fact]
    public async Task InstallRecordsResolvedRevisionSeparatelyFromRequestedReference()
    {
        _fetcher.Enqueue("deadbeefcafe", PluginContent("hello-world"));

        await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
            Reference = "main",
        });

        var record = _store.Find("hello-world")!;
        Assert.Equal("main", record.Reference);
        Assert.Equal("deadbeefcafe", record.ResolvedVersion);
        Assert.NotEqual(record.Reference, record.ResolvedVersion);
    }

    // AC2 - the update preference exists and defaults to ENABLED (pinning is opt-in).
    [Fact]
    public async Task InstallDefaultsUpdatePreferenceToEnabled()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world"));

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
        });

        Assert.True(result.Plugin!.UpdatesEnabled);
        Assert.True(_store.Find("hello-world")!.UpdatesEnabled);
    }

    // AC2 - the preference is honoured when explicitly disabled at install time, and survives a
    // round trip through the state file rather than living only in memory.
    [Fact]
    public async Task InstallHonoursExplicitlyDisabledUpdatePreferenceAndPersistsIt()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world"));

        await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
            UpdatesEnabled = false,
        });

        var reread = new PluginStateStore(_root).Find("hello-world");
        Assert.NotNull(reread);
        Assert.False(reread!.UpdatesEnabled);
    }

    // AC3 - update re-resolves the source and replaces content when the preference is enabled.
    [Fact]
    public async Task UpdateReResolvesSourceAndReplacesContentWhenEnabled()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", "1.0.0", ("data.txt", "old")));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        _fetcher.Enqueue("v2", PluginContent("hello-world", "2.0.0", ("data.txt", "new")));
        var result = await _manager.UpdateAsync("hello-world");

        Assert.Equal(PluginOperationOutcome.Updated, result.Outcome);
        Assert.Equal("v1", result.PreviousVersion);
        Assert.Equal("v2", result.Plugin!.ResolvedVersion);
        Assert.Equal("2.0.0", result.Plugin.ManifestVersion);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "hello-world", "data.txt")));

        // The source it re-resolved must be the one recorded at install time.
        Assert.Equal(2, _fetcher.Calls.Count);
        Assert.Equal("https://example.com/hello.git", _fetcher.Calls[1].Source);
    }

    // AC3 - update on an unchanged source replaces nothing and reports it, so a scheduled update
    // over many plugins does not churn every directory on every run.
    [Fact]
    public async Task UpdateLeavesContentInPlaceWhenSourceResolvesToTheSameRevision()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", extra: ("data.txt", "content")));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        var dataPath = Path.Combine(_root, "hello-world", "data.txt");
        var writtenAt = File.GetLastWriteTimeUtc(dataPath);

        _fetcher.Enqueue("v1", PluginContent("hello-world", extra: ("data.txt", "content")));
        var result = await _manager.UpdateAsync("hello-world");

        Assert.Equal(PluginOperationOutcome.AlreadyCurrent, result.Outcome);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(dataPath));
    }

    // AC3 - THE PINNED CASE. A pinned plugin is left untouched even though the source has moved:
    // its content, its recorded version and its manifest version all stand, and the transport is
    // not even invoked.
    [Fact]
    public async Task UpdateLeavesPinnedPluginCompletelyUntouchedEvenWhenSourceMoved()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", "1.0.0", ("data.txt", "old")));
        await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
            UpdatesEnabled = false,
        });

        var fetchesBefore = _fetcher.Calls.Count;

        // Queue a genuinely newer revision: if the pin were ignored, this WOULD be applied, so the
        // test cannot pass merely because there was nothing to update to.
        _fetcher.Enqueue("v2", PluginContent("hello-world", "2.0.0", ("data.txt", "new")));
        var result = await _manager.UpdateAsync("hello-world");

        Assert.Equal(PluginOperationOutcome.SkippedPinned, result.Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "hello-world", "data.txt")));

        var record = _store.Find("hello-world")!;
        Assert.Equal("v1", record.ResolvedVersion);
        Assert.Equal("1.0.0", record.ManifestVersion);
        Assert.Equal(fetchesBefore, _fetcher.Calls.Count);
    }

    // AC2/AC3 - a plugin pinned after install is then skipped, so pinning does not require a
    // reinstall.
    [Fact]
    public async Task PinningAfterInstallCausesSubsequentUpdateToSkip()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", extra: ("data.txt", "old")));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        _manager.SetUpdatePreference("hello-world", updatesEnabled: false);

        _fetcher.Enqueue("v2", PluginContent("hello-world", extra: ("data.txt", "new")));
        var result = await _manager.UpdateAsync("hello-world");

        Assert.Equal(PluginOperationOutcome.SkippedPinned, result.Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "hello-world", "data.txt")));
    }

    // AC4 - remove deletes every file install materialised, and leaves unrelated content alone.
    // The user file sits INSIDE the plugin directory, which is the case a directory-wipe
    // implementation would destroy and a pattern match would probably miss.
    [Fact]
    public async Task RemoveDeletesEveryInstalledFileAndLeavesUnrelatedContentUntouched()
    {
        _fetcher.Enqueue("v1", PluginContent(
            "hello-world",
            extra:
            [
                ("skills/greet/SKILL.md", "# greet"),
                ("scripts/run.ps1", "Write-Host hi"),
            ]));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        var pluginDir = Path.Combine(_root, "hello-world");

        // Content the user added alongside the plugin, both at the root and nested inside a
        // directory the install created.
        var userNote = Path.Combine(pluginDir, "my-notes.md");
        File.WriteAllText(userNote, "local override");
        var userInsideInstalledDir = Path.Combine(pluginDir, "skills", "greet", "local.md");
        File.WriteAllText(userInsideInstalledDir, "mine");

        // Content belonging to an entirely different plugin.
        var otherDir = Path.Combine(_root, "other-plugin");
        Directory.CreateDirectory(otherDir);
        var otherFile = Path.Combine(otherDir, "keep.txt");
        File.WriteAllText(otherFile, "keep");

        var result = _manager.Remove("hello-world");

        Assert.Equal(PluginOperationOutcome.Removed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(pluginDir, ".botnexus-plugin", "plugin.json")));
        Assert.False(File.Exists(Path.Combine(pluginDir, "skills", "greet", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(pluginDir, "scripts", "run.ps1")));
        Assert.False(Directory.Exists(Path.Combine(pluginDir, "scripts")));

        Assert.True(File.Exists(userNote), "A user file at the plugin root must survive removal.");
        Assert.True(File.Exists(userInsideInstalledDir), "A user file inside an installed directory must survive removal.");
        Assert.Equal("keep", File.ReadAllText(otherFile));

        Assert.Null(_store.Find("hello-world"));
    }

    // AC4 - removal works from the RECORDED file set, not from whatever is in the directory. A
    // file added after install is not in the record, so it must survive even though it looks
    // exactly like plugin content; this is what a pattern-matching implementation gets wrong.
    [Fact]
    public async Task RemoveUsesTheRecordedFileSetRatherThanScanningTheDirectory()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", extra: ("skills/greet/SKILL.md", "# greet")));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        var recorded = _store.Find("hello-world")!.Files;
        Assert.Equal([".botnexus-plugin/plugin.json", "skills/greet/SKILL.md"], recorded);

        // Indistinguishable from installed content by shape, but absent from the record.
        var impostor = Path.Combine(_root, "hello-world", "skills", "extra", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(impostor)!);
        File.WriteAllText(impostor, "# added later");

        _manager.Remove("hello-world");

        Assert.True(File.Exists(impostor), "A file absent from the recorded set must not be removed.");
    }

    // AC4 - a plugin directory containing nothing but installed content is pruned entirely, so a
    // clean removal does not leave an empty husk behind.
    [Fact]
    public async Task RemovePrunesThePluginDirectoryWhenNothingElseRemains()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", extra: ("data.txt", "x")));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        _manager.Remove("hello-world");

        Assert.False(Directory.Exists(Path.Combine(_root, "hello-world")));
    }

    // AC5 - THE ALL-OR-NOTHING CASE. The transport writes some files and then faults; no plugin
    // directory may exist afterwards and nothing may be recorded as installed.
    [Fact]
    public async Task FailedInstallLeavesNoPartialPluginDirectory()
    {
        _fetcher.EnqueueFaulting(
            PluginContent(
                "hello-world",
                extra:
                [
                    ("skills/greet/SKILL.md", "# greet"),
                    ("scripts/run.ps1", "Write-Host hi"),
                ]),
            faultAfterFiles: 2);

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
            Name = "hello-world",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.NotEmpty(result.Errors);

        Assert.False(
            Directory.Exists(Path.Combine(_root, "hello-world")),
            "A faulted install must leave no plugin directory at all.");
        Assert.Null(_store.Find("hello-world"));

        // And the staging directory it used is gone too - a failed install leaks nothing.
        Assert.All(_fetcher.StagingDirectories, dir => Assert.False(Directory.Exists(dir)));
    }

    // AC5 - content that clones successfully but is not a plugin is also all-or-nothing: a
    // missing manifest is caught in staging, so the destination is never created.
    [Fact]
    public async Task InstallOfContentWithoutAManifestLeavesNoPluginDirectory()
    {
        _fetcher.Enqueue("v1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "not a plugin",
        });

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
            Name = "hello-world",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_root, "hello-world")));
        Assert.Empty(_store.Read());
    }

    // AC5 - an install whose fetched manifest declares a different name than the caller asked for
    // is rejected before promotion, so content never lands under a name the caller did not request.
    [Fact]
    public async Task InstallRejectsContentWhoseManifestNameDoesNotMatchTheRequest()
    {
        _fetcher.Enqueue("v1", PluginContent("something-else"));

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/hello.git",
            Name = "hello-world",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, e => e.Message.Contains("something-else", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_root, "something-else")));
        Assert.False(Directory.Exists(Path.Combine(_root, "hello-world")));
    }

    // AC5 - a failed UPDATE must not destroy the working copy either. The previous content is
    // deleted only after the replacement has been fetched and validated.
    [Fact]
    public async Task FailedUpdateLeavesThePreviouslyInstalledContentIntact()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world", "1.0.0", ("data.txt", "old")));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        _fetcher.EnqueueFaulting(PluginContent("hello-world", "2.0.0", ("data.txt", "new")), faultAfterFiles: 1);
        var result = await _manager.UpdateAsync("hello-world");

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "hello-world", "data.txt")));
        Assert.Equal("v1", _store.Find("hello-world")!.ResolvedVersion);
    }

    // Installing over an existing plugin is refused rather than silently overwriting it, because
    // the existing record is the only thing that knows which files the previous install wrote.
    [Fact]
    public async Task InstallingAnAlreadyInstalledPluginIsRefused()
    {
        _fetcher.Enqueue("v1", PluginContent("hello-world"));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        _fetcher.Enqueue("v2", PluginContent("hello-world"));
        var result = await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Equal("v1", _store.Find("hello-world")!.ResolvedVersion);
    }

    [Fact]
    public async Task UpdateAndRemoveReportFailureForAnUninstalledPlugin()
    {
        var update = await _manager.UpdateAsync("absent");
        var remove = _manager.Remove("absent");

        Assert.Equal(PluginOperationOutcome.Failed, update.Outcome);
        Assert.Equal(PluginOperationOutcome.Failed, remove.Outcome);
    }

    [Fact]
    public async Task ListReturnsEveryInstalledPlugin()
    {
        _fetcher.Enqueue("v1", PluginContent("alpha"));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/alpha.git" });
        _fetcher.Enqueue("v1", PluginContent("beta"));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/beta.git" });

        Assert.Equal(["alpha", "beta"], _manager.List().Select(p => p.Name));
    }

    // The transport's git metadata is an artefact of cloning, not plugin content: copying it in
    // would make every plugin directory a nested repository and pollute the removal manifest.
    [Fact]
    public async Task InstallDoesNotMaterialiseTheTransportsGitMetadata()
    {
        _fetcher.Enqueue("v1", PluginContent(
            "hello-world",
            extra: [(".git/config", "[core]"), ("data.txt", "x")]));

        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/hello.git" });

        Assert.False(Directory.Exists(Path.Combine(_root, "hello-world", ".git")));
        Assert.DoesNotContain(_store.Find("hello-world")!.Files, f => f.StartsWith(".git/", StringComparison.Ordinal));
        Assert.Contains("data.txt", _store.Find("hello-world")!.Files);
    }
}
