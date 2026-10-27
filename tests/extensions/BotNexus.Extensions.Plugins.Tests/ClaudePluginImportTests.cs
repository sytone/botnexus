using System.Text.Json;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>Pins the bounded, skills-only Claude import through the ordinary lifecycle.</summary>
public sealed class ClaudePluginImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-import-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly PluginLifecycleManager _manager;

    public ClaudePluginImportTests()
    {
        _store = new PluginStateStore(_root);
        _manager = new PluginLifecycleManager(_store, _fetcher);
    }

    [Fact]
    public async Task Install_ClaudeSkills_PreservesContentAndRecordsFormat()
    {
        _fetcher.Enqueue("v1", Content());
        var result = await Install();
        result.Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var record = new PluginStateStore(_root).Find("example");
        record.ShouldNotBeNull();
        JsonSerializer.SerializeToElement(record).GetProperty("manifestFormat").GetString().ShouldBe("Claude");
        record.ManifestVersion.ShouldBe("1.2.3");
        record.Files.ShouldContain(".claude-plugin/plugin.json");
        record.Files.ShouldNotContain(".botnexus-plugin/plugin.json");
        var skillRoot = PluginSkillRootResolver.Resolve(_store).ShouldHaveSingleItem();
        File.ReadAllText(Path.Combine(skillRoot, "greet", "SKILL.md")).ShouldBe("# greet");
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
        _manager.Remove("example").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        _store.Read().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_root, "example")).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_ExplicitConventionalSkills_AreDiscovered()
    {
        _fetcher.Enqueue("v1", Content("{\"name\":\"example\",\"skills\":\"./skills\"}"));
        (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        PluginSkillRootResolver.Resolve(_store).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Install_UnsupportedDeclarations_ReportEveryFieldBeforePromotion()
    {
        _fetcher.Enqueue("v1", Content("""{"name":"example","hooks":{"Stop":[]},"commands":["./cmd"],"agents":"./other","mcpServers":{"server":{"command":"never-run"}},"lspServers":{}}"""));
        var result = await Install();
        AssertRejected(result, "hooks", "commands", "agents", "mcpServers", "lspServers");
    }

    [Fact]
    public async Task Install_ConventionalUnsupportedContent_ReportEveryDiscoveredPath()
    {
        var files = Content();
        files["hooks/hooks.json"] = "{}";
        files["commands/run.md"] = "run";
        files["agents/worker.json"] = "{\"id\":\"worker\"}";
        files[".mcp.json"] = "{\"mcpServers\":{\"bad\":{\"command\":\"never-run\"}}}";
        files[".botnexus-plugin/mcp.json"] = "{}";
        _fetcher.Enqueue("v1", files);
        AssertRejected(await Install(), "hooks", "commands", "agents", ".mcp.json", ".botnexus-plugin/mcp.json");
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Install_NonconventionalAndEmptySkillDeclarations_RejectInsteadOfIgnoring()
    {
        foreach (var skills in new[] { "\"./custom\"", "[\"../escape\"]", "[]", "[\"./skills\",\"./extra\"]" })
        {
            _fetcher.Enqueue("v1", Content("{\"name\":\"example\",\"skills\":" + skills + "}"));
            AssertRejected(await Install(), "skills");
        }
    }

    [Fact]
    public async Task Install_DualManifests_SelectsNativeWholeManifestWithoutInheritance()
    {
        var files = Content();
        files[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"}";
        _fetcher.Enqueue("v1", files);
        var result = await Install();
        result.Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var record = _store.Find("example");
        record.ShouldNotBeNull();
        record.ManifestVersion.ShouldBeNull();
        JsonSerializer.SerializeToElement(record).GetProperty("manifestFormat").GetString().ShouldBe("Native");
    }

    [Fact]
    public async Task Install_DualIdentityOrBehaviorConflict_Rejects()
    {
        foreach (var claude in new[] { "{\"name\":\"other\"}", "{\"name\":\"example\",\"hooks\":{}}", "{\"name\":\"example\",\"skills\":\"./custom\"}" })
        {
            var files = Content(claude);
            files[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"}";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install());
        }
    }

    [Fact]
    public async Task Install_MalformedNativeOrClaude_NeverFallsBack()
    {
        foreach (var native in new[] { "{", "{\"name\":9}" })
        {
            var files = Content();
            files[".botnexus-plugin/plugin.json"] = native;
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install());
        }
        _fetcher.Enqueue("v1", Content("{"));
        AssertRejected(await Install());
    }

    [Fact]
    public async Task Install_ClaudeUnknownOrDuplicateFields_FailsClosed()
    {
        foreach (var json in new[] { "{\"name\":\"example\",\"futureBehavior\":{}}", "{\"name\":\"example\",\"name\":\"other\"}", "{\"name\":\"example\",\"skills\":\"./skills\",\"skills\":\"./custom\"}" })
        {
            _fetcher.Enqueue("v1", Content(json));
            AssertRejected(await Install());
        }
    }

    [Fact]
    public async Task Update_UnsupportedRevision_LeavesInstalledContentAndReceiptUntouched()
    {
        _fetcher.Enqueue("v1", Content());
        (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var receipt = JsonSerializer.Serialize(_store.Find("example"));
        var files = Content();
        files["agents/worker.json"] = "{\"id\":\"worker\"}";
        _fetcher.Enqueue("v2", files);
        var result = await _manager.UpdateAsync("example");
        result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        result.Errors.ShouldContain(error => error.Field.Contains("agents", StringComparison.Ordinal));
        JsonSerializer.Serialize(_store.Find("example")).ShouldBe(receipt);
        File.Exists(Path.Combine(_root, "example", "agents", "worker.json")).ShouldBeFalse();
        _fetcher.Enqueue("v3", Content("{\"name\":\"example\",\"version\":\"2.0.0\"}"));
        var updated = await _manager.UpdateAsync("example");
        updated.Outcome.ShouldBe(PluginOperationOutcome.Updated);
        updated.Plugin.ShouldNotBeNull();
        updated.Plugin.ManifestVersion.ShouldBe("2.0.0");
        JsonSerializer.SerializeToElement(updated.Plugin).GetProperty("manifestFormat").GetString().ShouldBe("Claude");
    }

    [Fact]
    public async Task Install_MissingManifestAndRequestedIdentityMismatch_Rejects()
    {
        _fetcher.Enqueue("v1", new Dictionary<string, string> { ["skills/greet/SKILL.md"] = "# greet" });
        AssertRejected(await Install());
        _fetcher.Enqueue("v1", Content());
        AssertRejected(await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/fake.git", Name = "other" }));
    }

    [Fact]
    public async Task Install_NativeOnlyBehavior_RemainsAccepted()
    {
        _fetcher.Enqueue("v1", new Dictionary<string, string>
        {
            [".botnexus-plugin/plugin.json"] = "{\"name\":\"example\",\"hooks\":\"./hooks/hooks.json\"}",
            ["hooks/hooks.json"] = "{}",
        });
        (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
    }

    [Fact]
    public async Task Install_DualNativeUnsupportedDeclarations_Rejects()
    {
        foreach (var declaration in new[] { "\"hooks\":\"./custom.json\"", "\"agents\":[\"./custom\"]", "\"commands\":[]", "\"mcpServers\":\"./custom.json\"", "\"skills\":[\"./custom\"]" })
        {
            var files = Content();
            files[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"," + declaration + "}";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install());
        }
    }

    [Fact]
    public async Task Install_MetadataAndSingleSkillArray_AcceptsButNestedUnknownOrDuplicatesReject()
    {
        foreach (var author in new[] { "{\"name\":\"author\",\"future\":true}", "{\"name\":\"author\",\"name\":\"other\"}" })
        {
            _fetcher.Enqueue("v1", Content("{\"name\":\"example\",\"author\":" + author + "}"));
            AssertRejected(await Install(), "author");
        }
        var files = Content("{\"name\":\"example\",\"skills\":[\"./skills\"],\"author\":{\"name\":\"author\"},\"keywords\":[\"test\"]}");
        _fetcher.Enqueue("v1", files);
        (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        File.ReadAllText(Path.Combine(_root, "example", ".claude-plugin", "plugin.json"))
            .ShouldBe(files[".claude-plugin/plugin.json"]);
    }

    [Fact]
    public async Task Update_FormatTransition_RefreshesReceiptAndPreservesLocalFiles()
    {
        var native = Content();
        native.Remove(".claude-plugin/plugin.json");
        native[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"}";
        _fetcher.Enqueue("v1", native);
        (await Install()).Plugin.ShouldNotBeNull().ManifestFormat.ShouldBe(PluginManifestFormat.Native);
        var localFile = Path.Combine(_root, "example", "skills", "local.txt");
        File.WriteAllText(localFile, "local");
        _fetcher.Enqueue("v2", Content());
        (await _manager.UpdateAsync("example")).Plugin.ShouldNotBeNull().ManifestFormat.ShouldBe(PluginManifestFormat.Claude);
        new PluginStateStore(_root).Find("example").ShouldNotBeNull().ManifestFormat.ShouldBe(PluginManifestFormat.Claude);
        File.Exists(Path.Combine(_root, "example", ".botnexus-plugin", "plugin.json")).ShouldBeFalse();
        _fetcher.Enqueue("v3", native);
        (await _manager.UpdateAsync("example")).Plugin.ShouldNotBeNull().ManifestFormat.ShouldBe(PluginManifestFormat.Native);
        File.Exists(Path.Combine(_root, "example", ".claude-plugin", "plugin.json")).ShouldBeFalse();
        _manager.Remove("example").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        File.ReadAllText(localFile).ShouldBe("local");
    }

    [Fact]
    public void Receipt_WithoutFormat_DefaultsToNative()
    {
        var record = JsonSerializer.Deserialize<InstalledPlugin>("{\"name\":\"example\",\"source\":\"fake\",\"resolvedVersion\":\"v1\",\"installedAtUtc\":\"2026-01-01T00:00:00Z\"}");
        record.ShouldNotBeNull().ManifestFormat.ShouldBe(PluginManifestFormat.Native);
    }

    [Fact]
    public async Task Install_BackslashBehaviorPaths_RejectBeforePromotion()
    {
        // On Windows a backslash is already a separator; only Linux can materialise this attack.
        if (OperatingSystem.IsWindows()) return;
        foreach (var path in new[] { @"agents\worker.json", @".botnexus-plugin\mcp.json", @"..\escape.txt" })
        {
            var files = Content();
            files[path] = "{}";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install(), path);
        }
    }

    [Fact]
    public async Task Install_AmbiguousPortableNames_RejectBeforePromotion()
    {
        foreach (var path in new[] { "AGENTS/worker.json", "skills/greet/aux.txt" })
        {
            var files = Content();
            files[path] = "{}";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install(), path.Split('/')[0]);
        }
        if (OperatingSystem.IsWindows()) return;
        var collision = Content();
        collision["skills/greet/skill.md"] = "collision";
        _fetcher.Enqueue("v1", collision);
        AssertRejected(await Install(), "skills");
    }

    [Fact]
    public async Task Install_LinkedContentAndMetadata_RejectWithoutFollowingLinks()
    {
        foreach (var linkPath in new[]
        {
            "skills/greet/host.txt", "skills/linked", ".claude-plugin/plugin.json",
            ".claude-plugin", ".botnexus-plugin/plugin.json", ".botnexus-plugin",
            "dangling.txt", "cycle",
        })
        {
            var files = Content();
            if (linkPath.StartsWith(".botnexus-plugin", StringComparison.Ordinal))
                files[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"}";
            _fetcher.Enqueue("v1", files);
            var host = Path.Combine(_root, "host");
            Directory.CreateDirectory(host);
            var hostFile = Path.Combine(host, "secret.txt");
            File.WriteAllText(hostFile, "host-secret");
            var fetcher = new MutatingFetcher(_fetcher, staging =>
            {
                var link = Path.Combine(staging, linkPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(link)) File.Delete(link);
                if (Directory.Exists(link)) Directory.Delete(link, recursive: true);
                if (linkPath is "skills/linked" or ".claude-plugin" or ".botnexus-plugin" or "cycle")
                    Directory.CreateSymbolicLink(link, linkPath == "cycle" ? staging : host);
                else
                    File.CreateSymbolicLink(link, linkPath == "dangling.txt" ? Path.Combine(host, "absent") : hostFile);
            });
            var manager = new PluginLifecycleManager(_store, fetcher);
            // Link creation failures propagate from the fetcher as source failures, and must NOT pass this assertion.
            AssertRejected(await manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/fake.git" }), linkPath);
            File.ReadAllText(hostFile).ShouldBe("host-secret");
        }
    }

    [Fact]
    public async Task Install_DanglingClaudeManifestAlongsideNative_RejectsInsteadOfNativeFallback()
    {
        var files = Content();
        files.Remove(".claude-plugin/plugin.json");
        files[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"}";
        _fetcher.Enqueue("v1", files);
        var fetcher = new MutatingFetcher(_fetcher, staging =>
        {
            var metadata = Path.Combine(staging, ".claude-plugin");
            Directory.CreateDirectory(metadata);
            File.CreateSymbolicLink(Path.Combine(metadata, "plugin.json"), Path.Combine(staging, "absent"));
        });
        AssertRejected(await new PluginLifecycleManager(_store, fetcher)
            .InstallAsync(new PluginInstallRequest { Source = "https://example.com/fake.git" }), ".claude-plugin/plugin.json");
    }

    [Fact]
    public async Task Update_LinkedRevision_PreservesInstalledContentAndReceipt()
    {
        _fetcher.Enqueue("v1", Content());
        (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var receipt = JsonSerializer.Serialize(_store.Find("example"));
        _fetcher.Enqueue("v2", Content());
        var fetcher = new MutatingFetcher(_fetcher, staging =>
            File.CreateSymbolicLink(Path.Combine(staging, "skills", "greet", "linked.txt"),
                Path.Combine(staging, "skills", "greet", "SKILL.md")));
        var result = await new PluginLifecycleManager(_store, fetcher).UpdateAsync("example");
        result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        result.Errors.ShouldContain(error => error.Field == "skills/greet/linked.txt");
        JsonSerializer.Serialize(_store.Find("example")).ShouldBe(receipt);
        File.ReadAllText(Path.Combine(_root, "example", "skills", "greet", "SKILL.md")).ShouldBe("# greet");
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    private sealed class MutatingFetcher(IPluginSourceFetcher inner, Action<string> mutate) : IPluginSourceFetcher
    {
        public async Task<PluginFetchResult> FetchAsync(string source, string? reference, string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.FetchAsync(source, reference, stagingDirectory, cancellationToken);
            mutate(stagingDirectory);
            return result;
        }
    }

    private Task<PluginOperationResult> Install() => _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/fake.git" });

    private void AssertRejected(PluginOperationResult result, params string[] fields)
    {
        result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        foreach (var field in fields)
            result.Errors.ShouldContain(error => error.Field.Contains(field, StringComparison.Ordinal));
        _store.Read().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_root, "example")).ShouldBeFalse();
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    private static Dictionary<string, string> Content(string? manifest = null) => new(StringComparer.Ordinal)
    {
        [".claude-plugin/plugin.json"] = manifest ?? "{\"name\":\"example\",\"version\":\"1.2.3\"}",
        ["skills/greet/SKILL.md"] = "# greet",
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
