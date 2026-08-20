using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2892. Child-process environment overrides must be
/// written through the single shared <c>ProcessEnvironment.Merge</c> seam, never by a per-site
/// <c>startInfo.Environment[key] = value</c> loop. The defect was duplicated across the exec tool
/// and the MCP stdio transport precisely because each site owned its own merge; a fix that only
/// repairs the two known sites would silently rot the moment a third spawn seam is added or one of
/// these two is reverted, so the constraint is enforced structurally rather than by review.
/// </summary>
public sealed class ProcessEnvironmentMergeCentralizationArchitectureTests : ArchitectureTest
{

    /// <summary>The one file allowed to own the platform casing rule for environment merging.</summary>
    private const string CanonicalHelper = "src/agent/BotNexus.Agent.Core/Tools/ProcessEnvironment.cs";

    /// <summary>The spawn seams named by #2892; both must route through the shared helper.</summary>
    private static readonly string[] SpawnSites =
    [
        "src/extensions/BotNexus.Extensions.ExecTool/ExecTool.cs",
        "src/extensions/BotNexus.Extensions.Mcp/Transport/StdioMcpTransport.cs",
    ];

    /// <summary>
    /// Matches a direct indexer write into a ProcessStartInfo environment block, e.g.
    /// <c>startInfo.Environment[key] = value;</c> - the exact shape #2892 removed.
    /// </summary>
    private static readonly Regex DirectEnvironmentWrite = new(
        @"\.\s*Environment\s*\[[^\]]+\]\s*=",
        RegexOptions.Compiled);

    [Fact]
    public void SharedEnvironmentMergeHelper_Exists()
    {
        var path = ResolvePath(CanonicalHelper);

        File.Exists(path).ShouldBeTrue(
            $"#2892 requires a single shared environment-merge helper at {CanonicalHelper}.");

        var source = File.ReadAllText(path);
        source.Contains("OrdinalIgnoreCase", StringComparison.Ordinal).ShouldBeTrue("The helper must apply the Windows case-insensitive environment rule.");
        source.Contains("IsWindows", StringComparison.Ordinal).ShouldBeTrue("Casing semantics must be selected from the running platform, not hardcoded.");
    }

    [Fact]
    public void SpawnSites_DoNotWriteEnvironmentBlockDirectly()
    {
        var offenders = new List<string>();

        foreach (var relative in SpawnSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue($"Expected spawn site {relative} to exist.");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (DirectEnvironmentWrite.IsMatch(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "Spawn sites must merge caller-supplied environment overrides through " +
            "ProcessEnvironment.Merge, which applies the platform's own key-casing rule " +
            "(OrdinalIgnoreCase on Windows). A direct startInfo.Environment[key] = value write " +
            "lets the caller dictionary's comparer decide collisions, so an override spelled " +
            "'path' does not replace an inherited 'PATH' (#2892)." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void SpawnSites_ReferenceTheSharedHelper()
    {
        foreach (var relative in SpawnSites)
        {
            var source = File.ReadAllText(ResolvePath(relative));

            source.Contains("ProcessEnvironment.Merge", StringComparison.Ordinal).ShouldBeTrue(
                $"{relative} must apply caller-supplied environment overrides through the shared " +
                "ProcessEnvironment.Merge helper (#2892).");
        }
    }

    /// <summary>
    /// Non-vacuity: the direct-write regex must actually match the shape it claims to ban.
    /// A fence whose pattern never matches anything passes for the wrong reason.
    /// </summary>
    [Fact]
    public void DirectEnvironmentWritePattern_MatchesTheBannedShape()
    {
        DirectEnvironmentWrite.IsMatch("            startInfo.Environment[key] = value;").ShouldBeTrue();
        DirectEnvironmentWrite.IsMatch("        startInfo.Environment[key] = ResolveEnvValue(value);").ShouldBeTrue();
        DirectEnvironmentWrite.IsMatch("        ProcessEnvironment.Merge(startInfo.Environment, env);").ShouldBeFalse();
    }

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));

}
