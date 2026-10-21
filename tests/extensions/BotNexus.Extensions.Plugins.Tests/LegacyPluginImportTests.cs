using System.Text.Json;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Security;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>Pins legacy root skills import, without opting into any versioned plugin dialect.</summary>
public sealed class LegacyPluginImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-import-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly PluginLifecycleManager _manager;

    public LegacyPluginImportTests()
    {
        _store = new PluginStateStore(_root);
        _manager = new PluginLifecycleManager(_store, _fetcher);
    }

    [Fact]
    public async Task Install_LegacySkills_MetadataAndFormatSurviveRoundTrip()
    {
        _fetcher.Enqueue("v1", Content("""{"name":"example","displayName":"Example skills","version":"1.2.3","description":"portable","skills":"./skills/"}"""));
        var record = (await Install()).Plugin;
        record.ShouldNotBeNull();
        record.ManifestVersion.ShouldBe("1.2.3");
        JsonSerializer.SerializeToElement(record).GetProperty("manifestFormat").GetString().ShouldBe("Legacy");
        new PluginStateStore(_root).Find("example").ShouldNotBeNull().ManifestVersion.ShouldBe("1.2.3");
        record.Files.ShouldContain("plugin.json");
        ContentTrustCatalog.Verify(Path.Combine(_root, "example"),
            includeFile: ContentTrustCatalog.IncludeEveryFile, detectUnlistedFiles: true).Trusted.ShouldBeTrue();
        PluginSkillRootResolver.Resolve(_store).ShouldHaveSingleItem();
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
        _manager.Remove("example").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        Directory.Exists(Path.Combine(_root, "example")).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_ConventionalStringAndSingletonArray_Accepted()
    {
        foreach (var skills in new[] { "\"skills\"", "\"skills/\"", "\"./skills\"", "[\"./skills/\"]" })
        {
            _fetcher.Enqueue("v1", Content("{\"name\":\"example\",\"skills\":" + skills + "}"));
            (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
            PluginSkillRootResolver.Resolve(_store).ShouldHaveSingleItem();
            _manager.Remove("example").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        }
    }

    [Fact]
    public async Task Install_AdditiveEnhancement_InheritsIdentityAndVersion()
    {
        foreach (var path in new[] { ".botnexus-plugin/plugin.json", "botnexus-plugin.json" })
        {
            var files = Content();
            files[path] = "{\"description\":\"additional\",\"author\":{\"email\":\"author@example.com\"}}";
            _fetcher.Enqueue("v1", files);
            var record = (await Install()).Plugin;
            record.ShouldNotBeNull().ManifestVersion.ShouldBe("1.2.3");
            JsonSerializer.SerializeToElement(record).GetProperty("manifestFormat").GetString().ShouldBe("Legacy");
            record.Files.ShouldContain(path);
            _manager.Remove("example").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        }
    }

    [Fact]
    public async Task Install_EnhancementContradictions_NameBothSources()
    {
        foreach (var path in new[] { ".botnexus-plugin/plugin.json", "botnexus-plugin.json" })
        foreach (var enhancement in new[] { "{\"name\":\"other\"}", "{\"version\":\"9.0.0\"}", "{\"author\":{\"name\":\"other\"}}" })
        {
            var files = Content();
            files[path] = enhancement;
            _fetcher.Enqueue("v1", files);
            var result = await Install();
            Rejected(result);
            result.Errors.ShouldContain(error => error.Message.Contains("plugin.json", StringComparison.Ordinal)
                && error.Message.Contains(path, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Install_NestedEnhancementWins_ButMalformedSelectedNeverFallsBack()
    {
        var files = Content();
        files["botnexus-plugin.json"] = "{";
        files[".botnexus-plugin/plugin.json"] = "{\"description\":\"selected\"}";
        _fetcher.Enqueue("v1", files);
        (await Install()).Plugin.ShouldNotBeNull().ManifestVersion.ShouldBe("1.2.3");
        _manager.Remove("example");
        files["botnexus-plugin.json"] = "{}";
        foreach (var bad in new[] { "{", "[]", "{\"description\":42}", "{\"description\":\"a\",\"description\":\"b\"}" })
        {
            files[".botnexus-plugin/plugin.json"] = bad;
            _fetcher.Enqueue("v2", files);
            Rejected(await Install());
        }
    }

    [Fact]
    public async Task Install_UnsupportedBaselineCannotBeHiddenByEnhancement()
    {
        var files = Content("""{"name":"example","hooks":{},"agents":[],"commands":[],"mcpServers":{},"lspServers":{}}""");
        files[".botnexus-plugin/plugin.json"] = "{\"name\":\"example\"}";
        _fetcher.Enqueue("v1", files);
        Rejected(await Install(), "hooks", "agents", "commands", "mcpServers", "lspServers");
    }

    [Fact]
    public async Task Install_UnsupportedDiscoveredPaths_AllReported()
    {
        var files = Content();
        foreach (var path in new[] { "mcp.json", ".mcp.json", ".botnexus-plugin/mcp.json", "com.github.copilot/config.json", "agents/worker.json", "hooks/hooks.json", "commands/run.md" })
            files[path] = "{}";
        _fetcher.Enqueue("v1", files);
        Rejected(await Install(), "mcp.json", ".mcp.json", ".botnexus-plugin/mcp.json", "com.github.copilot", "agents", "hooks", "commands");
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Install_InvalidDialectMetadataAndSkills_FailClosed()
    {
        foreach (var manifest in new[]
        {
            "{", "[]", "{\"name\":42}", "{\"name\":\"example\",\"name\":\"other\"}",
            "{\"name\":\"example\",\"$schema\":\"https://example.com/v1\"}",
            "{\"name\":\"example\",\"schemaVersion\":\"1.0\"}",
            "{\"name\":\"example\",\"displayName\":42}",
            "{\"name\":\"example\",\"futureBehavior\":{}}",
        })
        {
            _fetcher.Enqueue("v1", Content(manifest));
            Rejected(await Install());
        }
        foreach (var skills in new[] { "[]", "null", "\"custom\"", "[\"skills\",\"other\"]", "\"../skills\"" })
        {
            _fetcher.Enqueue("v1", Content("{\"name\":\"example\",\"skills\":" + skills + "}"));
            Rejected(await Install(), "skills");
        }
    }

    [Fact]
    public async Task Install_EnhancementCannotAddOrHideBehavior()
    {
        foreach (var declaration in new[] { "\"hooks\":\"./custom.json\"", "\"agents\":[]", "\"commands\":[]", "\"mcpServers\":\"./custom.json\"", "\"skills\":[]", "\"skills\":[\"custom\"]" })
        {
            var files = Content();
            files[".botnexus-plugin/plugin.json"] = "{" + declaration + "}";
            _fetcher.Enqueue("v1", files);
            Rejected(await Install(), declaration.Split('"')[1]);
        }
    }

    [Fact]
    public async Task Update_RejectionIsAtomic_SuccessAndRemovalPreserveLocalOwnership()
    {
        _fetcher.Enqueue("v1", Content());
        (await Install()).Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var receipt = JsonSerializer.Serialize(_store.Find("example"));
        var local = Path.Combine(_root, "example", "skills", "local.txt");
        File.WriteAllText(local, "local");
        var bad = Content();
        bad["mcp.json"] = "{}";
        _fetcher.Enqueue("v2", bad);
        var rejected = await _manager.UpdateAsync("example");
        rejected.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        rejected.Errors.ShouldContain(error => error.Field == "mcp.json");
        JsonSerializer.Serialize(_store.Find("example")).ShouldBe(receipt);
        var next = Content("{\"name\":\"example\",\"version\":\"2.0.0\"}");
        next["botnexus-plugin.json"] = "{\"description\":\"enhanced\"}";
        _fetcher.Enqueue("v3", next);
        (await _manager.UpdateAsync("example")).Plugin.ShouldNotBeNull().ManifestVersion.ShouldBe("2.0.0");
        _manager.Remove("example").Outcome.ShouldBe(PluginOperationOutcome.Removed);
        _store.Read().ShouldBeEmpty();
        File.ReadAllText(local).ShouldBe("local");
        File.Exists(Path.Combine(_root, "example", "plugin.json")).ShouldBeFalse();
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }

    [Fact]
    public async Task Install_RootAndEnhancementLinksAndAliases_RejectBeforePromotion()
    {
        foreach (var path in new[] { "plugin.json", "botnexus-plugin.json", ".botnexus-plugin/plugin.json", "skills/greet/dangling.txt" })
        {
            _fetcher.Enqueue("v1", Content());
            var manager = new PluginLifecycleManager(_store, new MutatingFetcher(_fetcher, staging =>
            {
                var link = Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(link).ShouldNotBeNull());
                if (File.Exists(link)) File.Delete(link);
                File.CreateSymbolicLink(link, Path.Combine(staging, "absent"));
            }));
            Rejected(await manager.InstallAsync(new PluginInstallRequest { Source = "fake" }), path);
        }
        foreach (var path in new[] { "Plugin.json", "Mcp.json", "COM.GITHUB.COPILOT/config.json", ".botnexus-plugin/Plugin.json" })
        {
            var files = Content();
            if (path == "Plugin.json") files.Remove("plugin.json");
            files[path] = "{}";
            _fetcher.Enqueue("v1", files);
            Rejected(await Install(), path.Split('/')[0]);
        }
    }

    private sealed class MutatingFetcher(IPluginSourceFetcher inner, Action<string> mutate) : IPluginSourceFetcher
    {
        public async Task<PluginFetchResult> FetchAsync(string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            var result = await inner.FetchAsync(source, reference, stagingDirectory, cancellationToken);
            mutate(stagingDirectory);
            return result;
        }
    }

    private Task<PluginOperationResult> Install() => _manager.InstallAsync(new PluginInstallRequest { Source = "fake" });
    private void Rejected(PluginOperationResult result, params string[] fields)
    {
        result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        foreach (var field in fields) result.Errors.ShouldContain(error => error.Field.Contains(field, StringComparison.Ordinal));
        _store.Read().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_root, "example")).ShouldBeFalse();
        _fetcher.StagingDirectories.ShouldAllBe(path => !Directory.Exists(path));
    }
    private static Dictionary<string, string> Content(string? manifest = null) => new(StringComparer.Ordinal)
    {
        ["plugin.json"] = manifest ?? "{\"name\":\"example\",\"version\":\"1.2.3\",\"author\":{\"name\":\"author\"}}",
        ["skills/greet/SKILL.md"] = "# greet",
    };
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
