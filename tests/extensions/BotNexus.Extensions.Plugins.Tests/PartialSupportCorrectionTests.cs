using System.Text.Json;
using BotNexus.Extensions.Skills;
using BotNexus.Extensions.Skills.Security;
using BotNexus.Extensions.Plugins.Agents;
using NSubstitute;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

public sealed class PartialSupportCorrectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partial-correction", Guid.NewGuid().ToString("N"));
    private readonly FakePluginSourceFetcher _fetcher = new();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegacyMcpOnlyMetadata_UsesRootEnhancementWhenPresentAndUpdates(bool selectedRoot, bool rootEnhancement)
    {
        var files = Content("plugin.json");
        files[".botnexus-plugin/mcp.json"] = "opaque MCP";
        if (rootEnhancement) files["botnexus-plugin.json"] = """{"version":"1.0.0"}""";
        if (selectedRoot) files[".claude-plugin/marketplace.json"] = Catalog;
        _fetcher.Enqueue("v1", files);
        var manager = Manager();
        var installed = await manager.InstallAsync(Request(selectedRoot));
        installed.Outcome.ShouldBe(PluginOperationOutcome.Installed);
        installed.Plugin.ShouldNotBeNull().ManifestVersion.ShouldBe(rootEnhancement ? "1.0.0" : null);
        installed.Plugin.ShouldNotBeNull().SkippedComponents.ShouldBe(["agents", "mcp"]);
        File.ReadAllText(Path.Combine(_root, "sample", ".botnexus-plugin/mcp.json")).ShouldBe("opaque MCP");
        files[".botnexus-plugin/plugin.json"] = """{"version":"2.0.0"}""";
        files["botnexus-plugin.json"] = "{";
        _fetcher.Enqueue("v2", files);
        (await Manager().UpdateAsync("sample")).Plugin.ShouldNotBeNull().ManifestVersion.ShouldBe("2.0.0");
        var prior = JsonSerializer.Serialize(new PluginStateStore(_root).Find("sample"));
        files[".botnexus-plugin/plugin.json"] = "{";
        files["botnexus-plugin.json"] = "{}";
        _fetcher.Enqueue("v3", files);
        (await Manager().UpdateAsync("sample")).Outcome.ShouldBe(PluginOperationOutcome.Failed);
        JsonSerializer.Serialize(new PluginStateStore(_root).Find("sample")).ShouldBe(prior);
        File.ReadAllText(Path.Combine(_root, "sample", ".botnexus-plugin/plugin.json")).ShouldBe("""{"version":"2.0.0"}""");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"other\"}")]
    [InlineData("{\"version\":1}")]
    public async Task LegacyPresentNestedManifest_NeverFallsBack(string invalid)
    {
        var files = Content("plugin.json");
        files[".botnexus-plugin/mcp.json"] = "opaque";
        files[".botnexus-plugin/plugin.json"] = invalid;
        files["botnexus-plugin.json"] = "{}";
        _fetcher.Enqueue("v1", files);
        (await Manager().InstallAsync(Request(false))).Outcome.ShouldBe(PluginOperationOutcome.Failed);
        new PluginStateStore(_root).Read().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("link")]
    public async Task LegacyPresentNestedManifestEntry_RejectsBeforeFallback(string entry)
    {
        var files = Content("plugin.json");
        files["botnexus-plugin.json"] = "{}";
        _fetcher.Enqueue("v1", files);
        var manager = new PluginLifecycleManager(new PluginStateStore(_root), new MutatingFetcher(_fetcher, directory =>
        {
            var metadata = Path.Combine(directory, ".botnexus-plugin");
            Directory.CreateDirectory(metadata);
            var manifest = Path.Combine(metadata, "plugin.json");
            if (entry == "directory") Directory.CreateDirectory(manifest);
            else File.CreateSymbolicLink(manifest, Path.Combine(directory, "absent"));
        }));
        var result = await manager.InstallAsync(Request(false));
        result.Outcome.ShouldBe(PluginOperationOutcome.Failed);
        result.Errors.ShouldContain(error => error.Field.Contains(".botnexus-plugin/plugin.json", StringComparison.Ordinal));
        result.Errors.ShouldNotContain(error => error.Field == "source");
    }

    [Theory]
    [InlineData(".botnexus-plugin/plugin.json")]
    [InlineData(".claude-plugin/plugin.json")]
    [InlineData("plugin.json")]
    public async Task SelectedRootComponents_StayInactiveAcrossReload(string manifest)
    {
        var files = Content(manifest);
        files[".claude-plugin/marketplace.json"] = Catalog;
        files[".mcp.json"] = "opaque";
        files["commands/run.md"] = "inert";
        _fetcher.Enqueue("v1", files);
        var result = await Manager().InstallAsync(Request(true));
        result.Outcome.ShouldBe(PluginOperationOutcome.Installed);
        result.Plugin.ShouldNotBeNull().SkillsOnly.ShouldBeTrue();
        result.Plugin.ShouldNotBeNull().SkippedComponents.ShouldBe(["agents", "commands", "mcp"]);
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
        new PluginStateStore(_root).Find("sample").ShouldNotBeNull().SkillsOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task FormatTransitions_RefreshPolicyAndAgentActivation()
    {
        var files = Content(".botnexus-plugin/plugin.json");
        _fetcher.Enqueue("v1", files);
        (await Manager().InstallAsync(Request(false))).Plugin.ShouldNotBeNull().SkillsOnly.ShouldBeFalse();
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).Count.ShouldBe(1);
        files[".claude-plugin/plugin.json"] = """{"name":"sample"}""";
        _fetcher.Enqueue("v2", files);
        (await Manager().UpdateAsync("sample")).Plugin.ShouldNotBeNull().SkillsOnly.ShouldBeTrue();
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
        files.Remove(".claude-plugin/plugin.json");
        _fetcher.Enqueue("v3", files);
        (await Manager().UpdateAsync("sample")).Plugin.ShouldNotBeNull().SkillsOnly.ShouldBeFalse();
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).Count.ShouldBe(1);
        files.Remove(".botnexus-plugin/plugin.json");
        files["plugin.json"] = """{"name":"sample"}""";
        _fetcher.Enqueue("v4", files);
        (await Manager().UpdateAsync("sample")).Plugin.ShouldNotBeNull().SkillsOnly.ShouldBeTrue();
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Claude", false)]
    [InlineData("Legacy", false)]
    [InlineData("Native", true)]
    public async Task OldFalseRecords_CannotEnableAgentPayloadReads(string format, bool marketplace)
    {
        var store = new PluginStateStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sample", "agents"));
        // Invalid descriptor content is never interpreted by this source for skills-only records.
        File.WriteAllText(Path.Combine(_root, "sample", "agents", "worker.json"), "not JSON");
        var selected = marketplace ? ",\"marketplacePluginName\":\"sample\"" : "";
        File.WriteAllText(store.StatePath, $$"""[{"name":"sample","source":"https://example.invalid/repo.git","resolvedVersion":"v1","manifestFormat":"{{format}}","skillsOnly":false,"installedAtUtc":"2026-01-01T00:00:00Z"{{selected}}}]""");
        store.Find("sample").ShouldNotBeNull().SkillsOnly.ShouldBeTrue();
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Legacy")]
    public async Task ForeignPolicy_DoesNotOpenDescriptorPayload(string format)
    {
        var fs = NSubstitute.Substitute.For<System.IO.Abstractions.IFileSystem>();
        var directory = NSubstitute.Substitute.For<System.IO.Abstractions.IDirectory>();
        var file = NSubstitute.Substitute.For<System.IO.Abstractions.IFile>();
        var path = NSubstitute.Substitute.For<System.IO.Abstractions.IPath>();
        fs.Directory.Returns(directory);
        fs.File.Returns(file);
        fs.Path.Returns(path);
        path.GetFullPath(_root).Returns(_root);
        path.Combine(_root, PluginStateStore.StateFileName).Returns(_root + "/state");
        directory.Exists(_root).Returns(true);
        file.Exists(_root + "/state").Returns(true);
        file.ReadAllText(_root + "/state").Returns($$"""[{"name":"sample","source":"https://example.invalid/repo.git","resolvedVersion":"v1","manifestFormat":"{{format}}","skillsOnly":false,"installedAtUtc":"2026-01-01T00:00:00Z"}]""");
        (await new PluginAgentConfigurationSource(_root, fileSystem: fs).LoadAsync()).ShouldBeEmpty();
        file.Received(1).ReadAllText(_root + "/state");
        file.DidNotReceive().ReadAllText(NSubstitute.Arg.Is<string>(value => value != _root + "/state"));
        directory.DidNotReceive().GetFiles(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<string>());
    }

    [Fact]
    public async Task ImportedSkills_KeepPrecedenceAndEnforcedTrust()
    {
        var files = Content("plugin.json");
        files["skills/helper/scripts/run.sh"] = "echo harmless";
        _fetcher.Enqueue("v1", files);
        (await Manager().InstallAsync(Request(false))).IsSuccess.ShouldBeTrue();
        var roots = PluginSkillRootResolver.Resolve(new PluginStateStore(_root));
        var skillDirectory = Path.Combine(_root, "sample", "skills", "helper");
        var bytes = System.Text.Encoding.UTF8.GetBytes("echo harmless");
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        File.WriteAllText(Path.Combine(skillDirectory, SkillTrustVerifier.CatalogFileName), $$"""{"version":1,"generatedAt":"2026-01-01T00:00:00Z","entries":[{"path":"scripts/run.sh","sha256":"{{digest}}","updatedAt":"2026-01-01T00:00:00Z"}]}""");
        SkillDiscovery.Discover(null, null, null, pluginSkillsDirs: roots, trustMode: SkillTrustMode.Enforce)
            .ShouldHaveSingleItem().Source.ShouldBe(SkillSource.Plugin);
        File.WriteAllText(Path.Combine(skillDirectory, "scripts", "run.sh"), "echo modified");
        SkillDiscovery.Discover(null, null, null, pluginSkillsDirs: roots, trustMode: SkillTrustMode.Enforce).ShouldBeEmpty();
        var global = Path.Combine(_root, "global");
        Directory.CreateDirectory(Path.Combine(global, "helper"));
        File.WriteAllText(Path.Combine(global, "helper", "SKILL.md"), "---\nname: helper\ndescription: Operator override.\n---\nOverride.");
        SkillDiscovery.Discover(global, null, null, pluginSkillsDirs: roots).ShouldHaveSingleItem().Source.ShouldBe(SkillSource.Global);
    }

    private const string Catalog = """{"name":"catalog","owner":{"name":"Example"},"plugins":[{"name":"sample","source":"./"}]}""";
    private static Dictionary<string, string> Content(string manifest) => new()
    {
        [manifest] = """{"name":"sample"}""",
        ["skills/helper/SKILL.md"] = "---\nname: helper\ndescription: Help.\n---\nHelp.",
        ["agents/worker.json"] = """{"id":"worker","displayName":"Worker","model":"gpt-5","provider":"github-copilot"}""",
    };
    private static PluginInstallRequest Request(bool selected) => new()
    {
        Source = "https://example.invalid/repo.git", MarketplacePluginName = selected ? "sample" : null,
    };
    private PluginLifecycleManager Manager() => new(new PluginStateStore(_root), _fetcher);
    private sealed class MutatingFetcher(IPluginSourceFetcher inner, Action<string> mutate) : IPluginSourceFetcher
    {
        public async Task<PluginFetchResult> FetchAsync(string source, string? reference, string directory, CancellationToken cancellationToken = default)
        {
            var result = await inner.FetchAsync(source, reference, directory, cancellationToken);
            mutate(directory);
            return result;
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
