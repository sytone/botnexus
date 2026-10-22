using System.Text.Json;
#if !MARKETPLACE_NOHOST
using Shouldly;
#endif
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Security;
using BotNexus.Extensions.Skills;

namespace BotNexus.Extensions.Plugins.Tests;

// Also compiled directly into the focused no-host console harness. No gateway or test host.
public sealed class ClaudeMarketplaceSelectionTests
{
#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task RelativeSelection_LifecycleReceiptsTrustAndFailurePreservation()
    {
        foreach (var relative in new[] { "./", "./contained/subdir" })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root => Populate(root, relative);
            var installed = await fixture.Manager.InstallAsync(Request());
            Check(installed.Outcome == PluginOperationOutcome.Installed, "relative install: " + Describe(installed));
            var record = new PluginStateStore(fixture.Root).Find("chosen") ?? throw new Exception("missing persisted receipt");
            Check(JsonSerializer.Serialize(record).Contains("\"marketplacePluginName\":\"chosen\""), "selector persisted");
            Check(record.Source == "repository" && record.Reference == "branch" && record.ResolvedVersion == "revision-1", "source revision receipt");
            Check(record.Files.Contains("skills/greet/SKILL.md") && record.Files.Contains("trust.json"), "selected files recorded");
            if (relative != "./")
            {
                Check(!record.Files.Any(path => path.Contains("sibling") || path.Contains("marketplace.json") || path.StartsWith("contained/")), "sibling or repository metadata leaked");
            }
            var destination = fixture.Manager.GetPluginDirectory("chosen");
            Check(ContentTrustCatalog.Verify(destination, detectUnlistedFiles: true).Trusted, "selected content trusted");
            Discover(fixture, "greet");
            var receipt = JsonSerializer.Serialize(record);
            fixture.Fetcher.Revision = "revision-2";
            fixture.Fetcher.Write = root => { var selected = Populate(root, relative); Write(selected, "hooks/hooks.json", "{}"); };
            var failed = await fixture.Manager.UpdateAsync("chosen");
            Check(failed.Outcome == PluginOperationOutcome.Failed && failed.Errors.Any(e => e.Field == "hooks"), "foreign hooks rejected");
            Check(JsonSerializer.Serialize(fixture.Manager.List().Single()) == receipt, "failed update changed receipt");
            Check(File.ReadAllText(Path.Combine(destination, "skills/greet/SKILL.md")) == Skill("greet"), "failed update changed content");
            Discover(fixture, "greet");
            foreach (var corrupt in new Action<string>[]
            {
                root => Write(root, ".claude-plugin/marketplace.json", Catalog("")),
                root => Write(root, relative == "./" ? ".claude-plugin/plugin.json" : "contained/subdir/.claude-plugin/plugin.json", "{\"name\":\"renamed\"}"),
                root => { var path = Path.Combine(root, ".claude-plugin/marketplace.json"); File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(root, "missing")); },
            })
            {
                fixture.Fetcher.Write = root => { Populate(root, relative); corrupt(root); };
                var rejected = await fixture.Manager.UpdateAsync("chosen");
                Check(rejected.Outcome == PluginOperationOutcome.Failed && rejected.PreviousVersion == "revision-1"
                    && rejected.Errors.All(e => e.Field != "source"), "unsafe update rejected before mutation");
                Check(JsonSerializer.Serialize(fixture.Manager.List().Single()) == receipt, "unsafe update changed receipt");
                Check(ContentTrustCatalog.Verify(destination, detectUnlistedFiles: true).Trusted, "unsafe update changed trusted content");
            }
            fixture.Fetcher.Write = root => { var selected = Populate(root, relative, manifest: false); File.Delete(Path.Combine(selected, "skills/greet/SKILL.md")); Write(selected, "skills/new/SKILL.md", Skill("new")); };
            var updated = await fixture.Manager.UpdateAsync("chosen");
            Check(updated.Outcome == PluginOperationOutcome.Updated, "manifestless update replays selector: " + Describe(updated));
            Check(updated.Plugin?.ResolvedVersion == "revision-2" && updated.Plugin.ManifestFormat == PluginManifestFormat.Claude, "updated receipt");
            Check(!File.Exists(Path.Combine(destination, "skills/greet/SKILL.md")), "old file retired");
            Discover(fixture, "new");
            Check(ContentTrustCatalog.Verify(destination, detectUnlistedFiles: true).Trusted, "updated catalog");
            Check((await fixture.Manager.UpdateAsync("chosen")).Outcome == PluginOperationOutcome.AlreadyCurrent, "same revision unchanged");
            fixture.Manager.SetUpdatePreference("chosen", false);
            var calls = fixture.Fetcher.Calls.Count;
            Check((await fixture.Manager.UpdateAsync("chosen")).Outcome == PluginOperationOutcome.SkippedPinned && fixture.Fetcher.Calls.Count == calls, "pin skips fetch");
            Write(destination, "local.txt", "keep");
            Check(fixture.Manager.Remove("chosen").Outcome == PluginOperationOutcome.Removed, "remove");
            Check(File.ReadAllText(Path.Combine(destination, "local.txt")) == "keep" && !File.Exists(Path.Combine(destination, "skills/new/SKILL.md")), "exact set removal");
            Discover(fixture);
            Check(fixture.Manager.List().Count == 0 && fixture.Fetcher.Staging.All(path => !Directory.Exists(path)), "state and staging cleanup");
            Check(fixture.Fetcher.Calls.All(call => call == "repository:branch"), "update refetches repository reference");
        }
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task InvalidCatalogsAndPaths_FailClosed()
    {
        var entries = new List<(string Json, string Field)>();
        foreach (var source in new[] { "../outside", "./../outside", "./a/../b", "/root", "C:/root", "./a\\b", "./%2e%2e/x", "https://host/x", "file:///root", "./a//b", "./a/", "./a/./b", "./aux.txt", "./a?x", "./a#x" })
            entries.Add((JsonSerializer.Serialize(new { name = "chosen", source }), "sourcePath"));
        entries.AddRange(new[] {
            ("{}", "#/name"),
            ("{\"name\":\"chosen\"}", "sourcePath"),
            ("{\"name\":\"chosen\",\"source\":{\"source\":\"github\",\"repo\":\"x/y\"}}", "sourcePath"),
            ("{\"name\":\"chosen\",\"source\":\"./\",\"source\":\"./contained/subdir\"}", ".claude-plugin/marketplace.json/plugins") });
        foreach (var field in new[] { "hooks", "commands", "agents", "mcpServers", "lspServers", "skills", "strict", "version", "unknown" })
            entries.Add(("{\"name\":\"chosen\",\"source\":\"./contained/subdir\",\"" + field + "\":null}", field));
        foreach (var (entry, field) in entries)
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root => { Populate(root, "./contained/subdir"); Write(root, ".claude-plugin/marketplace.json", Catalog(entry)); };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, entry, field);
        }
        const string catalogPath = ".claude-plugin/marketplace.json";
        var documents = new (string Json, string Field)[] {
            ("{", catalogPath), ("[]", catalogPath), ("{\"owner\":{\"name\":\"owner\"},\"plugins\":[]}", "name"),
            ("{\"name\":\"market\",\"owner\":{\"name\":\"owner\"},\"metadata\":{\"pluginRoot\":\"./elsewhere\"},\"plugins\":[{\"name\":\"chosen\",\"source\":\"./contained/subdir\"}]}", "metadata/pluginRoot"),
            (Catalog("{\"name\":\"chosen\",\"source\":\"./contained/subdir\",\"name\":\"chosen\"}"), catalogPath + "/plugins"),
            (Catalog("{\"name\":\"chosen\",\"source\":\"./\"},{\"name\":\"chosen\",\"source\":\"./\"}"), "name"),
            ("{\"name\":\"market\",\"owner\":{\"name\":\"owner\"},\"name\":\"other\",\"plugins\":[]}", catalogPath),
            ("{\"name\":\"market\",\"owner\":{\"name\":\"x\",\"name\":\"y\"},\"plugins\":[]}", catalogPath + "/owner"),
            (new string(' ', 1024 * 1024 + 1), catalogPath),
            ("{\"name\":\"market\",\"owner\":{\"name\":\"owner\"},\"metadata\":{\"description\":\"" + new string('é', 600000) + "\"},\"plugins\":[{\"name\":\"chosen\",\"source\":\"./contained/subdir\"}]}", catalogPath)
        };
        foreach (var (document, field) in documents)
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root => { Populate(root, "./contained/subdir"); Write(root, catalogPath, document); };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "catalog " + document[..Math.Min(100, document.Length)], field);
        }
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task IdentityMetadataAndLinks_FailClosed()
    {
        var mutationFields = new[] { "name", "name", ".claude-plugin/marketplace.json", "sourcePath", "hooks", ".claude-plugin/plugin.json/name", "#/commands", ".claude-plugin/marketplace.json", ".claude-plugin", "contained/subdir", "contained/subdir/linked", "contained" };
        var mutationIndex = 0;
        foreach (var mutation in new Action<string>[] {
            root => Write(root, "contained/subdir/.claude-plugin/plugin.json", "{\"name\":\"other\"}"),
            root => Write(root, "contained/subdir/.botnexus-plugin/plugin.json", "{\"name\":\"other\"}"),
            root => File.Delete(Path.Combine(root, ".claude-plugin/marketplace.json")),
            root => Directory.Delete(Path.Combine(root, "contained/subdir"), true),
            root => Write(root, "contained/subdir/.botnexus-plugin/plugin.json", "{\"name\":\"chosen\",\"hooks\":\"./custom\"}"),
            root => Write(root, "contained/subdir/.claude-plugin/plugin.json", "{\"name\":\"chosen\",\"name\":\"chosen\"}"),
            root => { File.Delete(Path.Combine(root, "contained/subdir/.claude-plugin/plugin.json")); Write(root, "contained/subdir/.botnexus-plugin/plugin.json", "{\"name\":\"chosen\",\"commands\":\"./custom\"}"); },

            root => { var path = Path.Combine(root, ".claude-plugin/marketplace.json"); File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(root, "missing")); },
            root => { Directory.Delete(Path.Combine(root, ".claude-plugin"), true); Directory.CreateSymbolicLink(Path.Combine(root, ".claude-plugin"), Path.Combine(root, "missing")); },
            root => { Directory.Move(Path.Combine(root, "contained/subdir"), Path.Combine(root, "actual")); Directory.CreateSymbolicLink(Path.Combine(root, "contained/subdir"), Path.Combine(root, "actual")); },
            root => File.CreateSymbolicLink(Path.Combine(root, "contained/subdir/linked"), Path.Combine(root, "missing")),
            root => { Directory.Move(Path.Combine(root, "contained"), Path.Combine(root, "actual")); Directory.CreateSymbolicLink(Path.Combine(root, "contained"), Path.Combine(root, "actual")); }
        })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root => { Populate(root, "./contained/subdir"); mutation(root); };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "identity/link", mutationFields[mutationIndex++]);
        }
        foreach (var selector in new[] { "", " ", "missing", "../escape", "Upper" })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root => Populate(root, "./contained/subdir");
            Reject(await fixture.Manager.InstallAsync(Request(selector)), fixture, "invalid selector");
        }
        using var mismatch = new Fixture();
        mismatch.Fetcher.Write = root => Populate(root, "./contained/subdir");
        Reject(await mismatch.Manager.InstallAsync(Request(name: "other")), mismatch, "request identity mismatch");
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task NativeRequests_AreNotReinterpreted()
    {
        using var fixture = new Fixture();
        fixture.Fetcher.Write = root => { Write(root, ".botnexus-plugin/plugin.json", "{\"name\":\"native\",\"hooks\":\"./hooks\"}"); Write(root, "hooks/a.json", "{}"); };
        Check((await fixture.Manager.InstallAsync(new PluginInstallRequest { Source = "repository" })).Outcome == PluginOperationOutcome.Installed, "native unchanged");
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task Selection_MetadataAuthorityAndUnselectedDeclarations()
    {
        using var fixture = new Fixture();
        fixture.Fetcher.Write = root =>
        {
            var selected = Populate(root, "./contained/subdir");
            Write(root, ".claude-plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./contained/subdir\",\"description\":\"catalog description\"},{\"name\":\"sibling\",\"source\":{\"source\":\"url\",\"url\":\"https://never-fetch.invalid\"},\"hooks\":{}}"));
            Write(selected, ".botnexus-plugin/plugin.json", "{\"name\":\"chosen\",\"version\":\"1.2.3\",\"description\":\"native description\"}");
        };
        var result = await fixture.Manager.InstallAsync(Request());
        Check(result.Outcome == PluginOperationOutcome.Installed && result.Plugin?.ManifestFormat == PluginManifestFormat.Native
            && result.Plugin.ManifestVersion == "1.2.3", "whole native manifest authority: " + Describe(result));
        Check(fixture.Fetcher.Calls.Count == 1, "unselected remote source fetched");
        fixture.Fetcher.Revision = "revision-2";
        var restarted = new PluginLifecycleManager(new PluginStateStore(fixture.Root), fixture.Fetcher);
        Check((await restarted.UpdateAsync("chosen")).Outcome == PluginOperationOutcome.Updated, "persisted selector replay after restart");
        Check(restarted.List().Single().Files.All(path => !path.Contains("sibling") && !path.Contains("marketplace.json")), "unselected declaration promoted");
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public Task Catalog_MissingOwner_IsRejected() => RejectOwner(null, "owner");

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public Task Catalog_EmptyOwner_IsRejected() => RejectOwner("{}", "owner/name");

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task Catalog_InvalidOwnerFields_AreRejected()
    {
        foreach (var owner in new[] { "{\"name\":42}", "{\"name\":null}", "{\"name\":\"\"}", "{\"name\":\" \"}" })
            await RejectOwner(owner, "owner/name");
        await RejectOwner("null", "owner");
        await RejectOwner("{\"name\":\"owner\",\"email\":42}", "owner/email");
        await RejectOwner("{\"name\":\"owner\",\"url\":false}", "owner/url");
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task Selection_EmptyMetadataDirectory_IsRejected()
    {
        foreach (var content in new[] { "empty", "nested-catalog", "extra-file" })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root =>
            {
                var selected = Populate(root, "./contained/subdir", manifest: false);
                Directory.CreateDirectory(Path.Combine(selected, ".claude-plugin"));
                if (content != "empty") Write(selected, ".claude-plugin/marketplace.json", "{}");
                if (content == "extra-file") Write(selected, ".claude-plugin/extra.json", "{}");
            };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "selected metadata " + content, ".claude-plugin/plugin.json");
        }
    }

#if !MARKETPLACE_NOHOST
    [Xunit.Fact]
#endif
    public async Task Selection_NativeOnlyCanonicalRoots_AreRequired()
    {
        foreach (var rootName in new[] { "Hooks", "Skills", "Commands", "Agents" })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root =>
            {
                var selected = Populate(root, "./contained/subdir");
                File.Delete(Path.Combine(selected, ".claude-plugin/plugin.json"));
                Directory.Delete(Path.Combine(selected, ".claude-plugin"));
                Write(selected, ".botnexus-plugin/plugin.json", "{\"name\":\"chosen\"}");
                if (rootName == "Skills")
                {
                    Directory.Move(Path.Combine(selected, "skills"), Path.Combine(selected, "renaming"));
                    Directory.Move(Path.Combine(selected, "renaming"), Path.Combine(selected, rootName));
                }
                else
                    Write(selected, rootName + "/ignored.json", "{}");
            };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "noncanonical selected native root " + rootName);
        }
    }

    [Xunit.Fact]
    public async Task CopilotCatalog_SelectsLegacyPackageAndReplaysPersistedSelection()
    {
        using var fixture = new Fixture();
        fixture.Fetcher.Write = PopulateCopilot;
        var installed = await fixture.Manager.InstallAsync(Request());
        Check(installed.Outcome == PluginOperationOutcome.Installed, Describe(installed));
        var record = new PluginStateStore(fixture.Root).Find("chosen") ?? throw new Exception("missing receipt");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(record));
        Check(json.RootElement.GetProperty("marketplaceCatalogPath").GetString() == ".github/plugin/marketplace.json", "catalog dialect persisted");
        Check(json.RootElement.GetProperty("marketplaceSourcePath").GetString() == "./packages/chosen", "source subpath persisted");
        Check(record.MarketplacePluginName == "chosen" && record.ManifestFormat == PluginManifestFormat.Legacy, "legacy identity receipt");
        Check(record.ManifestVersion == "2.0.0", "package manifest, not catalog version, is authoritative");
        Check(record.Files.All(path => !path.Contains("sibling") && !path.Contains("marketplace.json")), "selected subtree only");
        Check(ContentTrustCatalog.Verify(fixture.Manager.GetPluginDirectory("chosen"), detectUnlistedFiles: true).Trusted, "trusted legacy import");
        Discover(fixture, "greet");
        fixture.Fetcher.Revision = "revision-2";
        var restarted = new PluginLifecycleManager(new PluginStateStore(fixture.Root), fixture.Fetcher);
        Check((await restarted.UpdateAsync("chosen")).Outcome == PluginOperationOutcome.Updated, "selector replay after restart");
        var receipt = JsonSerializer.Serialize(restarted.List().Single());
        fixture.Fetcher.Revision = "revision-3";
        fixture.Fetcher.Write = root => { PopulateCopilot(root); Write(root, "packages/chosen/plugin.json", "{\"name\":\"other\"}"); };
        Check((await restarted.UpdateAsync("chosen")).Outcome == PluginOperationOutcome.Failed, "identity mismatch rejected");
        Check(JsonSerializer.Serialize(restarted.List().Single()) == receipt, "failed update preserves receipt");
        Discover(fixture, "greet");
        Check(restarted.Remove("chosen").Outcome == PluginOperationOutcome.Removed, "selected package removed");
        Discover(fixture);
    }

    [Xunit.Fact]
    public async Task CopilotCatalog_AmbiguityUnsafePathsAndOverridesFailClosed()
    {
        foreach (var mutation in new Action<string>[]
        {
            root => Write(root, ".claude-plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./packages/chosen\"}")),
            root => Write(root, ".github/plugin/marketplace.json", "{"),
            root => Write(root, ".github/plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./../escape\",\"version\":\"1.0.0\"}")),
            root => Write(root, ".github/plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./packages/chosen\",\"version\":42}")),
            root => Write(root, ".github/plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./packages/chosen\",\"skills\":\"./elsewhere\"}")),
            root => Write(root, ".github/plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./packages/chosen\",\"version\":\"1.0.0\",\"version\":\"2.0.0\"}")),
            root => Write(root, "packages/chosen/hooks/hooks.json", "{}"),
            root => { var path = Path.Combine(root, ".github/plugin/marketplace.json"); File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(root, "missing")); },
        })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root => { PopulateCopilot(root); mutation(root); };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "unsafe Copilot catalog or package");
        }
    }

    [Xunit.Fact]
    public async Task CopilotCatalog_UnselectedUppercaseIdentity_DoesNotBlockCommonLegacyPackage()
    {
        using var fixture = new Fixture();
        fixture.Fetcher.Write = root =>
        {
            Write(root, ".github/plugin/marketplace.json", Catalog("{\"name\":\"common\",\"source\":\"./.github/plugins/common\",\"version\":\"1.0.0\"},{\"name\":\"PM\",\"source\":\"./.github/plugins/PM\"}"));
            Write(root, ".github/plugins/common/plugin.json", "{\"name\":\"common\",\"version\":\"2.0.0\",\"skills\":\"./skills/\"}");
            Write(root, ".github/plugins/common/skills/greet/SKILL.md", Skill("greet"));
        };
        var installed = await fixture.Manager.InstallAsync(Request("common"));
        Check(installed.Outcome == PluginOperationOutcome.Installed, Describe(installed));
        var record = new PluginStateStore(fixture.Root).Find("common") ?? throw new Exception("missing common receipt");
        Check(record.ManifestFormat == PluginManifestFormat.Legacy && record.ManifestVersion == "2.0.0", "selected legacy manifest authority");
        Check(record.MarketplacePluginName == "common" && record.Files.Contains("skills/greet/SKILL.md"), "selected common receipt and content");
        Check(fixture.Fetcher.Calls.Count == 1, "unselected PM source fetched");
        Check(ContentTrustCatalog.Verify(fixture.Manager.GetPluginDirectory("common"), detectUnlistedFiles: true).Trusted, "trusted common import");
    }

    [Xunit.Fact]
    public async Task Catalog_UnselectedIdentities_StillRequireNonemptyUniqueStrings()
    {
        foreach (var sibling in new[] { "{}", "{\"name\":null}", "{\"name\":42}", "{\"name\":\"\"}", "{\"name\":\" \"}", "{\"name\":\"CHOSEN\"}" })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root =>
            {
                PopulateCopilot(root);
                Write(root, ".github/plugin/marketplace.json", Catalog("{\"name\":\"chosen\",\"source\":\"./packages/chosen\"}," + sibling[..^1] + (sibling == "{}" ? "" : ",") + "\"source\":\"./unselected\"}"));
            };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "invalid unselected identity " + sibling);
        }
    }

    [Xunit.Fact]
    public async Task Selection_RootTransportGitRefs_AreNotPluginPaths()
    {
        foreach (var legacy in new[] { false, true })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root =>
            {
                if (legacy) PopulateCopilot(root); else Populate(root, "./");
                var refs = Path.Combine(root, ".git", "refs", "heads");
                Directory.CreateDirectory(refs);
                var auxiliaryRef = Path.GetFullPath(Path.Combine(refs, "aux"));
                // Git refs need not be portable plugin destinations. Use the Windows extended
                // path syntax to create this legal Git ref without DOS device-name resolution.
                if (OperatingSystem.IsWindows()) auxiliaryRef = @"\\?\" + auxiliaryRef;
                File.WriteAllText(auxiliaryRef, new string('a', 40) + "\n");
            };
            var installed = await fixture.Manager.InstallAsync(Request());
            Check(installed.Outcome == PluginOperationOutcome.Installed, Describe(installed));
            var record = fixture.Manager.List().Single();
            Check(record.Files.All(path => !path.StartsWith(".git/", StringComparison.Ordinal)), "transport metadata promoted");
            Check(ContentTrustCatalog.Verify(fixture.Manager.GetPluginDirectory("chosen"), detectUnlistedFiles: true).Trusted, "trusted import with transport refs");
        }
    }

    [Xunit.Fact]
    public async Task Selection_GitExclusion_DoesNotBypassLinksOrWorkingTreeSafety()
    {
        foreach (var relative in new[] { ".git", "packages/chosen/.git", "sibling/.git", "sibling/linked" })
        {
            using var fixture = new Fixture();
            fixture.Fetcher.Write = root =>
            {
                PopulateCopilot(root);
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new Exception("parent"));
                Directory.CreateSymbolicLink(path, Path.Combine(root, "missing"));
            };
            Reject(await fixture.Manager.InstallAsync(Request()), fixture, "linked Git or working tree entry", relative);
        }
        using var nested = new Fixture();
        nested.Fetcher.Write = root =>
        {
            PopulateCopilot(root);
            var refs = Path.Combine(root, "sibling", ".git", "refs", "heads");
            Directory.CreateDirectory(refs);
            var auxiliaryRef = Path.GetFullPath(Path.Combine(refs, "aux"));
            if (OperatingSystem.IsWindows()) auxiliaryRef = @"\\?\" + auxiliaryRef;
            File.WriteAllText(auxiliaryRef, new string('a', 40) + "\n");
        };
        Reject(await nested.Manager.InstallAsync(Request()), nested, "nested Git paths remain inspected");
    }

    private static void PopulateCopilot(string root)
    {
        // Synthetic values; only the field structure mirrors the inspected repository.
        Write(root, ".github/plugin/marketplace.json", """
            {"name":"sample-market","metadata":{"description":"Sample catalog","version":"1.0.0"},
             "owner":{"name":"Sample owner"},"plugins":[
               {"name":"chosen","source":"./packages/chosen","description":"Selected skills","version":"1.0.0","keywords":["sample"]},
               {"name":"sibling","source":"./packages/sibling","description":"Other skills","version":"1.0.0","keywords":["other"]}]}
            """);
        Write(root, "packages/chosen/plugin.json", "{\"name\":\"chosen\",\"version\":\"2.0.0\",\"skills\":\"./skills/\"}");
        Write(root, "packages/chosen/skills/greet/SKILL.md", Skill("greet"));
        Write(root, "packages/sibling/skills/other/SKILL.md", Skill("other"));
    }

    private static async Task RejectOwner(string? owner, string field)
    {
        using var fixture = new Fixture();
        fixture.Fetcher.Write = root =>
        {
            Populate(root, "./contained/subdir");
            Write(root, ".claude-plugin/marketplace.json", "{\"name\":\"market\"," + (owner is null ? "" : "\"owner\":" + owner + ",")
                + "\"plugins\":[{\"name\":\"chosen\",\"source\":\"./contained/subdir\"}]}");
        };
        Reject(await fixture.Manager.InstallAsync(Request()), fixture, "invalid owner " + owner, field);
    }

    private static PluginInstallRequest Request(string selector = "chosen", string? name = null) =>
        JsonSerializer.Deserialize<PluginInstallRequest>(JsonSerializer.Serialize(new { Source = "repository", Reference = "branch", Name = name, MarketplacePluginName = selector })) ?? throw new Exception("request deserialize");
    private static string Populate(string root, string relative, bool manifest = true)
    {
        Write(root, ".claude-plugin/marketplace.json", JsonSerializer.Serialize(new { name = "market", owner = new { name = "owner" }, plugins = new[] { new { name = "chosen", source = relative }, new { name = "sibling", source = "./sibling" } } }));
        var selected = relative == "./" ? root : Path.Combine(root, "contained/subdir");
        if (manifest) Write(selected, ".claude-plugin/plugin.json", "{\"name\":\"chosen\"}");
        Write(selected, "skills/greet/SKILL.md", Skill("greet"));
        if (relative != "./")
        {
            Write(root, "sibling/hooks/unselected.json", "{}");
            Write(root, "sibling/skills/sibling/SKILL.md", Skill("sibling"));
        }
        return selected;
    }
    private static string Catalog(string entry) => "{\"name\":\"market\",\"owner\":{\"name\":\"owner\"},\"plugins\":[" + entry + "]}";
    private static string Skill(string name) => "---\nname: " + name + "\ndescription: Test the selected skill lifecycle.\n---\n# Skill\nSay hello.";
    private static void Discover(Fixture fixture, params string[] expected)
    {
        var skills = SkillDiscovery.Discover(null, null, null,
            pluginSkillsDirs: PluginSkillRootResolver.Resolve(new PluginStateStore(fixture.Root)));
        Check(skills.Select(skill => skill.Name).Order().SequenceEqual(expected.Order()), "discovery: " + string.Join(",", skills.Select(skill => skill.Name)));
        foreach (var skill in skills)
        {
            Check(skill.Source == SkillSource.Plugin && skill.Content.Contains("Say hello."), "selected plugin skill content");
            Check(Path.GetFullPath(skill.SourcePath) == Path.GetFullPath(Path.Combine(fixture.Manager.GetPluginDirectory("chosen"), "skills", skill.Name)), "selected skill source path");
        }
    }
    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new Exception("parent"));
        File.WriteAllText(path, content);
    }
    private static string Describe(PluginOperationResult result) => string.Join("; ", result.Errors.Select(e => e.Field + ": " + e.Message));
    private static void Check(bool condition, string message)
    {
#if MARKETPLACE_NOHOST
        if (!condition) throw new InvalidOperationException(message);
#else
        condition.ShouldBeTrue(message);
#endif
    }
    private static void Reject(PluginOperationResult result, Fixture fixture, string context, string? field = null)
    {
        Check(result.Outcome == PluginOperationOutcome.Failed && result.Errors.Count > 0 && result.Errors.All(e => e.Field != "source"), "expected validation failure: " + context + " " + Describe(result));
        if (field is not null) Check(result.Errors.Any(e => e.Field == field), "expected field " + field + ": " + context + " " + Describe(result));
        Check(fixture.Manager.List().Count == 0 && !Directory.Exists(fixture.Manager.GetPluginDirectory("chosen")), "rejected install materialized content");
        Check(fixture.Fetcher.Staging.All(path => !Directory.Exists(path)), "failed staging cleanup");
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "claude-selection-tests", Guid.NewGuid().ToString("N"));
        public Fetcher Fetcher { get; } = new();
        public PluginLifecycleManager Manager { get; }
        public Fixture() => Manager = new(new PluginStateStore(Root), Fetcher);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Fetcher : IPluginSourceFetcher
    {
        public Action<string> Write { get; set; } = _ => { };
        public string Revision { get; set; } = "revision-1";
        public List<string> Staging { get; } = [];
        public List<string> Calls { get; } = [];
        public Task<PluginFetchResult> FetchAsync(string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            Staging.Add(stagingDirectory); Calls.Add(source + ":" + reference); Write(stagingDirectory);
            return Task.FromResult(new PluginFetchResult(Revision));
        }
    }
}
