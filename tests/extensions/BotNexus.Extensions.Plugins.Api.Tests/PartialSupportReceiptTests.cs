using System.Text.Json;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BotNexus.Extensions.Plugins.Api.Tests;

public sealed class PartialSupportReceiptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partial-api-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallUpdateAndInspection_KeepBoundedSanitizedWarnings()
    {
        var fetcher = new MixedFetcher();
        var manager = new PluginLifecycleManager(new PluginStateStore(_root), fetcher);
        var installed = (await PluginsEndpointContributor.Install(new PluginInstallRequest
        {
            Source = "https://example.invalid/repo.git",
        }, manager, CancellationToken.None)).ShouldBeOfType<Ok<PluginOperationReceipt>>().Value.ShouldNotBeNull();
        installed.SkillsOnly.ShouldBe(true);
        installed.SkippedComponents.ShouldBe(["agents", "mcp"]);
        installed.Warnings.Count.ShouldBe(3);
        JsonSerializer.Serialize(installed).ShouldNotContain("payload-secret");
        JsonSerializer.Serialize(installed).ShouldNotContain("never-fetch");
        var listed = PluginsEndpointContributor.List(_root).ShouldBeOfType<Ok<IReadOnlyList<PluginPortalRow>>>()
            .Value.ShouldNotBeNull().ShouldHaveSingleItem();
        listed.Warnings.ShouldBe(installed.Warnings);
        var found = PluginsEndpointContributor.Get("mixed", _root).ShouldBeOfType<Ok<PluginPortalRow>>().Value.ShouldNotBeNull();
        found.SkillsOnly.ShouldBeTrue();
        found.SkippedComponents.ShouldBe(installed.SkippedComponents);
        var updated = (await PluginsEndpointContributor.Update("mixed", manager, CancellationToken.None))
            .ShouldBeOfType<Ok<PluginOperationReceipt>>().Value.ShouldNotBeNull();
        updated.Outcome.ShouldBe(PluginOperationOutcome.Updated);
        updated.SkillsOnly.ShouldBe(true);
        updated.SkippedComponents.ShouldBeEmpty();
        updated.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void PersistedUntrustedCategoryText_CannotBecomePublicWarningText()
    {
        var record = new InstalledPlugin
        {
            Name = "mixed", Source = "https://example.invalid/repo.git", ResolvedVersion = "v1",
            InstalledAtUtc = DateTimeOffset.UtcNow, ManifestFormat = PluginManifestFormat.Claude,
            SkippedComponents = Enumerable.Repeat("payload-secret\nhttps://never-fetch.invalid", 100).Append("agents").ToArray(),
        };
        var receipt = PluginOperationReceipt.FromResult(PluginLifecycleOperation.Install,
            new PluginOperationResult { Name = "mixed", Outcome = PluginOperationOutcome.Installed, Plugin = record });
        receipt.SkippedComponents.ShouldBe(["agents"]);
        receipt.Warnings.Count.ShouldBe(2);
        string.Join(" ", receipt.Warnings).ShouldNotContain("payload-secret");
        string.Join(" ", receipt.Warnings).ShouldNotContain("never-fetch");
    }

    private sealed class MixedFetcher : IPluginSourceFetcher
    {
        private int _calls;
        public Task<PluginFetchResult> FetchAsync(string source, string? reference, string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            var manifest = Path.Combine(stagingDirectory, "plugin.json");
            File.WriteAllText(manifest, _calls == 1
                ? """{"name":"mixed","agents":[],"mcpServers":{"server":{"command":"payload-secret","url":"https://never-fetch.invalid"}}}"""
                : """{"name":"mixed"}""");
            var skills = Path.Combine(stagingDirectory, "skills", "helper");
            Directory.CreateDirectory(skills);
            File.WriteAllText(Path.Combine(skills, "SKILL.md"), "---\nname: helper\ndescription: Help.\n---\nHelp.");
            return Task.FromResult(new PluginFetchResult("v" + _calls));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
