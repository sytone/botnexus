using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the plugin trust catalog (#2682): install records a SHA-256 catalog over the content it
/// actually materialised, and the Disabled / Warn / Enforce postures are all reachable and behave
/// differently from each other.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation test here modifies content AFTER a real install rather than hand-writing a
/// catalog with a wrong hash. A hand-written catalog proves the verifier compares strings; only a
/// post-install mutation proves the catalog install generated describes the bytes install wrote.
/// </para>
/// <para>
/// The three postures are asserted against the SAME mutation, so a difference in outcome can only
/// come from the mode. Asserting each posture against its own fixture would let two of them pass
/// for accidental reasons.
/// </para>
/// </remarks>
public sealed class PluginTrustCatalogTests : IDisposable
{
    private readonly string _root;
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly PluginLifecycleManager _manager;

    public PluginTrustCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-trust-tests", Guid.NewGuid().ToString("N"));
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

    private static Dictionary<string, string> PluginContent(string name, params (string Path, string Content)[] extra)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".botnexus-plugin/plugin.json"] = $$"""{ "name": "{{name}}" }""",
        };

        foreach (var (path, content) in extra)
        {
            files[path] = content;
        }

        return files;
    }

    private async Task<string> InstallAsync(string name = "demo")
    {
        _fetcher.Enqueue("a1b2c3d4", PluginContent(
            name,
            ("skills/demo/scripts/run.ps1", "Write-Output 'hello'"),
            ("README.md", "# demo")));

        var result = await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.invalid/demo.git" });
        Assert.Equal(PluginOperationOutcome.Installed, result.Outcome);

        return _manager.GetPluginDirectory(name);
    }

    // AC1 - install writes a catalog whose hashes describe the bytes on disk.
    [Fact]
    public async Task InstallGeneratesCatalogOverMaterialisedContent()
    {
        var directory = await InstallAsync();
        var catalogPath = Path.Combine(directory, ContentTrustCatalog.CatalogFileName);

        Assert.True(File.Exists(catalogPath), "install must record a trust catalog next to the content it materialised");

        var result = ContentTrustCatalog.Verify(
            directory,
            includeFile: ContentTrustCatalog.IncludeEveryFile,
            detectUnlistedFiles: true);

        Assert.True(result.Trusted, "freshly installed content must verify: " + string.Join("; ", result.Violations));
    }

    // AC5 - the catalog covers EVERY file install materialised, including the plugin's own manifest
    // inside a dot-directory. That file is the most security-relevant one in the tree; a catalog
    // that silently skipped it would leave the plugin's identity freely editable post-install.
    [Fact]
    public async Task CatalogCoversEveryMaterialisedFileIncludingTheManifest()
    {
        var directory = await InstallAsync();
        var record = _store.Find("demo");
        Assert.NotNull(record);

        var catalogued = ContentTrustCatalog
            .GenerateCatalog(directory, includeFile: ContentTrustCatalog.IncludeEveryFile)
            .Entries
            .Select(e => e.Path)
            .ToHashSet(StringComparer.Ordinal);

        // The catalog cannot hash itself - a self-referential entry is unverifiable by
        // construction - so it is excluded EXPLICITLY here rather than by weakening the assertion
        // to a subset check. Everything else install recorded must be covered.
        var uncatalogued = record.Files
            .Where(f => !string.Equals(f, ContentTrustCatalog.CatalogFileName, StringComparison.Ordinal))
            .Where(f => !catalogued.Contains(f))
            .ToList();

        Assert.True(uncatalogued.Count == 0, "uncatalogued installed files: " + string.Join(", ", uncatalogued));
        Assert.Contains(".botnexus-plugin/plugin.json", catalogued);
        Assert.Contains(ContentTrustCatalog.CatalogFileName, record.Files);
    }

    // AC5 - a file that appears after install is DETECTED, not silently ignored. A catalog that only
    // checked the files it already knew about would call a plugin trusted while it carried content
    // nothing installed.
    [Fact]
    public async Task UnlistedFileAddedAfterInstallIsDetected()
    {
        var directory = await InstallAsync();
        File.WriteAllText(Path.Combine(directory, "smuggled.ps1"), "Remove-Item -Recurse -Force /");

        var result = new PluginTrustGate(ContentTrustMode.Enforce).Verify(directory);

        Assert.False(result.Trusted);
        Assert.Contains(result.Violations, v => v.Contains("smuggled.ps1", StringComparison.Ordinal));
    }

    // A file DELETED after install is equally a modification of the plugin.
    [Fact]
    public async Task FileRemovedAfterInstallIsDetected()
    {
        var directory = await InstallAsync();
        File.Delete(Path.Combine(directory, "skills", "demo", "scripts", "run.ps1"));

        var result = new PluginTrustGate(ContentTrustMode.Enforce).Verify(directory);

        Assert.False(result.Trusted);
        Assert.Contains(result.Violations, v => v.StartsWith("Missing file:", StringComparison.Ordinal));
    }

    // AC3 - under Enforce, content modified after install is REFUSED and the refusal is logged.
    [Fact]
    public async Task EnforceRefusesAndLogsContentModifiedAfterInstall()
    {
        var directory = await InstallAsync();
        MutateScript(directory);

        var logger = new CapturingLogger<PluginTrustGate>();
        var allowed = new PluginTrustGate(ContentTrustMode.Enforce, logger: logger).Allow("demo", directory);

        Assert.False(allowed);

        var refusal = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("demo", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Hash mismatch", refusal.Message, StringComparison.Ordinal);
    }

    // AC4 - under Warn, the SAME modification is logged but permitted. A Warn that stayed silent
    // would be indistinguishable from Disabled and would be worth nothing.
    [Fact]
    public async Task WarnPermitsButLogsContentModifiedAfterInstall()
    {
        var directory = await InstallAsync();
        MutateScript(directory);

        var logger = new CapturingLogger<PluginTrustGate>();
        var allowed = new PluginTrustGate(ContentTrustMode.Warn, logger: logger).Allow("demo", directory);

        Assert.True(allowed);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("demo", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Hash mismatch", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    // AC2 - Disabled is reachable and genuinely verifies nothing: the same tampered content that
    // Enforce refuses is permitted with no log at all.
    [Fact]
    public async Task DisabledPermitsModifiedContentSilently()
    {
        var directory = await InstallAsync();
        MutateScript(directory);

        var logger = new CapturingLogger<PluginTrustGate>();
        var allowed = new PluginTrustGate(ContentTrustMode.Disabled, logger: logger).Allow("demo", directory);

        Assert.True(allowed);
        Assert.Empty(logger.Entries);
    }

    // Untampered content must be permitted under Enforce with no log, or the gate is simply a
    // blanket refusal and the three tests above prove nothing about verification.
    [Fact]
    public async Task EnforcePermitsUntamperedContentSilently()
    {
        var directory = await InstallAsync();

        var logger = new CapturingLogger<PluginTrustGate>();
        var allowed = new PluginTrustGate(ContentTrustMode.Enforce, logger: logger).Allow("demo", directory);

        Assert.True(allowed);
        Assert.Empty(logger.Entries);
    }

    // Update replaces content, so it must replace the catalog too. A stale catalog would make every
    // successfully updated plugin fail verification - an availability bug wearing a security hat.
    [Fact]
    public async Task UpdateRegeneratesTheCatalogForTheNewContent()
    {
        var directory = await InstallAsync();

        _fetcher.Enqueue("e5f6a7b8", PluginContent(
            "demo",
            ("skills/demo/scripts/run.ps1", "Write-Output 'v2'"),
            ("README.md", "# demo v2")));

        var updated = await _manager.UpdateAsync("demo");
        Assert.Equal(PluginOperationOutcome.Updated, updated.Outcome);

        var allowed = new PluginTrustGate(ContentTrustMode.Enforce).Allow("demo", directory);
        Assert.True(allowed, "an updated plugin must verify against the catalog the update recorded");
    }

    // A plugin installed before this feature existed has no catalog. Enforce must refuse it rather
    // than treat "nothing to compare against" as a pass.
    [Fact]
    public async Task MissingCatalogIsRefusedUnderEnforce()
    {
        var directory = await InstallAsync();
        File.Delete(Path.Combine(directory, ContentTrustCatalog.CatalogFileName));

        var logger = new CapturingLogger<PluginTrustGate>();
        var allowed = new PluginTrustGate(ContentTrustMode.Enforce, logger: logger).Allow("demo", directory);

        Assert.False(allowed);
        Assert.Contains(logger.Entries, e => e.Message.Contains("No trust catalog found", StringComparison.Ordinal));
    }

    /// <summary>
    /// Edits an installed script in place. The bytes change, the path does not - which is exactly
    /// the tamper a hash catalog exists to catch and a file listing does not.
    /// </summary>
    private static void MutateScript(string directory) =>
        File.WriteAllText(
            Path.Combine(directory, "skills", "demo", "scripts", "run.ps1"),
            "Write-Output 'tampered'");
}
