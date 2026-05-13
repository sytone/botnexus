using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

public sealed class ServeCommandTests : IDisposable
{
    private readonly string _rootPath;

    public ServeCommandTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-serve-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public void DeployExtensionsSilent_UsesDebugOutput_WhenReleaseOutputMissing()
    {
        var repoRoot = Path.Combine(_rootPath, "repo");
        var home = Path.Combine(_rootPath, "home");
        var extensionDir = CreateExtensionProject(repoRoot, "sample-debug", "Sample.Debug");
        var debugDir = Path.Combine(extensionDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(debugDir);
        File.WriteAllText(Path.Combine(debugDir, "Sample.Debug.dll"), "debug");

        var deployed = ServeCommand.DeployExtensionsSilent(repoRoot, home, verbose: false);

        deployed.ShouldBe(1);
        File.Exists(Path.Combine(home, "extensions", "sample-debug", "Sample.Debug.dll")).ShouldBeTrue();
        File.Exists(Path.Combine(home, "extensions", "sample-debug", "botnexus-extension.json")).ShouldBeTrue();
    }

    [Fact]
    public void DeployExtensionsSilent_PrefersMostRecentOutput_WhenReleaseIsStale()
    {
        var repoRoot = Path.Combine(_rootPath, "repo");
        var home = Path.Combine(_rootPath, "home");
        var extensionDir = CreateExtensionProject(repoRoot, "sample-fresh", "Sample.Fresh");

        var releaseDir = Path.Combine(extensionDir, "bin", "Release", "net10.0");
        Directory.CreateDirectory(releaseDir);
        var releaseDll = Path.Combine(releaseDir, "Sample.Fresh.dll");
        File.WriteAllText(releaseDll, "release-stale");
        Directory.SetLastWriteTimeUtc(releaseDir, DateTime.UtcNow.AddMinutes(-10));

        var debugDir = Path.Combine(extensionDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(debugDir);
        var debugDll = Path.Combine(debugDir, "Sample.Fresh.dll");
        File.WriteAllText(debugDll, "debug-fresh");
        Directory.SetLastWriteTimeUtc(debugDir, DateTime.UtcNow);

        var deployed = ServeCommand.DeployExtensionsSilent(repoRoot, home, verbose: false);

        deployed.ShouldBe(1);
        var deployedDll = Path.Combine(home, "extensions", "sample-fresh", "Sample.Fresh.dll");
        File.Exists(deployedDll).ShouldBeTrue();
        File.ReadAllText(deployedDll).ShouldBe("debug-fresh");
    }

    private static string CreateExtensionProject(string repoRoot, string extensionId, string projectName)
    {
        var projectDir = Path.Combine(repoRoot, "src", "extensions", projectName);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, $"{projectName}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(projectDir, "botnexus-extension.json"), $$"""
            {
              "id": "{{extensionId}}",
              "name": "{{projectName}}",
              "version": "1.0.0",
              "entryAssembly": "{{projectName}}.dll",
              "extensionTypes": ["tool"]
            }
            """);

        return projectDir;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
            return;

        Directory.Delete(_rootPath, recursive: true);
    }
}
