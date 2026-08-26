using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #3099 — enforces the <b>Strongly Typed IDs and Value Objects</b> convention documented in
/// <c>AGENTS.md</c> for the three hot identifiers: a parameter declared as
/// <c>string agentId</c>, <c>string conversationId</c> or <c>string sessionId</c> in
/// <b>non-boundary</b> code must instead carry the corresponding value object from
/// <c>src/domain/BotNexus.Domain/Primitives/</c> — <c>AgentId</c>, <c>ConversationId</c>,
/// <c>SessionId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "boundary" means here — stated, not inferred from the exemption list (AC2).</b>
/// Boundary code is code that sits on a serialisation edge, where the identifier arrives as, or
/// departs as, an untyped wire value and converting it would only move the conversion one frame
/// outward without removing a single transposition risk. Exactly four kinds of edge qualify:
/// </para>
/// <list type="number">
///   <item><description><b>HTTP controllers and their request/response DTOs</b> — a path segment
///   or a JSON body field is a string on the wire by definition. Matched by a
///   <c>/Controllers/</c> path segment, a <c>*Controller.cs</c> file name, or a
///   <c>*Request.cs</c> / <c>*Response.cs</c> / <c>*Dto.cs</c> / <c>*Contracts.cs</c> file
///   name.</description></item>
///   <item><description><b>Persistence column reads and writes</b> — SQLite stores TEXT; the
///   mapper that lifts a column into the domain is the conversion point, so it necessarily
///   handles the primitive. Matched by a <c>Sqlite*.cs</c> file name.</description></item>
///   <item><description><b>Channel wire formats</b> — SignalR hub methods, Telegram/Agent365
///   payloads and the Blazor client that mirrors them are shaped by an external protocol, not by
///   this codebase. Matched by the <c>src/extensions/BotNexus.Extensions.Channels.*</c> project
///   prefix.</description></item>
///   <item><description><b>Command-line argument binding</b> — CLI options are user-typed strings
///   parsed at the process edge. Matched by the <c>src/gateway/BotNexus.Cli/</c> project
///   prefix.</description></item>
/// </list>
/// <para>
/// Everything else — services, stores, notifiers, trackers, tool implementations and the
/// contracts they are declared on — is non-boundary and must carry the typed value.
/// </para>
/// <para>
/// <b>Why a fence and not a Roslyn analyser.</b> Measured on the branch point: 500 matching
/// parameter declarations across 116 files in <c>src/</c>, of which 386 sit on the boundary
/// edges above and 114 (in 62 files) do not. A sweep of 114 non-boundary sites cascades into
/// every caller and is not reviewable in one PR, so the rule has to ship with a frozen baseline
/// regardless of mechanism. Given that, a new analyser project — with its own package reference,
/// its own packaging story and its own maintenance surface — buys IDE-time feedback for one
/// convention at a cost far above a reflection-free file fence that runs in the gate that already
/// exists. This mirrors the <c>ProviderCapabilities</c> baseline pattern from #2432.
/// </para>
/// <para>
/// <b>The baseline is shrink-only and fails in BOTH directions (AC4).</b> A one-directional
/// baseline is a permanent licence: the listed files keep their violations forever and nobody
/// notices when one is fixed. So a count ABOVE the baseline fails (a new violation landed), a
/// count BELOW it fails (the baseline is stale and must be lowered — the sweep only ever
/// ratchets down), a baseline entry that no longer carries the shape at all fails, and a
/// baseline entry naming a file that no longer exists fails.
/// </para>
/// </remarks>
public sealed class PrimitiveIdParameterFenceArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The three identifiers under enforcement, mapped to the value object the failure message
    /// must name (AC1) so the fix is stated, not left as an exercise.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ValueObjectForParameter =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agentId"] = "BotNexus.Domain.Primitives.AgentId",
            ["conversationId"] = "BotNexus.Domain.Primitives.ConversationId",
            ["sessionId"] = "BotNexus.Domain.Primitives.SessionId",
        };

    /// <summary>
    /// Matches a <c>string</c> / <c>string?</c> declaration of one of the three identifier names.
    /// The lookbehind stops <c>SomeType.string</c>-like false positives and, critically, stops
    /// matching inside a longer identifier.
    /// </summary>
    private static readonly Regex PrimitiveIdDeclaration = new(
        @"(?<![A-Za-z0-9_.])string\??\s+(?<name>agentId|conversationId|sessionId)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string BaselineFileName = "PrimitiveIdParameterBaseline.baseline";

    /// <summary>
    /// AC1 + AC4 (upward direction): no non-boundary file may carry more primitive-ID parameter
    /// declarations than its frozen baseline allows, and a file absent from the baseline may carry
    /// none at all.
    /// </summary>
    [Fact]
    public void NonBoundaryCode_IntroducesNoNewPrimitiveIdParameters()
    {
        var baseline = ReadBaseline();
        var actual = ScanNonBoundaryViolations();

        var offenders = new List<string>();
        foreach (var (path, sites) in actual.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var allowed = baseline.TryGetValue(path, out var count) ? count : 0;
            if (sites.Count <= allowed)
                continue;

            var names = sites
                .Select(site => site.ParameterName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);

            var fixes = string.Join(", ", names.Select(name => $"'{name}' -> {ValueObjectForParameter[name]}"));

            offenders.Add(
                $"{path}: {sites.Count} primitive ID parameter(s), baseline allows {allowed}. " +
                $"Use the value object instead: {fixes}. " +
                "Offending lines: " +
                string.Join("; ", sites.Skip(allowed).Select(site => $"L{site.Line} {site.Text}")));
        }

        offenders.ShouldBeEmpty(
            "Non-boundary code must carry the strongly-typed identifier value objects from " +
            "src/domain/BotNexus.Domain/Primitives/, not primitive strings (#3099). " +
            "If the parameter really is on a boundary (controller/DTO, SQLite column read, channel " +
            "wire format, CLI argument) see the boundary definition in this class's doc comment — " +
            "do NOT simply add the file to the baseline. Violations:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// AC4 (downward direction): the baseline is shrink-only. An entry whose file no longer
    /// carries the shape, whose file no longer exists, or whose real count has dropped below the
    /// recorded number is stale and must be updated — otherwise the list quietly becomes a
    /// permanent licence for files that were fixed years ago.
    /// </summary>
    [Fact]
    public void PrimitiveIdBaseline_HasNoStaleEntries()
    {
        var baseline = ReadBaseline();
        var actual = ScanNonBoundaryViolations();

        var stale = new List<string>();
        foreach (var (path, allowed) in baseline.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var absolute = Path.Combine(Repository.Root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                stale.Add($"{path}: baseline entry names a file that no longer exists.");
                continue;
            }

            var count = actual.TryGetValue(path, out var sites) ? sites.Count : 0;
            if (count == 0)
                stale.Add($"{path}: baseline allows {allowed} but the file no longer carries any primitive ID parameter. Remove the entry.");
            else if (count < allowed)
                stale.Add($"{path}: baseline allows {allowed} but only {count} remain. Lower the entry to {count}.");
        }

        stale.ShouldBeEmpty(
            "The #3099 primitive-ID baseline is shrink-only: once a site is fixed the baseline must " +
            "be lowered in the same change, or the entry becomes a standing licence to reintroduce " +
            "the violation. Stale entries:" +
            Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// Non-vacuity guard (AC3, structural half): the scanner must actually find the shape it is
    /// looking for. If a refactor moves <c>src/</c> or breaks the regex, both rules above would go
    /// green by finding nothing — the classic vacuous fence. This asserts the scan has teeth by
    /// requiring it to match a synthetic non-boundary sample.
    /// </summary>
    [Fact]
    public void Scanner_MatchesAViolatingDeclaration_AndIgnoresATypedOne()
    {
        var violating = MatchesInText(
            "    public Task SendAsync(string agentId, string conversationId, CancellationToken ct);");
        violating.Select(site => site.ParameterName).ShouldBe(["agentId", "conversationId"]);

        var typed = MatchesInText(
            "    public Task SendAsync(AgentId agentId, ConversationId conversationId, CancellationToken ct);");
        typed.ShouldBeEmpty("a declaration that already uses the value objects must not be flagged.");

        var commented = MatchesInText("    // public Task SendAsync(string agentId);");
        commented.ShouldBeEmpty("commented-out code must not be flagged.");

        MatchesInText("        var key = BuildKey(agentId, conversationId);")
            .ShouldBeEmpty("an ARGUMENT is not a parameter DECLARATION; only declarations are fenced.");
    }

    /// <summary>
    /// The boundary classification is itself a rule with a stated definition (AC2), so it gets a
    /// test rather than living only as prose that can drift from the code beneath it.
    /// </summary>
    [Fact]
    public void BoundaryClassification_MatchesTheStatedDefinition()
    {
        IsBoundary("src/gateway/BotNexus.Gateway.Api/Controllers/AgentsController.cs").ShouldBeTrue();
        IsBoundary("src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs").ShouldBeTrue();
        IsBoundary("src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs").ShouldBeTrue();
        IsBoundary("src/gateway/BotNexus.Cli/Commands/AgentCommands.cs").ShouldBeTrue();
        IsBoundary("src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/AgentConfigContracts.cs").ShouldBeTrue();

        IsBoundary("src/gateway/BotNexus.Memory/MemoryIndexer.cs").ShouldBeFalse();
        IsBoundary("src/gateway/BotNexus.Gateway/Sessions/SessionTurnTracker.cs").ShouldBeFalse();
        IsBoundary("src/gateway/BotNexus.Gateway.Contracts/Events/IWorldEventBus.cs").ShouldBeFalse();
    }

    /// <summary>
    /// The baseline file must be well formed and ordered, so a diff against it is readable and a
    /// merge conflict is a real conflict rather than a reordering artefact.
    /// </summary>
    [Fact]
    public void PrimitiveIdBaseline_IsWellFormedAndSorted()
    {
        var paths = ReadBaseline().Keys.ToArray();
        paths.ShouldBe(paths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            "baseline entries must be sorted by path.");
        paths.Distinct(StringComparer.Ordinal).Count().ShouldBe(paths.Length, "baseline paths must be unique.");
    }

    private readonly record struct ViolationSite(int Line, string ParameterName, string Text);

    private Dictionary<string, List<ViolationSite>> ScanNonBoundaryViolations()
    {
        var results = new Dictionary<string, List<ViolationSite>>(StringComparer.Ordinal);

        foreach (var absolute in Directory.EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Relative(absolute);
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;
            if (IsBoundary(relative))
                continue;

            var sites = MatchesInLines(File.ReadAllLines(absolute));
            if (sites.Count > 0)
                results[relative] = sites;
        }

        return results;
    }

    private static List<ViolationSite> MatchesInText(string text) => MatchesInLines(text.Split('\n'));

    /// <summary>
    /// Finds primitive ID parameter DECLARATIONS. A match only counts when the text immediately
    /// before it ends the previous parameter or opens the list — <c>(</c>, <c>,</c>, <c>[</c>, or
    /// nothing at all (a wrapped parameter on its own line). That distinguishes a declaration from
    /// an argument at a call site, which is not what this rule is about.
    /// </summary>
    private static List<ViolationSite> MatchesInLines(IReadOnlyList<string> lines)
    {
        var sites = new List<ViolationSite>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                continue;

            foreach (Match match in PrimitiveIdDeclaration.Matches(line))
            {
                var before = line[..match.Index].TrimEnd();
                if (before.Length != 0 && !before.EndsWith('(') && !before.EndsWith(',') && !before.EndsWith('['))
                    continue;

                sites.Add(new ViolationSite(index + 1, match.Groups["name"].Value, Truncate(trimmed)));
            }
        }

        return sites;
    }

    private static string Truncate(string text) => text.Length <= 110 ? text : text[..110];

    /// <summary>
    /// Implements the four boundary kinds enumerated in this class's doc comment. Kept as one
    /// function so the prose above has exactly one place to drift from, guarded by
    /// <see cref="BoundaryClassification_MatchesTheStatedDefinition"/>.
    /// </summary>
    private static bool IsBoundary(string relativePath)
    {
        var fileName = relativePath[(relativePath.LastIndexOf('/') + 1)..];

        // 1. HTTP controllers and their request/response DTOs.
        if (relativePath.Contains("/Controllers/", StringComparison.Ordinal)
            || fileName.EndsWith("Controller.cs", StringComparison.Ordinal)
            || fileName.EndsWith("Request.cs", StringComparison.Ordinal)
            || fileName.EndsWith("Response.cs", StringComparison.Ordinal)
            || fileName.EndsWith("Dto.cs", StringComparison.Ordinal)
            || fileName.EndsWith("Contracts.cs", StringComparison.Ordinal))
            return true;

        // 2. Persistence column reads/writes.
        if (fileName.StartsWith("Sqlite", StringComparison.Ordinal))
            return true;

        // 3. Channel wire formats.
        if (relativePath.StartsWith("src/extensions/BotNexus.Extensions.Channels.", StringComparison.Ordinal))
            return true;

        // 4. Command-line argument binding.
        if (relativePath.StartsWith("src/gateway/BotNexus.Cli/", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static Dictionary<string, int> ReadBaseline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BaselineFileName);
        File.Exists(path).ShouldBeTrue($"The #3099 baseline '{BaselineFileName}' must be copied to the test output directory.");

        var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split('|');
            parts.Length.ShouldBe(2, $"Malformed baseline line: '{raw}'. Expected 'relative/path.cs|count'.");
            var count = int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            baseline[parts[0].Trim()] = count;
            order.Add(parts[0].Trim());
        }

        // Preserve file order for the sortedness check.
        return order.ToDictionary(key => key, key => baseline[key], StringComparer.Ordinal);
    }


    private string Relative(string absolute) =>
        Path.GetRelativePath(Repository.Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

}
