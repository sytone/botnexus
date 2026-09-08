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
    /// <para>
    /// The list once carried two further entries - <c>SubAgentCommand.cs</c> and
    /// <c>SubAgentWorkspaceCheck.cs</c> - on the grounds that an injected <c>IFileSystem</c> cannot
    /// be served by the framework's PHYSICAL file provider. That reasoning was sound about the file
    /// provider and wrong about the conclusion: the framework also accepts a STREAM
    /// (<c>AddJsonStream</c>), which an injected filesystem can supply. #3824 removed both
    /// exemptions and routed them through <c>IPlatformConfigAccessor</c>, which is why nothing
    /// outside the pipeline appears here any more.
    /// </para>
    /// <para>
    /// Adding an entry back is therefore a deliberate act, not a convenience: an exempted file is a
    /// file that cannot see the SQLite config store.
    /// </para>
    /// </remarks>
    private static readonly string[] AllowedFiles =
    [
        "PlatformConfigurationSources.cs",
        "PlatformConfigLoader.cs",
        "ResilientJsonConfigurationSource.cs",
        "SqliteConfigurationProvider.cs",
        "SqliteConfigurationSource.cs",
        "PlatformConfigAccessor.cs",
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
            "Use IOptionsMonitor<PlatformConfig> (gateway) or IPlatformConfigAccessor (CLI) - " +
            "PlatformConfigAccessor.Shared.Get(configPath) resolves it, and the " +
            "Get(configPath, IFileSystem) overload covers a call site with an injected filesystem " +
            "(#3824).\n" +
            "Offending files:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The two call sites #3824 migrated must stay migrated. The generic fence above would catch a
    /// literal reintroduction of <c>PlatformConfigLoader.Load</c>, but this names them and the
    /// accessor, so the mutation in acceptance criterion 5 fails a test whose message says what to
    /// do rather than one that merely lists a path.
    /// </summary>
    [Theory]
    [InlineData("SubAgentCommand.cs")]
    [InlineData("SubAgentWorkspaceCheck.cs")]
    public void SubAgentConfigReads_GoThroughTheAccessor(string fileName)
    {
        var file = ProductionSourceFiles()
            .SingleOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

        file.ShouldNotBeNull($"{fileName} must exist for this fence to mean anything");

        var text = File.ReadAllText(file);

        DirectLoad.IsMatch(text).ShouldBeFalse(
            $"{fileName} must not call PlatformConfigLoader.Load/LoadAsync: that read cannot see the " +
            "SQLite config store and yields an all-defaults PlatformConfig when config.json is " +
            "absent (#3824). Use PlatformConfigAccessor.Shared.Get(configPath, _fileSystem).");

        text.ShouldContain(
            "PlatformConfigAccessor",
            customMessage: $"{fileName} must resolve configuration through IPlatformConfigAccessor (#3824).");
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
