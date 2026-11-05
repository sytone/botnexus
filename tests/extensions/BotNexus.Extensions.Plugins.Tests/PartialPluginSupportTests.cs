using System.Text.Json;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using BotNexus.Extensions.Skills;

namespace BotNexus.Extensions.Plugins.Tests;

public sealed class PartialPluginSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partial-plugin-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("plugin.json", false)]
    [InlineData(".claude-plugin/plugin.json", false)]
    [InlineData("plugin.json", true)]
    [InlineData(".claude-plugin/plugin.json", true)]
    public async Task MixedPackage_InstallReloadUpdateAndExactRemoval(string manifestPath, bool selected)
    {
        var fetcher = new FakePluginSourceFetcher();
        var store = new PluginStateStore(_root);
        var manager = new PluginLifecycleManager(store, fetcher);
        var prefix = selected ? "packages/common/" : "";
        var files = new Dictionary<string, string>
        {
            [prefix + manifestPath] = """{"name":"common","hooks":{"run":"https://secret:token@never-fetch.invalid"},"commands":[],"agents":{},"mcpServers":false,"lspServers":"opaque"}""",
            [prefix + "skills/prerequisite/SKILL.md"] = "---\nname: prerequisite\ndescription: A shared prerequisite.\n---\nUse the helper.",
            [prefix + "agents/worker.json"] = """{"id":"foreign-worker","name":"Foreign worker"}""",
            [prefix + ".botnexus-plugin/mcp.json"] = "not even JSON; never interpret",
            [prefix + "hooks/hooks.json"] = "not executable",
            [prefix + "commands/run.md"] = "not a command",
        };
        if (selected) files[".claude-plugin/marketplace.json"] = """{"name":"catalog","owner":{"name":"Example"},"plugins":[{"name":"common","source":"./packages/common"}]}""";
        fetcher.Enqueue("v1", files);
        var result = await manager.InstallAsync(new PluginInstallRequest { Source = "https://example.invalid/repo.git", MarketplacePluginName = selected ? "common" : null });
        result.Outcome.ShouldBe(PluginOperationOutcome.Installed);
        var record = new PluginStateStore(_root).Find("common").ShouldNotBeNull();
        record.SkillsOnly.ShouldBeTrue();
        record.SkippedComponents.ShouldBe(["agents", "commands", "hooks", "mcp", "lsp"]);
        record.Warnings.Count.ShouldBe(6);
        string.Join(" ", record.Warnings).ShouldNotContain("token");
        string.Join(" ", record.Warnings).ShouldNotContain("never-fetch");
        File.ReadAllText(Path.Combine(_root, "common", manifestPath)).ShouldBe(files[prefix + manifestPath]);
        (await new PluginAgentConfigurationSource(_root).LoadAsync()).ShouldBeEmpty();
        new PluginPortalProjector(new PluginStateStore(_root)).Find("common").ShouldNotBeNull().Warnings.ShouldBe(record.Warnings);
        var learning = new Dictionary<string, string>
        {
            ["plugin.json"] = """{"name":"learning"}""",
            ["skills/learn/SKILL.md"] = "---\nname: learn\ndescription: Learn using prerequisite.\n---\nUse prerequisite first.",
        };
        fetcher.Enqueue("learn-v1", learning);
        (await manager.InstallAsync(new PluginInstallRequest { Source = "https://example.invalid/learning.git" })).IsSuccess.ShouldBeTrue();
        SkillDiscovery.Discover(null, null, null, pluginSkillsDirs: PluginSkillRootResolver.Resolve(store))
            .Select(skill => skill.Name).Order().ShouldBe(["learn", "prerequisite"]);
        fetcher.Enqueue("v1", files);
        (await manager.UpdateAsync("common")).Outcome.ShouldBe(PluginOperationOutcome.AlreadyCurrent);
        manager.SetUpdatePreference("common", false);
        var calls = fetcher.Calls.Count;
        (await manager.UpdateAsync("common")).Plugin.ShouldNotBeNull().Warnings.ShouldBe(record.Warnings);
        fetcher.Calls.Count.ShouldBe(calls);
        manager.SetUpdatePreference("common", true);
        files[prefix + manifestPath] = """{"name":"common"}""";
        files.Remove(prefix + "agents/worker.json");
        files.Remove(prefix + ".botnexus-plugin/mcp.json");
        files.Remove(prefix + "hooks/hooks.json");
        files.Remove(prefix + "commands/run.md");
        fetcher.Enqueue("v2", files);
        var updated = await new PluginLifecycleManager(new PluginStateStore(_root), fetcher).UpdateAsync("common");
        updated.Outcome.ShouldBe(PluginOperationOutcome.Updated);
        updated.Plugin.ShouldNotBeNull().SkippedComponents.ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "common", "agents/worker.json")).ShouldBeFalse();
        var local = Path.Combine(_root, "common", "local.txt");
        File.WriteAllText(local, "keep");
        manager.Remove("common").IsSuccess.ShouldBeTrue();
        File.ReadAllText(local).ShouldBe("keep");
        File.Exists(Path.Combine(_root, "common", manifestPath)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Legacy")]
    [InlineData("Native")]
    public async Task OlderRecords_ForeignAgentsStayInactiveNativeRemainsCompatible(string format)
    {
        Directory.CreateDirectory(Path.Combine(_root, "old", "agents"));
        File.WriteAllText(Path.Combine(_root, "old", "agents", "worker.json"), """{"id":"worker","name":"Worker"}""");
        File.WriteAllText(Path.Combine(_root, PluginStateStore.StateFileName), $$"""[{"name":"old","source":"https://example.invalid/old.git","resolvedVersion":"v1","manifestFormat":"{{format}}","installedAtUtc":"2026-01-01T00:00:00Z"}]""");
        var descriptors = await new PluginAgentConfigurationSource(_root).LoadAsync();
        descriptors.Count.ShouldBe(format == "Native" ? 1 : 0);
        new PluginStateStore(_root).Find("old").ShouldNotBeNull().SkillsOnly.ShouldBe(format != "Native");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
