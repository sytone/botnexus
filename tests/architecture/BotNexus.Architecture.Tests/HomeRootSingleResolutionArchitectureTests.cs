using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// AC4 of #2836: every file-backed store must resolve its root through the verified BotNexus home,
/// and none may construct a home path of its own.
/// </summary>
/// <remarks>
/// <para><b>Why a source fence and not a unit test.</b> The defect is a path that is <i>never</i>
/// resolved through the guard. No behavioural test can observe a store that quietly built its own
/// <c>~/.botnexus</c>, because such a store works perfectly - it simply works against the wrong
/// directory, which is precisely what #2819 did for days. The only thing that can catch the
/// thirteenth store added tomorrow is a rule over the source text.</para>
/// <para><b>What counts as a violation.</b> Combining a user-profile (or <c>HOME</c>) path with the
/// literal <c>.botnexus</c> directory name. That two-part shape is the exact expression the #2819
/// fallback used, and it is the only way to name a home root without going through
/// <c>BotNexusHome</c>.</para>
/// <para><b>The allowlist is exact, not a prefix.</b> Each entry is a file that legitimately owns a
/// home derivation - the resolver itself, and CLI entry points that must locate a home before any
/// resolver exists. A new name appearing here must be justified in review, not absorbed by a wildcard.
/// </para>
/// </remarks>
public sealed class HomeRootSingleResolutionArchitectureTests
{
    /// <summary>
    /// Files permitted to derive a home root from the user profile. Every one of these is either the
    /// canonical resolver or a pre-DI entry point that cannot consume it.
    /// </summary>
    private static readonly HashSet<string> PermittedHomeDerivations = new(StringComparer.OrdinalIgnoreCase)
    {
        // THE canonical resolver. The one derivation the design allows.
        "BotNexusHome.cs",

        // CLI + cron entry points that must locate a home before any DI container exists. They are
        // in scope for a follow-up that routes them through the resolver; pinning them here means the
        // count cannot grow silently in the meantime.
        "CliPaths.cs",
        "GatewayProcessManager.cs",
        "MemoryDreamingCronAction.cs",
        "CronServiceCollectionExtensions.cs",
        "CronOptionsPromptTemplateResolver.cs",
        "PluginsEndpointContributor.cs",
        "SkillManagerToolContributor.cs",
        "SkillPromptHookHandler.cs",
        "SkillsEndpointContributor.cs",
        "SkillsServiceContributor.cs",
        "SkillsToolContributor.cs",
        "AgentBrowserBinaryResolver.cs"
    };

    private static readonly Regex HomeLiteral = new(
        @"""\.botnexus""",
        RegexOptions.Compiled);

    /// <summary>
    /// No file outside the allowlist may name the <c>.botnexus</c> home directory literal.
    /// </summary>
    [Fact]
    public void NoNewFile_DerivesTheHomeRootItself()
    {
        var offenders = EnumerateSourceFiles()
            .Where(file => !PermittedHomeDerivations.Contains(Path.GetFileName(file)))
            .Where(file => HomeLiteral.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These files construct a BotNexus home path instead of consuming the verified home from " +
            "BotNexusHome (#2836 AC4): " + string.Join(", ", offenders) + ". A store that resolves its " +
            "own home has never been checked against this world's sentinel, so it is exactly where the " +
            "guard would have a hole. Take a BotNexusHome / IVerifiedHome dependency instead.");
    }

    /// <summary>
    /// The allowlist is a debt ledger, not a parking space: it must not grow. A new entry means a new
    /// unguarded home derivation, which is the defect this fence exists to prevent.
    /// </summary>
    [Fact]
    public void TheAllowlist_DoesNotGrow()
        => PermittedHomeDerivations.Count.ShouldBe(13,
            "Adding a file to PermittedHomeDerivations adds an unguarded home derivation. If you are " +
            "removing one because it now consumes the verified home, lower this number in the same " +
            "commit - that is the ratchet working as intended.");

    /// <summary>
    /// Non-vacuity: the fence must actually be reading source. A rule that scans zero files passes
    /// forever and reads as a clean result - the #2700 shape.
    /// </summary>
    [Fact]
    public void TheFence_ActuallyScansSource()
    {
        var files = EnumerateSourceFiles().ToList();

        files.Count.ShouldBeGreaterThan(500,
            "the fence found almost no source files, which means it is passing because it is inert, " +
            "not because the codebase is clean.");

        files.ShouldContain(
            file => Path.GetFileName(file).Equals("BotNexusHome.cs", StringComparison.OrdinalIgnoreCase),
            "the canonical resolver must be inside the scanned set, or the allowlist is protecting " +
            "nothing and the pattern may no longer match anything at all.");
    }

    private static IEnumerable<string> EnumerateSourceFiles()
        => Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("could not locate the repository root from the test output directory.");
        return Path.Combine(directory!.FullName, "src");
    }
}
