using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace BotNexus.Architecture.Tests;

public sealed class UserGuideBuildReconciliationTests : ArchitectureTest, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-guide-reconciliation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CopyUserGuide_ReconcilesIncrementalOutputToCurrentOwnedSources()
    {
        var sourceRoot = Path.Combine(_root, "docs");
        var guideSource = Path.Combine(sourceRoot, "user-guide");
        Directory.CreateDirectory(Path.Combine(guideSource, "nested"));
        await File.WriteAllTextAsync(Path.Combine(guideSource, "legacy.md"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(guideSource, "nested", "current.md"), "current");
        await File.WriteAllTextAsync(Path.Combine(guideSource, "guide-index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "skills.md"), "skills");

        var incrementalRoot = Path.Combine(_root, "incremental", "wwwroot");
        var sentinel = Path.Combine(incrementalRoot, "unrelated-sentinel.txt");
        Directory.CreateDirectory(incrementalRoot);
        await File.WriteAllTextAsync(sentinel, "preserve me");

        await RunCopyTargetAsync(sourceRoot, incrementalRoot);
        File.Exists(Path.Combine(incrementalRoot, "guide", "legacy.md")).ShouldBeTrue();

        File.Delete(Path.Combine(guideSource, "legacy.md"));
        await File.WriteAllTextAsync(Path.Combine(guideSource, "renamed.md"), "renamed");
        await RunCopyTargetAsync(sourceRoot, incrementalRoot);

        var incrementalGuide = Path.Combine(incrementalRoot, "guide");
        File.Exists(Path.Combine(incrementalGuide, "legacy.md")).ShouldBeFalse(
            "removed guide sources must not survive an incremental target invocation");
        File.Exists(Path.Combine(incrementalGuide, "renamed.md")).ShouldBeTrue();
        File.Exists(Path.Combine(incrementalGuide, "nested", "current.md")).ShouldBeTrue();
        File.Exists(Path.Combine(incrementalGuide, "guide-index.json")).ShouldBeTrue();
        File.Exists(Path.Combine(incrementalGuide, "skills.md")).ShouldBeTrue();
        File.Exists(sentinel).ShouldBeTrue(
            "reconciliation owns wwwroot/guide only and must preserve sibling static assets");

        var cleanRoot = Path.Combine(_root, "clean", "wwwroot");
        await RunCopyTargetAsync(sourceRoot, cleanRoot);

        RelativeInventory(incrementalGuide).ShouldBe(
            RelativeInventory(Path.Combine(cleanRoot, "guide")),
            ignoreOrder: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveProjectStaticWebAssets_RegistersOnlyCurrentGuideSourcesOnce(bool warmOutput)
    {
        var sourceRoot = Path.Combine(_root, "docs");
        var guideSource = Path.Combine(sourceRoot, "user-guide");
        Directory.CreateDirectory(Path.Combine(guideSource, "nested"));
        await File.WriteAllTextAsync(Path.Combine(guideSource, "legacy.md"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(guideSource, "nested", "current.md"), "current");
        await File.WriteAllTextAsync(Path.Combine(guideSource, "guide-index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "skills.md"), "skills");

        var outputRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "sentinel.txt"), "preserve me");
        if (warmOutput)
            await RunCopyTargetAsync(sourceRoot, outputRoot);
        File.Delete(Path.Combine(guideSource, "legacy.md"));
        await File.WriteAllTextAsync(Path.Combine(guideSource, "renamed.md"), "renamed");

        // Keep the production SDK, properties, item declarations and targets. Remove only
        // dependencies: resolving project assets needs neither compilation nor a restore.
        // An isolated project directory prevents parallel tests from mutating real wwwroot.
        var fixture = XDocument.Load(ClientProject);
        var project = fixture.Root;
        project.ShouldNotBeNull();
        foreach (var dependency in project.Descendants()
                     .Where(node => node.Name.LocalName is "PackageReference" or "ProjectReference" or "ServiceWorker")
                     .ToArray())
            dependency.Remove();
        project.Add(new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"),
            new XElement("UserGuideSourceRoot", sourceRoot),
            new XElement("UserGuideOutputRoot", outputRoot),
            new XElement("WasmEnableHotReload", "false")));
        var fixturePath = Path.Combine(_root, "guide-assets.csproj");
        fixture.Save(fixturePath);

        // Each invocation reevaluates Content, just like a project-reference build followed
        // by SignalR's nested publish. Check both the first inventory and the cached repeat.
        for (var invocation = 0; invocation < 2; invocation++)
        {
            var output = await RunMsBuildAsync(fixturePath,
                "-t:ResolveProjectStaticWebAssets", "-getItem:StaticWebAsset");
            using var inventory = JsonDocument.Parse(output);
            var assets = inventory.RootElement.GetProperty("Items").GetProperty("StaticWebAsset")
                .EnumerateArray().ToArray();
            var guideAssets = assets.Where(asset =>
                asset.GetProperty("RelativePath").GetString()?.StartsWith("guide/", StringComparison.Ordinal) == true)
                .ToArray();
            guideAssets.Select(asset => Path.GetRelativePath(outputRoot,
                    asset.GetProperty("Identity").GetString() ?? string.Empty).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray()
                .ShouldBe(new[] { "guide/guide-index.json", "guide/nested/current.md", "guide/renamed.md", "guide/skills.md" },
                    "clean, warm and repeated SDK discovery must register each current guide asset exactly once");
            guideAssets.ShouldAllBe(asset => asset.GetProperty("AssetKind").GetString() == "All");
            assets.Count(asset => asset.GetProperty("Identity").GetString() == Path.Combine(outputRoot, "sentinel.txt"))
                .ShouldBe(1, "unrelated static assets must retain their SDK registration");
            File.Exists(Path.Combine(outputRoot, "guide", "legacy.md")).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ResolveProjectStaticWebAssets_CompetingConfigurationPreservesRegisteredGuide()
    {
        var sourceRoot = Path.Combine(_root, "docs");
        var guideSource = Path.Combine(sourceRoot, "user-guide");
        Directory.CreateDirectory(Path.Combine(guideSource, "nested"));
        await File.WriteAllTextAsync(Path.Combine(guideSource, "held.md"), "held by Debug");
        await File.WriteAllTextAsync(Path.Combine(guideSource, "nested", "current.md"), "current");
        await File.WriteAllTextAsync(Path.Combine(guideSource, "guide-index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "skills.md"), "skills");

        var replacementRoot = Path.Combine(_root, "replacement");
        Directory.CreateDirectory(Path.Combine(replacementRoot, "user-guide"));
        await File.WriteAllTextAsync(Path.Combine(replacementRoot, "user-guide", "renamed.md"), "Release");
        await File.WriteAllTextAsync(Path.Combine(replacementRoot, "user-guide", "guide-index.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(replacementRoot, "skills.md"), "skills");

        var fixture = XDocument.Load(ClientProject);
        var project = fixture.Root;
        project.ShouldNotBeNull();
        foreach (var dependency in project.Descendants()
                     .Where(node => node.Name.LocalName is "PackageReference" or "ProjectReference" or "ServiceWorker")
                     .ToArray())
            dependency.Remove();
        project.Add(new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"),
            new XElement("UserGuideSourceRoot", sourceRoot),
            new XElement("WasmEnableHotReload", "false")));

        // Deterministically place Release reconciliation after Debug registers Content but
        // before the real SDK opens those files. Different source inventories expose deletion
        // without relying on a narrow filesystem race or sleeps. No SDK assertion is replaced.
        project.Add(new XElement("Target",
            new XAttribute("Name", "CompetingConfiguration"),
            new XAttribute("BeforeTargets", "ResolveProjectStaticWebAssets"),
            new XAttribute("DependsOnTargets", "CopyUserGuide"),
            new XAttribute("Condition", "'$(Configuration)' == 'Debug'"),
            new XElement("MSBuild",
                new XAttribute("Projects", "$(MSBuildProjectFullPath)"),
                new XAttribute("Targets", "CopyUserGuide"),
                new XAttribute("Properties", $"Configuration=Release;UserGuideSourceRoot={replacementRoot}"))));
        var fixturePath = Path.Combine(_root, "competing-guide.csproj");
        fixture.Save(fixturePath);
        var legacyGuide = Path.Combine(_root, "wwwroot", "guide");
        Directory.CreateDirectory(legacyGuide);
        var legacyFile = Path.Combine(legacyGuide, "stale.md");
        await File.WriteAllTextAsync(legacyFile, "legacy generated output");

        var expected = new[] { "guide/guide-index.json", "guide/held.md", "guide/nested/current.md", "guide/skills.md" };
        (await ResolveCompetingGuideAsync(fixturePath)).ShouldBe(expected);
        (await ResolveCompetingGuideAsync(fixturePath)).ShouldBe(expected);

        File.Delete(Path.Combine(guideSource, "held.md"));
        await File.WriteAllTextAsync(Path.Combine(guideSource, "renamed.md"), "renamed");
        var incremental = await ResolveCompetingGuideAsync(fixturePath);
        incremental.ShouldBe(new[] { "guide/guide-index.json", "guide/nested/current.md", "guide/renamed.md", "guide/skills.md" });
        Directory.Delete(Path.Combine(_root, "obj", "Debug"), recursive: true);
        (await ResolveCompetingGuideAsync(fixturePath)).ShouldBe(incremental);
        (await File.ReadAllTextAsync(legacyFile)).ShouldBe("legacy generated output",
            "legacy shared output is ignored, not deleted out from under another consumer");
    }

    private static async Task<string[]> ResolveCompetingGuideAsync(string fixturePath)
    {
        var output = await RunMsBuildAsync(fixturePath,
            "-t:ResolveProjectStaticWebAssets", "-getItem:StaticWebAsset");
        using var inventory = JsonDocument.Parse(output);
        var guideAssets = inventory.RootElement.GetProperty("Items").GetProperty("StaticWebAsset")
            .EnumerateArray().Where(asset =>
                asset.GetProperty("RelativePath").GetString()?.StartsWith("guide/", StringComparison.Ordinal) == true)
            .ToArray();
        guideAssets.ShouldAllBe(asset => File.Exists(asset.GetProperty("Identity").GetString()));
        guideAssets.ShouldAllBe(asset => asset.GetProperty("AssetKind").GetString() == "All");
        guideAssets.ShouldAllBe(asset => (asset.GetProperty("Identity").GetString() ?? string.Empty)
            .Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        // ContentRoot must map the logical guide URL back to its physical generated file,
        // including consumers which use a file provider rather than the publish copy list.
        guideAssets.ShouldAllBe(asset => Path.GetFullPath(Path.Combine(
            asset.GetProperty("ContentRoot").GetString() ?? string.Empty,
            (asset.GetProperty("RelativePath").GetString() ?? string.Empty)
                .Replace("#[.{fingerprint}]?", string.Empty, StringComparison.Ordinal)))
            == asset.GetProperty("Identity").GetString());
        return guideAssets.Select(asset => (asset.GetProperty("RelativePath").GetString() ?? string.Empty)
                .Replace("#[.{fingerprint}]?", string.Empty, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private string ClientProject => Repository.Path(
        "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.csproj");

    private static string[] RelativeInventory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private async Task RunCopyTargetAsync(string sourceRoot, string outputRoot) =>
        _ = await RunMsBuildAsync(ClientProject, "-t:CopyUserGuide",
            $"-p:UserGuideSourceRoot={sourceRoot}", $"-p:UserGuideOutputRoot={outputRoot}");

    private static async Task<string> RunMsBuildAsync(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-v:q");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        process.ShouldNotBeNull("dotnet msbuild must start for the guide target regression");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        process.ExitCode.ShouldBe(0, $"Guide MSBuild target failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        return output;
    }
}
