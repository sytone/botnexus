using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences the single configuration read path (#3504): production code reads platform configuration
/// through <c>IOptions</c>/<c>IOptionsMonitor</c>, never by loading and binding the file itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fence and not a code review.</b> Seventeen hand-rolled load sites accumulated one at a
/// time, each locally reasonable - a CLI command needs config, the loader is right there, it
/// compiles, nothing fails. The cost only appeared in aggregate: fourteen commands that could not
/// see the SQLite store, got no hot reload, and skipped the last-known-good protection in
/// <c>ResilientJsonConfigurationSource</c> (#2358), because all three live in the provider pipeline
/// rather than in a file read.
/// </para>
/// <para>
/// A fence is the only thing that makes the eighteenth fail. The framework can do all of this; the
/// rule is that BotNexus code must let it.
/// </para>
/// </remarks>
public sealed class ConfigurationReadPathFenceTests : ArchitectureTest
{
    /// <summary>
    /// Files permitted to reference the loader: the composition root, the loader itself, and the
    /// provider implementations that make up the pipeline.
    /// </summary>
    /// <remarks>
    /// The last two entries are genuine exemptions rather than pipeline components, and the reason
    /// is worth stating because it bounds how far this refactor can go. Both take an injected
    /// <c>IFileSystem</c>, and the framework's file configuration provider is backed by a PHYSICAL
    /// file provider - it cannot read an injected abstraction. Routing them through the pipeline
    /// would silently read the real disk while the caller supplied a mock, producing configuration
    /// that matches neither. A hand-load through the injected filesystem is the honest option.
    /// </remarks>
    private static readonly string[] AllowedFiles =
    [
        "PlatformConfigurationSources.cs",
        "PlatformConfigLoader.cs",
        "ResilientJsonConfigurationSource.cs",
        "SqliteConfigurationProvider.cs",
        "SqliteConfigurationSource.cs",
        "PlatformConfigAccessor.cs",

        // Injected-IFileSystem exemptions - see the remarks above.
        "SubAgentCommand.cs",
        "SubAgentWorkspaceCheck.cs",
    ];

    private static readonly Regex DirectLoad =
        new(@"PlatformConfigLoader\s*\.\s*Load(Async)?\s*\(", RegexOptions.Compiled);

    private IEnumerable<string> ProductionSourceFiles()
    {
        var srcRoot = Repository.SourceRoot;
        if (!Directory.Exists(srcRoot))
            yield break;

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal))
                continue;

            yield return file;
        }
    }

    [Fact]
    public void NoProductionCode_LoadsPlatformConfigDirectly()
    {
        var offenders = ProductionSourceFiles()
            .Where(f => !AllowedFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => DirectLoad.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(Repository.Root, f).Replace('\\', '/'))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "Platform configuration must be read through IOptions/IOptionsMonitor, fed by the JSON " +
            "and SQLite configuration providers - not by loading and binding the file directly.\n" +
            "A direct load cannot see the SQLite store, gets no hot reload, and skips the " +
            "last-known-good protection in ResilientJsonConfigurationSource (#2358).\n" +
            "Use IOptionsMonitor<PlatformConfig> (gateway) or IPlatformConfigAccessor (CLI).\n" +
            "Offending files:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Non-vacuity: the scan must actually be reading production source. A mis-rooted enumeration
    /// would make the assertion above pass by finding nothing at all, which is indistinguishable
    /// from a clean tree.
    /// </summary>
    [Fact]
    public void Fence_ScansARealProductionTree()
    {
        var files = ProductionSourceFiles().ToList();

        files.Count.ShouldBeGreaterThan(500, "the production source tree should be substantial");
        files.ShouldContain(
            f => Path.GetFileName(f).Equals("GatewayServiceCollectionExtensions.cs", StringComparison.OrdinalIgnoreCase),
            "the scan must reach the gateway registration file, which is where two offenders lived");
        files.ShouldContain(
            f => Path.GetFileName(f).Equals("DoctorCommand.cs", StringComparison.OrdinalIgnoreCase),
            "the scan must reach the CLI, which held fourteen of the seventeen offenders");
    }

    /// <summary>
    /// The allow-list must name files that exist, or an entry silently permits nothing and a real
    /// exemption could be lost in a rename without anyone noticing.
    /// </summary>
    [Fact]
    public void AllowList_NamesOnlyRealFiles()
    {
        var present = ProductionSourceFiles().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = AllowedFiles.Where(a => !present.Contains(a)).ToList();

        missing.ShouldBeEmpty(
            "every allow-listed file must exist; a stale entry exempts nothing and hides a rename.\n" +
            "Missing:\n  " + string.Join("\n  ", missing));
    }
}
