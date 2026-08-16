using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for clause 5 of #3208: inside <c>src/gateway/BotNexus.Cli</c>,
/// <c>CliText.SafeDisplay</c> is the only permitted way to render an untrusted string, so a
/// bare <c>Markup.Escape</c> anywhere outside <c>CliText.cs</c> is a build failure.
///
/// <para><b>Why a fence and not just a sweep.</b> #2722 added <c>CliText.SafeDisplay</c> and
/// converted three of fifty command files. Nothing failed for the other forty-seven, so the
/// helper sat there while twenty-five files kept calling bare <c>Markup.Escape</c> on values
/// an agent can write into <c>cron.sqlite</c>, <c>sessions.db</c>, or the conversation store -
/// and #3208 had to redo the sweep fourteen months of commits later. The sweep removes today's
/// instances; it does nothing about the next command file, whose author reaches for
/// <c>Markup.Escape</c> because that is what Spectre's own documentation says, ships an OSC-52
/// forwarding path by omission, and passes review because the omission looks like the rest of
/// the codebase used to. A property that must hold at every render site cannot be maintained
/// by fifty independent decisions.</para>
///
/// <para><b>The legitimate remedy is always the same: call
/// <c>CliText.SafeDisplay(value)</c>.</b> It strips terminal control sequences via
/// <c>AnsiStripper</c>, scrubs the lone C0/DEL controls that survive sequence-stripping, and
/// then applies <c>Markup.Escape</c> - so it is strictly stronger than what the offending line
/// does today and never a behaviour regression for legitimate text. There is no "this value is
/// trusted so escaping is enough" case: the fence governs the render call, and a value that is
/// genuinely CLI-authored markup (a colour tag, a status glyph) should not be passed to
/// <c>Markup.Escape</c> at all - escaping it would print the markup literally. If a genuinely
/// new exemption arises, add the file to <see cref="AllowedBareEscapeSites"/> WITH A REASON so
/// the exemption is reviewed rather than assumed; do not relax the pattern.</para>
///
/// <para><b>On the files PR #3200 owns.</b> <c>Commands/AgentCommands.cs</c> and
/// <c>Commands/AgentExecCommand.cs</c> were deliberately left untouched by the #3208 sweep
/// because another PR held them. They need no exemption: #2722 already converted
/// <c>AgentCommands.cs</c>, and <c>AgentExecCommand.cs</c> never called <c>Markup.Escape</c>,
/// so both are already clean and the fence forces no edit into either. Had they been dirty,
/// the honest move would have been an allow-list entry naming the owning PR - NOT a relaxed
/// pattern - because an entry expires loudly via
/// <see cref="EveryAllowListEntry_StillExists_AndStillCallsMarkupEscape"/> while a loosened
/// regex expires silently.</para>
///
/// <para>Source-text based, like <see cref="GatewayClientFactoryFenceArchitectureTests"/>:
/// "which escaping helper did this call site choose" is a property of the source, and the
/// compiled assembly retains no trace of it - both spellings end up as a string.</para>
/// </summary>
public sealed class CliSafeDisplayFenceArchitectureTests
{
    /// <summary>Root of the CLI project this fence governs.</summary>
    private const string CliRoot = "src/gateway/BotNexus.Cli";

    /// <summary>The one file permitted to spell out strip-then-escape.</summary>
    private const string HelperSource = CliRoot + "/CliText.cs";

    /// <summary>
    /// Files permitted to call <c>Markup.Escape</c> directly, each with the reason. Adding an
    /// entry is a deliberate, reviewable act - and a stale entry fails too, so the list cannot
    /// rot into a blanket exemption.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedBareEscapeSites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HelperSource] =
                "The helper itself - the single definition of strip-then-escape that every other " +
                "render site composes through.",
        };

    /// <summary>
    /// A <c>Markup.Escape</c> invocation, however it is qualified (<c>Markup.Escape</c>,
    /// <c>Spectre.Console.Markup.Escape</c>). Word-boundary anchored so <c>SafeDisplay</c>'s
    /// own prose in a comment cannot accidentally match.
    /// </summary>
    private static readonly Regex BareEscape =
        new(@"\bMarkup\s*\.\s*Escape\s*\(", RegexOptions.Compiled);

    private static readonly Regex SafeDisplayUse =
        new(@"\bCliText\s*\.\s*SafeDisplay\s*\(", RegexOptions.Compiled);

    private static string RepoRoot => FindRepoRoot();

    [Fact]
    public void Helper_Exists()
    {
        var path = ResolvePath(HelperSource);
        File.Exists(path).ShouldBeTrue(
            "CliText - the single 'safe to hand to Spectre' definition every CLI render site " +
            $"depends on (#2722, #3208) - is missing. Expected at: {path}");

        SafeDisplayUse.IsMatch("CliText.SafeDisplay(x)").ShouldBeTrue(
            "Vacuity guard: the SafeDisplay detector must match its own canonical spelling.");
    }

    [Fact]
    public void NoBareMarkupEscape_OutsideTheHelper()
    {
        var offenders = EnumerateCliSources()
            .Where(file => BareEscape.IsMatch(File.ReadAllText(file)))
            .Select(ToRepoRelative)
            .Where(relative => !AllowedBareEscapeSites.ContainsKey(relative))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These CLI files call Markup.Escape directly instead of CliText.SafeDisplay: " +
            string.Join(", ", offenders) +
            ".\nMarkup.Escape neutralises Spectre's [] grammar but forwards ANSI/OSC/DCS " +
            "sequences and lone C0 controls to the terminal verbatim, so a stored cron job " +
            "name, conversation title, or session label - all agent-writable - can set the " +
            "operator's clipboard via OSC-52 or overwrite output already printed. " +
            "REMEDY: replace 'Markup.Escape(x)' with 'CliText.SafeDisplay(x)'; it strips first " +
            "and then escapes, so it is strictly stronger and changes nothing for plain text. " +
            "See #3208 clause 5 and #2722.");
    }

    [Fact]
    public void EveryAllowListEntry_StillExists_AndStillCallsMarkupEscape()
    {
        foreach (var (relative, reason) in AllowedBareEscapeSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Allow-listed file no longer exists: {relative} (reason on record: {reason}). " +
                "Remove the entry - a stale allow-list slowly becomes a blanket exemption. See #3208.");

            BareEscape.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' is allow-listed to call Markup.Escape but no longer does. Remove " +
                "the entry so the exemption cannot silently cover a future render site added to " +
                "this file. See #3208.");
        }
    }

    /// <summary>
    /// The positive half of the fence: the enumerated #3208 call sites must actually render
    /// through the helper, so a file that is emptied, renamed, or reverted fails rather than
    /// passing vacuously by simply containing no Markup.Escape.
    /// </summary>
    [Fact]
    public void EveryConvertedCallSite_RendersThroughSafeDisplay()
    {
        string[] convertedCallSites =
        [
            CliRoot + "/Commands/CronCommands.cs",
            CliRoot + "/Commands/ConversationCommands.cs",
            CliRoot + "/Commands/DebugSessionsCommand.cs",
            CliRoot + "/Commands/SessionCommands.cs",
            CliRoot + "/Commands/MemoryCommands.cs",
            CliRoot + "/Commands/PromptCommands.cs",
        ];

        foreach (var relative in convertedCallSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Expected CLI command source not found: {path}. If it was renamed, update this " +
                "list - do not delete the entry without confirming the render seam is gone.");

            SafeDisplayUse.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' renders stored, agent-writable values but never calls " +
                "CliText.SafeDisplay, so its output is no longer sanitised. See #3208 clause 1.");
        }
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsBareEscapeAndDoesNotFlagSafeDisplay()
    {
        const string offendingRenderSite = """
            internal sealed class FiftyFirstCommand
            {
                public void Render(string storedJobName)
                    => AnsiConsole.MarkupLine($"[bold]Name:[/] {Markup.Escape(storedJobName)}");
            }
            """;

        BareEscape.IsMatch(offendingRenderSite).ShouldBeTrue(
            "Vacuity guard: a bare Markup.Escape MUST be detected. If this fails the fence " +
            "matches nothing and the fifty-first command file reintroduces #2722 unnoticed.");

        const string compliantRenderSite = """
            internal sealed class CompliantCommand
            {
                public void Render(string storedJobName)
                    => AnsiConsole.MarkupLine($"[bold]Name:[/] {CliText.SafeDisplay(storedJobName)}");
            }
            """;

        BareEscape.IsMatch(compliantRenderSite).ShouldBeFalse(
            "Positive pin: the sanctioned remedy must NOT be flagged, otherwise correct code " +
            "cannot go green and authors will route around the fence.");
        SafeDisplayUse.IsMatch(compliantRenderSite).ShouldBeTrue(
            "Positive pin: the sanctioned remedy must satisfy the SafeDisplay detector.");

        // Whitespace and full qualification are the obvious evasions; catch both.
        BareEscape.IsMatch("Spectre.Console.Markup . Escape ( value )").ShouldBeTrue(
            "Vacuity guard: whitespace and full qualification must not defeat the detector.");
    }

    /// <summary>
    /// Guards the scan itself: if the CLI source tree moves or empties, every offender query
    /// returns nothing and the fence would pass while enforcing nothing.
    /// </summary>
    [Fact]
    public void Fence_ScansANonEmptyCliSourceTree()
        => EnumerateCliSources().Count().ShouldBeGreaterThan(
            20,
            "Vacuity guard: the CLI source tree scanned by this fence is missing or nearly " +
            "empty, so every assertion above would pass without inspecting anything. Check " +
            $"that '{CliRoot}' still exists relative to the repo root.");

    private static IEnumerable<string> EnumerateCliSources()
    {
        var cliRoot = Path.Combine(RepoRoot, CliRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(cliRoot).ShouldBeTrue($"CLI source root not found: {cliRoot}");
        return Directory.EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories);
    }

    private static string ToRepoRelative(string absolutePath) =>
        Path.GetRelativePath(RepoRoot, absolutePath).Replace('\\', '/');

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
