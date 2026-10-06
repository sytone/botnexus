using System.Diagnostics;

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

    private static string[] RelativeInventory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private async Task RunCopyTargetAsync(string sourceRoot, string outputRoot)
    {
        var project = Repository.Path(
            "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-t:CopyUserGuide");
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-v:q");
        start.ArgumentList.Add($"-p:UserGuideSourceRoot={sourceRoot}");
        start.ArgumentList.Add($"-p:UserGuideOutputRoot={outputRoot}");

        using var process = Process.Start(start);
        process.ShouldNotBeNull("dotnet msbuild must start for the guide target regression");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        process.ExitCode.ShouldBe(0, $"CopyUserGuide failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}
