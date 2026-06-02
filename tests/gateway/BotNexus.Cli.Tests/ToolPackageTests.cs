using System.Xml.Linq;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Validates the CLI NuGet tool package is installable on supported .NET SDK versions.
///
/// Issue #736: Users on .NET 9 received "DotnetToolSettings.xml was not found in the package"
/// because BotNexus.Cli only shipped a net10.0 TFM. The fix adds Directory.Build.props files
/// to src/gateway/, src/agent/, and src/domain/ with TargetFrameworks=net9.0;net10.0 so that
/// dotnet tool install succeeds on any SDK >= 9.
/// </summary>
public sealed class ToolPackageTests
{
    [Fact]
    public void CliCsproj_does_not_override_TargetFramework_singular()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ToolPackageTests).Assembly.Location)!;
        var csprojPath = FindCliCsproj(assemblyDir);
        csprojPath.ShouldNotBeNull("Could not find BotNexus.Cli.csproj relative to repo root");

        var doc = XDocument.Load(csprojPath!);

        // The CLI csproj must NOT set <TargetFramework> (singular) which would override the
        // per-subtree multi-targeting and break install on .NET 9.
        var singleTfm = doc.Descendants("TargetFramework").FirstOrDefault();
        Assert.True(
            singleTfm == null,
            "BotNexus.Cli.csproj must not set <TargetFramework> (singular) — " +
            "that would override the gateway multi-target and break install on .NET 9 (issue #736)");
    }

    [Fact]
    public void GatewayDirectoryBuildProps_TargetFrameworks_includes_net9_and_net10()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ToolPackageTests).Assembly.Location)!;
        var repoRoot = FindRepoRoot(assemblyDir);
        repoRoot.ShouldNotBeNull("Could not find repo root (Directory.Build.props with Version property)");

        var propsPath = Path.Combine(repoRoot!, "src", "gateway", "Directory.Build.props");
        Assert.True(
            File.Exists(propsPath),
            $"src/gateway/Directory.Build.props must exist to multi-target gateway projects. " +
            $"Expected at: {propsPath} (issue #736)");

        var doc = XDocument.Load(propsPath);
        var tfms = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value ?? string.Empty;

        Assert.True(
            tfms.Contains("net9.0", StringComparison.Ordinal),
            $"src/gateway/Directory.Build.props must include net9.0 in <TargetFrameworks> so dotnet tool install " +
            $"succeeds on .NET 9 SDK (issue #736). Actual value: '{tfms}'");

        Assert.True(
            tfms.Contains("net10.0", StringComparison.Ordinal),
            $"src/gateway/Directory.Build.props must include net10.0 in <TargetFrameworks>. Actual value: '{tfms}'");
    }

    [Fact]
    public void GatewayDirectoryBuildProps_does_not_set_singular_TargetFramework()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ToolPackageTests).Assembly.Location)!;
        var repoRoot = FindRepoRoot(assemblyDir);
        repoRoot.ShouldNotBeNull("Could not find repo root");

        var propsPath = Path.Combine(repoRoot!, "src", "gateway", "Directory.Build.props");
        if (!File.Exists(propsPath))
            return; // Covered by other test

        var doc = XDocument.Load(propsPath);

        // We allow <TargetFramework /> (empty value) as a deliberate "clear" pattern so the SDK
        // uses <TargetFrameworks> (plural). What we disallow is a non-empty TargetFramework
        // value that would override the multi-targeting. (Issue #736)
        var singleTfmWithValue = doc.Descendants("TargetFramework")
            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Value));

        Assert.True(
            singleTfmWithValue == null,
            "src/gateway/Directory.Build.props must not set a non-empty <TargetFramework> (singular) — " +
            "that would override multi-targeting and break install on .NET 9 (issue #736). " +
            "An empty <TargetFramework /> to clear the root value is acceptable.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var propsFile = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(propsFile))
            {
                var doc = XDocument.Load(propsFile);
                // Root props has the Version element; subtree props only import it.
                if (doc.Descendants("Version").Any())
                    return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindCliCsproj(string startDir)
    {
        var repoRoot = FindRepoRoot(startDir);
        if (repoRoot == null) return null;
        var csproj = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");
        return File.Exists(csproj) ? csproj : null;
    }
}
