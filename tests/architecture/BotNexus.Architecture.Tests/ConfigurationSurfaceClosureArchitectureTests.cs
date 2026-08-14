using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function closing the configuration boundary (#2887), the half of #2765 AC3
/// that the dependency-direction fence could not reach.
///
/// <para>
/// <b>Why the dependency fence was not enough.</b>
/// <see cref="ConfigurationProjectBoundaryArchitectureTests"/> asserts the assembly graph: where the
/// configuration types live and what they may reference. It says nothing about <em>how</em> a
/// consumer reads a value, and that is exactly the #2764 failure mode - two <c>doctor config</c>
/// checks indexed <c>root["compaction"]</c> while the setting binds at <c>gateway.compaction</c>.
/// The traversal was hand-rolled and wrong, the read was permanently null, and a null is
/// indistinguishable from "not configured". Both a guard that could never fire and a check that
/// reported a healthy platform as broken passed every test they had.
/// </para>
///
/// <para>
/// <b>AC5 is why this file has two independent clauses.</b> Making
/// <see cref="RawConfigPath"/> <c>internal</c> is necessary but not sufficient: if the CLI call
/// sites still <em>expressed</em> raw traversal and merely compiled against a widened surface, then
/// reverting one access modifier would silently restore the whole defect class. So the visibility
/// clause and the call-site clause are asserted separately, against different evidence - reflection
/// for the first, source text for the second. Reverting <c>internal</c> alone reddens only the
/// first; the second still requires someone to reintroduce a raw read at a call site, deliberately.
/// </para>
/// </summary>
public sealed class ConfigurationSurfaceClosureArchitectureTests
{
    private static readonly Assembly ConfigAssembly = typeof(PlatformConfig).Assembly;

    /// <summary>
    /// AC1: the raw traversal primitives are not part of the configuration project's public API.
    /// </summary>
    [Theory]
    [InlineData("BotNexus.Gateway.Configuration.RawConfigPath")]
    [InlineData("BotNexus.Gateway.Configuration.ConfigPathSyntax")]
    public void RawTraversalPrimitives_AreInternal(string typeName)
    {
        var type = ConfigAssembly.GetType(typeName, throwOnError: false);

        type.ShouldNotBeNull(
            $"{typeName} must still exist in {ConfigAssembly.GetName().Name}. If it was renamed, " +
            "update this fence rather than deleting the clause - the invariant is that raw " +
            "traversal is not reachable from outside the configuration project.");

        type.IsPublic.ShouldBeFalse(
            $"{typeName} must be internal to BotNexus.Gateway.Configuration (#2887 AC1). Making it " +
            "public again re-opens the surface that let #2764 happen: a consumer that can express a " +
            "traversal can express a wrong one, and a wrong traversal returns null, which reads " +
            "exactly like 'not configured'. Consumers address configuration through ConfigDocument.");
    }

    /// <summary>
    /// AC2 + AC5: no file under the CLI performs raw config-document indexing.
    ///
    /// <para>
    /// This is the non-vacuity clause. It reads SOURCE, not the type graph, precisely so that
    /// widening <see cref="RawConfigPath"/> back to <c>public</c> cannot satisfy it. A call site
    /// that indexes a config JSON node - <c>root["gateway"]</c>, <c>config["agents"]</c> - fails
    /// here regardless of what any access modifier says.
    /// </para>
    /// </summary>
    [Fact]
    public void CliFiles_DoNotPerformRawConfigDocumentIndexing()
    {
        var cliRoot = ResolveRepoPath("src", "gateway", "BotNexus.Cli");
        Directory.Exists(cliRoot).ShouldBeTrue($"expected the CLI project at {cliRoot}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Comments are stripped first. This fence must not forbid *documenting* the defect it
            // prevents - ConfigChecks.cs explains #2764 by quoting the wrong traversal verbatim, and
            // a rule that punished that explanation would delete the reason the rule exists.
            var text = StripComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(cliRoot, file);

            foreach (var pattern in ForbiddenPatterns)
            {
                foreach (Match match in pattern.Expression.Matches(text))
                {
                    var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{relative}:{line} - {pattern.Description} ({match.Value.Trim()})");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "No file under src/gateway/BotNexus.Cli may read or write the config document by raw " +
            "JSON traversal (#2887 AC2). Offenders:\n  " + string.Join("\n  ", offenders) +
            "\n\nUse the canonical-path surface instead: ConfigDocument.TryGetString / GetBool / " +
            "GetInt / TrySet / TryPatchEntry / TryRemoveEntry, reached via CliConfigMutation. An " +
            "unrecognised path there is an explicit failure rather than a null that reads " +
            "identically to 'not configured' - which is the whole point (#2764).");
    }

    /// <summary>
    /// AC5, stated positively: the migrated call sites genuinely use the canonical surface, so the
    /// previous clause is passing because the reads moved and not because the CLI stopped reading
    /// configuration at all. A fence that would pass on an empty directory proves nothing (#2700).
    /// </summary>
    [Fact]
    public void CliFiles_ActuallyUseTheCanonicalSurface()
    {
        var cliRoot = ResolveRepoPath("src", "gateway", "BotNexus.Cli");

        var users = Directory
            .EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("ConfigDocument", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // The files the issue's measured call-site table names, plus the check contract they flow
        // through. If a migration is reverted, the file drops out of this set.
        //
        // SatelliteCommand.cs is deliberately absent: its writes go through
        // PlatformConfigWriter.MutateDocumentAsync with an implicitly-typed lambda parameter, so it
        // never names ConfigDocument. Its migration is pinned by the raw-indexing clause above,
        // which previously matched three times in that file.
        foreach (var expected in new[]
                 {
                     "ConfigChecks.cs",
                     "FeatureFlagChecks.cs",
                     "ConfigAdvisories.cs",
                     "InitCommand.cs",
                     "GatewayBindAddress.cs",
                     "CliConfigMutation.cs",
                 })
        {
            users.ShouldContain(expected,
                $"{expected} must address configuration through ConfigDocument. Its absence means " +
                "the migration was reverted, which would make the raw-indexing fence above vacuous.");
        }
    }

    /// <summary>
    /// Blanks out line and block comments, preserving newlines so reported line numbers still point
    /// at the offending code. Crude by design: it is scanning for a code shape, not parsing C#.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(
            source,
            @"/\*.*?\*/",
            match => Regex.Replace(match.Value, @"[^\n]", " "),
            RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"//[^\n]*", match => new string(' ', match.Value.Length));
    }

    private sealed record ForbiddenPattern(Regex Expression, string Description);

    private static readonly ForbiddenPattern[] ForbiddenPatterns =
    [
        // root["gateway"], config["agents"], candidate["cron"] ... - the #2764 shape verbatim.
        new(new Regex(@"\b(root|config|configuration|candidate|rootNode|document)\s*\[\s*""",
                RegexOptions.Compiled),
            "raw string-key indexing of a config document"),

        // `as JsonObject` / `is JsonObject` on a config node: the cast that precedes the indexing.
        new(new Regex(@"\b(root|config|configuration|candidate|rootNode|document)\s*\[[^\]]*\]\s*(as|is)\s+Json",
                RegexOptions.Compiled),
            "casting a config document node to a JSON node type"),

        // Direct use of the now-internal primitives, in case InternalsVisibleTo is ever widened.
        new(new Regex(@"\bRawConfigPath\s*\.", RegexOptions.Compiled),
            "direct RawConfigPath use"),

        new(new Regex(@"\bConfigPathSyntax\s*\.", RegexOptions.Compiled),
            "direct ConfigPathSyntax use"),
    ];

    /// <summary>
    /// Walks up from the test binary to the repository root. Reading source is the point of this
    /// fence, so the path must resolve from wherever the runner puts the assembly.
    /// </summary>
    private static string ResolveRepoPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("could not locate the repository root (no BotNexus.slnx found above the test binary)");

        return Path.Combine([directory.FullName, .. segments]);
    }
}
