using System.Text.Json;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Skills;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>Explicit-identity, conventional-skills-only manifestless lifecycle boundary.</summary>
public sealed class ManifestlessClaudeImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manifestless-import-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly PluginLifecycleManager _manager;

    public ManifestlessClaudeImportTests()
    {
        _store = new PluginStateStore(_root);
        _manager = new PluginLifecycleManager(_store, _fetcher);
    }

    [Fact]
    public async Task Install_ExplicitIdentity_PersistsClaudeReceiptAndRemovesOnlyOwnedFiles()
    {
        _fetcher.Enqueue("v1", Skills());
        (await Install("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var record = new PluginStateStore(_root).Find("operator-name").ShouldNotBeNull();
        record.Name.ShouldBe("operator-name");
        record.ManifestFormat.ShouldBe(PluginManifestFormat.Claude);
        record.ManifestVersion.ShouldBeNull();
        record.Files.ShouldContain("skills/greet/SKILL.md");
        record.Files.ShouldNotContain(".botnexus-plugin/plugin.json");
        record.Files.ShouldNotContain(".claude-plugin/plugin.json");
        PluginSkillRootResolver.Resolve(_store).ShouldHaveSingleItem();
        var local = Path.Combine(_root, "operator-name", "local.txt");
        File.WriteAllText(local, "keep");
        _manager.Remove("operator-name").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        _store.Read().ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "operator-name", "skills", "greet", "SKILL.md")).ShouldBeFalse();
        File.ReadAllText(local).ShouldBe("keep");
    }

    [Fact]
    public async Task Install_UnknownOrInvalidIdentity_NeverInfersFromSource()
    {
        foreach (var name in new string?[] { null, "", " ", "../escape", "Upper", "bad/name" })
        {
            // A fresh manager prevents early name validation from leaving queued fetches behind.
            var fetcher = new FakePluginSourceFetcher();
            fetcher.Enqueue("v1", Skills());
            var result = await new PluginLifecycleManager(_store, fetcher).InstallAsync(
                new PluginInstallRequest { Source = "https://example.com/operator-name.git", Name = name });
            AssertRejected(result, "name");
            fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
        }
    }

    [Fact]
    public async Task Install_AbsentEmptyOrNoncanonicalSkills_Rejects()
    {
        foreach (var files in new[]
        {
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["README.md"] = "not a plugin" },
            new Dictionary<string, string> { ["SKILL.md"] = "loose skill" },
            new Dictionary<string, string> { ["skills/README.md"] = "no skills" },
            new Dictionary<string, string> { ["skills/greet/readme.md"] = "no SKILL.md" },
            new Dictionary<string, string> { ["skills/greet/deep/SKILL.md"] = "too deep" },
            new Dictionary<string, string> { ["Skills/greet/SKILL.md"] = "alias" },
            new Dictionary<string, string> { ["skills/greet/skill.md"] = "alias" },
        })
        {
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install("operator-name"), "skills");
        }
        _fetcher.Enqueue("v1", new Dictionary<string, string>());
        var empty = new MutatingFetcher(_fetcher, staging => Directory.CreateDirectory(Path.Combine(staging, "skills")));
        AssertRejected(await new PluginLifecycleManager(_store, empty).InstallAsync(
            new PluginInstallRequest { Source = "https://example.com/fake.git", Name = "operator-name" }), "skills");
    }

    [Fact]
    public async Task Install_UnsupportedDiscoveredPaths_ReportAllBeforePromotion()
    {
        var files = Skills();
        foreach (var path in new[] { "hooks/hooks.json", "commands/run.md", "agents/worker.json", "lsp/config.json", ".mcp.json", ".lsp.json", ".botnexus-plugin/mcp.json" })
            files[path] = "{}";
        _fetcher.Enqueue("v1", files);
        var result = await Install("operator-name");
        foreach (var field in new[] { "hooks", "commands", "agents", "lsp", ".mcp.json", ".lsp.json", ".botnexus-plugin/mcp.json" })
            AssertRejected(result, field);
    }

    [Fact]
    public async Task Install_PresentMalformedMetadata_NeverFallsBack()
    {
        foreach (var path in new[] { ".botnexus-plugin/plugin.json", ".claude-plugin/plugin.json" })
        foreach (var content in new[] { "{", "{\"name\":9}", "{\"name\":\"other\"}" })
        {
            var files = Skills();
            files[path] = content;
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install("operator-name"));
        }
    }

    [Fact]
    public async Task Install_LinksAndDanglingMetadata_RejectWithoutFollowing()
    {
        foreach (var path in new[] { "skills/greet/linked.txt", "skills/linked", ".botnexus-plugin/plugin.json", ".claude-plugin/plugin.json", ".botnexus-plugin", ".claude-plugin" })
        {
            _fetcher.Enqueue("v1", Skills());
            var fetcher = new MutatingFetcher(_fetcher, staging =>
            {
                var link = Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(link).ShouldNotBeNull());
                if (path is "skills/linked" or ".botnexus-plugin" or ".claude-plugin")
                    Directory.CreateSymbolicLink(link, Path.Combine(staging, "missing-directory"));
                else
                    File.CreateSymbolicLink(link, Path.Combine(staging, "missing-file"));
            });
            // A source error (including unavailable link privileges) must not satisfy this assertion.
            AssertRejected(await new PluginLifecycleManager(_store, fetcher).InstallAsync(
                new PluginInstallRequest { Source = "https://example.com/fake.git", Name = "operator-name" }), path);
        }
    }

    [Fact]
    public async Task Install_PortableAliases_Reject()
    {
        foreach (var path in new[] { "AGENTS/worker.json", "skills/greet/aux.txt" })
        {
            var files = Skills();
            files[path] = "bad";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install("operator-name"), path.Split('/')[0]);
        }
        if (OperatingSystem.IsWindows()) return;
        foreach (var path in new[] { "skills/greet/skill.md", @"agents\worker.json" })
        {
            var files = Skills();
            files[path] = "bad";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install("operator-name"), path);
        }
    }

    [Fact]
    public async Task Update_UsesRecordedIdentity_RejectsUnsafeRevisionAndRefreshesReceipt()
    {
        _fetcher.Enqueue("v1", Skills());
        (await Install("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var receipt = JsonSerializer.Serialize(_store.Find("operator-name"));
        var unsafeFiles = Skills();
        unsafeFiles["hooks/hooks.json"] = "{}";
        _fetcher.Enqueue("v2", unsafeFiles);
        var rejected = await _manager.UpdateAsync("operator-name");
        rejected.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        rejected.Errors.ShouldContain(error => error.Field == "hooks");
        JsonSerializer.Serialize(_store.Find("operator-name")).ShouldBe(receipt);
        File.ReadAllText(Path.Combine(_root, "operator-name", "skills", "greet", "SKILL.md")).ShouldBe("# greet");
        _fetcher.Enqueue("v3", new Dictionary<string, string> { ["skills/new/SKILL.md"] = "# new" });
        var updated = await _manager.UpdateAsync("operator-name");
        updated.Outcome.ShouldBe(PluginOperationOutcome.Updated);
        var record = new PluginStateStore(_root).Find("operator-name").ShouldNotBeNull();
        record.Name.ShouldBe("operator-name");
        record.ResolvedVersion.ShouldBe("v3");
        record.ManifestFormat.ShouldBe(PluginManifestFormat.Claude);
        File.Exists(Path.Combine(_root, "operator-name", "skills", "greet", "SKILL.md")).ShouldBeFalse();
        _fetcher.Calls.ShouldAllBe(call => call.Source == "https://example.com/fake-source-not-the-name.git");
        _manager.Remove("operator-name").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        Directory.Exists(Path.Combine(_root, "operator-name")).ShouldBeFalse();
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    [Fact]
    public async Task Install_MetadataBlockers_NeverBecomeManifestlessSuccess()
    {
        foreach (var path in new[] { ".botnexus-plugin", ".claude-plugin" })
        {
            var files = Skills();
            files[path] = "not a metadata directory";
            _fetcher.Enqueue("v1", files);
            AssertRejected(await Install("operator-name"));
        }
        foreach (var path in new[] { ".botnexus-plugin/plugin.json", ".claude-plugin/plugin.json" })
        {
            _fetcher.Enqueue("v1", Skills());
            var fetcher = new MutatingFetcher(_fetcher, staging =>
                Directory.CreateDirectory(Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar))));
            AssertRejected(await new PluginLifecycleManager(_store, fetcher).InstallAsync(
                new PluginInstallRequest { Source = "https://example.com/fake.git", Name = "operator-name" }));
        }
    }

    [Fact]
    public async Task Update_LinkOrRenamedManifest_PreservesReceiptAndContent()
    {
        _fetcher.Enqueue("v1", Skills());
        (await Install("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var receipt = JsonSerializer.Serialize(_store.Find("operator-name"));
        foreach (var metadataPath in new[] { ".botnexus-plugin/plugin.json", ".claude-plugin/plugin.json" })
        {
            var files = Skills();
            files[metadataPath] = "{\"name\":\"renamed\"}";
            _fetcher.Enqueue("v2", files);
            var result = await _manager.UpdateAsync("operator-name");
            result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
            result.Errors.ShouldContain(error => error.Field == "name");
            JsonSerializer.Serialize(_store.Find("operator-name")).ShouldBe(receipt);
        }
        _fetcher.Enqueue("v3", Skills());
        var fetcher = new MutatingFetcher(_fetcher, staging =>
            File.CreateSymbolicLink(Path.Combine(staging, "skills", "greet", "linked.txt"),
                Path.Combine(staging, "skills", "greet", "SKILL.md")));
        var linked = await new PluginLifecycleManager(_store, fetcher).UpdateAsync("operator-name");
        linked.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        linked.Errors.ShouldContain(error => error.Field == "skills/greet/linked.txt");
        JsonSerializer.Serialize(_store.Find("operator-name")).ShouldBe(receipt);
        File.ReadAllText(Path.Combine(_root, "operator-name", "skills", "greet", "SKILL.md")).ShouldBe("# greet");
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
        _manager.Remove("operator-name").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        Directory.Exists(Path.Combine(_root, "operator-name")).ShouldBeFalse();
    }

    [Fact]
    public async Task Update_FromManifestToManifestless_KeepsIdentityAndRefreshesFormat()
    {
        foreach (var metadataPath in new[] { ".botnexus-plugin/plugin.json", ".claude-plugin/plugin.json" })
        {
            var files = Skills();
            files[metadataPath] = JsonSerializer.Serialize(new { name = "operator-name", version = "1.0.0" });
            _fetcher.Enqueue("v1", files);
            (await Install("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Installed);
            _fetcher.Enqueue("v2", Skills());
            (await _manager.UpdateAsync("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Updated);
            var record = new PluginStateStore(_root).Find("operator-name").ShouldNotBeNull();
            record.ManifestFormat.ShouldBe(PluginManifestFormat.Claude);
            record.ManifestVersion.ShouldBeNull();
            record.Files.ShouldNotContain(metadataPath);
            File.Exists(Path.Combine(_root, "operator-name", metadataPath.Replace('/', Path.DirectorySeparatorChar))).ShouldBeFalse();
            _manager.Remove("operator-name").Outcome.ShouldBe(PluginOperationOutcome.Removed);
            Directory.Exists(Path.Combine(_root, "operator-name")).ShouldBeFalse();
        }
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    [Fact]
    public async Task Install_ManifestlessValidSkill_IsDiscoveredThroughRecordedRoots()
    {
        _fetcher.Enqueue("v1", new Dictionary<string, string>
        {
            ["skills/greet/SKILL.md"] = "---\nname: greet\ndescription: Greet the user when they say hello.\n---\n# Greeting\nSay hello.",
        });
        (await Install("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var roots = PluginSkillRootResolver.Resolve(new PluginStateStore(_root));
        roots.ShouldHaveSingleItem();
        var skill = SkillDiscovery.Discover(null, null, null, pluginSkillsDirs: roots).ShouldHaveSingleItem();
        skill.Name.ShouldBe("greet");
        skill.Description.ShouldBe("Greet the user when they say hello.");
        skill.Source.ShouldBe(SkillSource.Plugin);
        Path.GetFullPath(skill.SourcePath).ShouldBe(
            Path.GetFullPath(Path.Combine(_root, "operator-name", "skills", "greet")));
        skill.Content.ShouldContain("Say hello.");
        _manager.Remove("operator-name").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        SkillDiscovery.Discover(null, null, null,
            pluginSkillsDirs: PluginSkillRootResolver.Resolve(new PluginStateStore(_root))).ShouldBeEmpty();
        Directory.Exists(Path.Combine(_root, "operator-name")).ShouldBeFalse();
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    [Fact]
    public async Task Update_FromManifestlessToSameNameManifest_RefreshesReceipt()
    {
        foreach (var (metadataPath, format) in new[]
        {
            (".botnexus-plugin/plugin.json", PluginManifestFormat.Native),
            (".claude-plugin/plugin.json", PluginManifestFormat.Claude),
        })
        {
            _fetcher.Enqueue("v1", Skills());
            (await Install("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Installed);
            var original = new PluginStateStore(_root).Find("operator-name").ShouldNotBeNull();
            original.ManifestFormat.ShouldBe(PluginManifestFormat.Claude);
            original.ManifestVersion.ShouldBeNull();
            var files = new Dictionary<string, string>
            {
                [metadataPath] = JsonSerializer.Serialize(new { name = "operator-name", version = "2.0.0" }),
                ["skills/new/SKILL.md"] = "# new",
            };
            _fetcher.Enqueue("v2", files);
            (await _manager.UpdateAsync("operator-name")).Outcome.ShouldBe(PluginOperationOutcome.Updated);
            var record = new PluginStateStore(_root).Find("operator-name").ShouldNotBeNull();
            record.Name.ShouldBe("operator-name");
            record.ManifestFormat.ShouldBe(format);
            record.ManifestVersion.ShouldBe("2.0.0");
            record.ResolvedVersion.ShouldBe("v2");
            record.Files.OrderBy(path => path, StringComparer.Ordinal).ShouldBe(
                files.Keys.Append("trust.json").OrderBy(path => path, StringComparer.Ordinal));
            File.ReadAllText(Path.Combine(_root, "operator-name", metadataPath.Replace('/', Path.DirectorySeparatorChar)))
                .ShouldBe(files[metadataPath]);
            File.ReadAllText(Path.Combine(_root, "operator-name", "skills", "new", "SKILL.md")).ShouldBe("# new");
            File.Exists(Path.Combine(_root, "operator-name", "skills", "greet", "SKILL.md")).ShouldBeFalse();
            _manager.Remove("operator-name").Outcome.ShouldBe(PluginOperationOutcome.Removed);
            Directory.Exists(Path.Combine(_root, "operator-name")).ShouldBeFalse();
        }
        _fetcher.Calls.ShouldAllBe(call => call.Source == "https://example.com/fake-source-not-the-name.git");
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    [Fact]
    public async Task Install_NativeOnly_RemainsUnchanged()
    {
        _fetcher.Enqueue("v1", new Dictionary<string, string>
        {
            [".botnexus-plugin/plugin.json"] = "{\"name\":\"native-only\",\"hooks\":\"./hooks/hooks.json\"}",
            ["hooks/hooks.json"] = "{}",
        });
        var result = await Install(null);
        result.Outcome.ShouldBe(PluginOperationOutcome.Installed);
        result.Plugin.ShouldNotBeNull().ManifestFormat.ShouldBe(PluginManifestFormat.Native);
    }

    private Task<PluginOperationResult> Install(string? name) => _manager.InstallAsync(
        new PluginInstallRequest { Source = "https://example.com/fake-source-not-the-name.git", Name = name });

    private void AssertRejected(PluginOperationResult result, params string[] fields)
    {
        result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        foreach (var field in fields)
            result.Errors.ShouldContain(error => error.Field.Contains(field, StringComparison.OrdinalIgnoreCase));
        _store.Read().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_root, "operator-name")).ShouldBeFalse();
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    private static Dictionary<string, string> Skills() => new(StringComparer.Ordinal) { ["skills/greet/SKILL.md"] = "# greet" };

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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
