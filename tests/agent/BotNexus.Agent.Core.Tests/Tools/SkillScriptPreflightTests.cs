using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="SkillScriptPreflight"/> (issue #2758). The defect: an agent that guesses
/// a plausible-but-absent wrapper name (<c>ListMessages.ps1</c> when the real one is
/// <c>ListChatMessages.ps1</c>) gets <c>pwsh</c>'s generic usage banner, which names neither the skill
/// nor any candidate - so it has no signal to correct with and retries the identical call.
/// </summary>
/// <remarks>
/// The candidate list must be derived by ENUMERATING the skill's <c>scripts/</c> directory at failure
/// time (AC2), never from a hand-maintained alias table, so a wrapper added later appears
/// automatically. These tests therefore drive enumeration through an injected lister rather than
/// asserting against a fixed set baked into the implementation.
/// </remarks>
public class SkillScriptPreflightTests
{
    // The real teams skill wrapper set observed in the forensics window (issue #2758 Evidence).
    private static readonly string[] TeamsScripts =
    [
        "ListChatMessages.ps1",
        "ListChannelMessages.ps1",
        "GetChatMessage.ps1",
        "SendMessageToChat.ps1",
        "ListChats.ps1",
        "ListTeams.ps1",
        "GetTeam.ps1",
        "CreateChat.ps1",
    ];

    private const string TeamsScriptPath =
        @"C:\Users\username\.botnexus\skills\teams\scripts\ListMessages.ps1";

    private static Func<string, bool> ExistsNever => _ => false;

    private static Func<string, IReadOnlyList<string>> ListsTeams =>
        _ => TeamsScripts;

    // === AC1: a missing wrapper under a skill's scripts/ dir names the skill and the candidates ===

    [Fact]
    public void Validate_MissingTeamsWrapper_NamesTheSkill()
    {
        var message = SkillScriptPreflight.Validate(TeamsScriptPath, ExistsNever, ListsTeams);

        message.ShouldNotBeNull();
        message!.Contains("teams", StringComparison.Ordinal)
            .ShouldBeTrue("the failure must name the skill the wrapper belongs to");
        message.Contains("ListMessages.ps1", StringComparison.Ordinal)
            .ShouldBeTrue("the failure must echo the wrapper name that does not exist");
    }

    [Fact]
    public void Validate_MissingTeamsWrapper_SuggestsBothListMessageWrappers()
    {
        var message = SkillScriptPreflight.Validate(TeamsScriptPath, ExistsNever, ListsTeams);

        message.ShouldNotBeNull();
        message!.Contains("ListChatMessages.ps1", StringComparison.Ordinal)
            .ShouldBeTrue("AC1: ListChatMessages.ps1 is one Levenshtein hop away and must be suggested");
        message.Contains("ListChannelMessages.ps1", StringComparison.Ordinal)
            .ShouldBeTrue("AC1: ListChannelMessages.ps1 must be suggested alongside it");
    }

    [Fact]
    public void Validate_MissingTeamsWrapper_ListsTheEnumeratedScripts()
    {
        var message = SkillScriptPreflight.Validate(TeamsScriptPath, ExistsNever, ListsTeams);

        message.ShouldNotBeNull();
        message!.Contains("SendMessageToChat.ps1", StringComparison.Ordinal)
            .ShouldBeTrue("the actual available wrapper names must be listed, not just near matches");
    }

    [Fact]
    public void Validate_MissingTeamsWrapper_StatesTheNearMatchWasNotExecuted()
    {
        var message = SkillScriptPreflight.Validate(TeamsScriptPath, ExistsNever, ListsTeams);

        message.ShouldNotBeNull();
        message!.Contains("not executed", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue("a near match must be reported, never silently executed in place of the request");
    }

    [Fact]
    public void Validate_GetChatMessagesTypo_SuggestsTheSingularWrapper()
    {
        var path = @"C:\Users\username\.botnexus\skills\teams\scripts\GetChatMessages.ps1";

        var message = SkillScriptPreflight.Validate(path, ExistsNever, ListsTeams);

        message.ShouldNotBeNull();
        message!.Contains("GetChatMessage.ps1", StringComparison.Ordinal)
            .ShouldBeTrue("the singular wrapper is one hop away from the plural guess");
    }

    // === AC2: candidates come from enumeration, so a later-added wrapper appears automatically ===

    [Fact]
    public void Validate_WrapperAddedLater_AppearsWithoutCodeChange()
    {
        var extended = TeamsScripts.Append("ListMessagesForUser.ps1").ToArray();

        var message = SkillScriptPreflight.Validate(
            TeamsScriptPath,
            ExistsNever,
            _ => extended);

        message.ShouldNotBeNull();
        message!.Contains("ListMessagesForUser.ps1", StringComparison.Ordinal)
            .ShouldBeTrue("AC2: candidates are enumerated at failure time, not read from a fixed table");
    }

    [Fact]
    public void Validate_EnumerationYieldsNothing_StillNamesTheSkillWithoutCandidates()
    {
        var message = SkillScriptPreflight.Validate(
            TeamsScriptPath,
            ExistsNever,
            _ => Array.Empty<string>());

        message.ShouldNotBeNull();
        message!.Contains("teams", StringComparison.Ordinal).ShouldBeTrue();
        message.Contains("ListChatMessages.ps1", StringComparison.Ordinal)
            .ShouldBeFalse("no candidate may be invented when the directory enumerates empty");
    }

    // === AC5: outside a skill scripts/ dir, report a plain path-not-found with no candidate list ===

    [Fact]
    public void Validate_NonSkillPath_ReportsPlainNotFoundWithoutCandidates()
    {
        var message = SkillScriptPreflight.Validate(
            @"C:\work\tmp\fq.ps1",
            ExistsNever,
            _ => throw new InvalidOperationException("must not enumerate outside a skill scripts directory"));

        message.ShouldNotBeNull();
        message!.Contains("fq.ps1", StringComparison.Ordinal).ShouldBeTrue();
        message.Contains("Closest matches", StringComparison.Ordinal)
            .ShouldBeFalse("AC5: no bogus candidate list outside a skill directory");
        message.Contains("skill", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("AC5: a non-skill path must not claim skill provenance");
    }

    // === Happy path: an existing script is never rejected ===

    [Fact]
    public void Validate_ScriptExists_ReturnsNull()
    {
        SkillScriptPreflight
            .Validate(TeamsScriptPath, _ => true, ListsTeams)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NoPath_ReturnsNull(string? path)
    {
        SkillScriptPreflight.Validate(path, ExistsNever, ListsTeams).ShouldBeNull();
    }

    [Theory]
    // Unresolvable at preflight time - a variable or a wildcard could expand to a real file, so the
    // preflight must stay silent rather than refuse a legitimate command.
    [InlineData(@"C:\skills\teams\scripts\$name.ps1")]
    [InlineData(@"C:\skills\teams\scripts\*.ps1")]
    [InlineData(@"C:\skills\teams\scripts\$(Get-Name).ps1")]
    public void Validate_UnresolvableTarget_ReturnsNull(string path)
    {
        SkillScriptPreflight.Validate(path, ExistsNever, ListsTeams).ShouldBeNull();
    }

    // === Skill-context detection ===

    [Theory]
    [InlineData(@"C:\Users\username\.botnexus\skills\teams\scripts\X.ps1", "teams")]
    [InlineData("/home/agent/.botnexus/skills/ado-msdata/scripts/X.ps1", "ado-msdata")]
    [InlineData(@"skills\botnexus-maintenance\scripts\New-BotNexusPr.ps1", "botnexus-maintenance")]
    [InlineData("agents/tinker/skills/worktree/scripts/New-DevWorktree.ps1", "worktree")]
    public void DescribeSkillScript_SkillPaths_ResolveTheSkillName(string path, string expected)
    {
        var context = SkillScriptPreflight.DescribeSkillScript(path);

        context.ShouldNotBeNull();
        context!.SkillName.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\work\tmp\fq.ps1")]
    [InlineData("skills/teams/X.ps1")]              // not under scripts/
    [InlineData("other/teams/scripts/X.ps1")]        // grandparent is not "skills"
    [InlineData("scripts/X.ps1")]                    // no skill segment at all
    public void DescribeSkillScript_NonSkillPaths_ReturnNull(string path)
    {
        SkillScriptPreflight.DescribeSkillScript(path).ShouldBeNull();
    }

    // === -File target extraction ===

    [Theory]
    [InlineData(new[] { "-NoProfile", "-File", "a.ps1" }, "a.ps1")]
    [InlineData(new[] { "-nologo", "-file", "b.ps1", "-Arg", "1" }, "b.ps1")]
    public void TryGetFileTarget_FileFlagPresent_ReturnsTarget(string[] args, string expected)
    {
        SkillScriptPreflight.TryGetFileTarget(args, out var path).ShouldBeTrue();
        path.ShouldBe(expected);
    }

    [Theory]
    [InlineData((object)new[] { "-NoProfile", "-Command", "Get-Date" })]
    [InlineData((object)new[] { "-NoProfile", "-File" })]
    [InlineData((object)new string[0])]
    public void TryGetFileTarget_NoUsableFileFlag_ReturnsFalse(string[] args)
    {
        SkillScriptPreflight.TryGetFileTarget(args, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("pwsh -NoProfile -File 'C:\\s\\teams\\scripts\\X.ps1' -Chat 1", "C:\\s\\teams\\scripts\\X.ps1")]
    [InlineData("pwsh -NoProfile -File \"C:\\s\\X.ps1\"", "C:\\s\\X.ps1")]
    [InlineData("pwsh -File ./scripts/X.ps1", "./scripts/X.ps1")]
    public void TryGetFileTargetFromCommandLine_QuotedAndBare_ReturnsTarget(string command, string expected)
    {
        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out var path).ShouldBeTrue();
        path.ShouldBe(expected);
    }

    [Theory]
    [InlineData("pwsh -NoProfile -Command 'Get-Date'")]
    [InlineData("Get-ChildItem -Filter *.ps1")]
    [InlineData("echo '-File not-really.ps1'")]
    [InlineData("pwsh -File")]
    public void TryGetFileTargetFromCommandLine_NoFileTarget_ReturnsFalse(string command)
    {
        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out _).ShouldBeFalse();
    }

    // === Issue #3566: -File extraction must be PARSED, not text-split ===
    //
    // The extractor used to split on whitespace and take the token after the literal text "-File".
    // That is not PowerShell's tokenisation, so any command element terminator following the path -
    // ';', '|', ')' or a redirect - was swallowed into the "path" and the call was refused claiming a
    // file that demonstrably exists does not. 206 of 316 weekly refusals across 22 agents were this
    // false positive. These tests pin clauses 1-7 of the issue.

    [Theory]
    // Clause 1: a statement separator terminates the path token.
    [InlineData("pwsh -NoProfile -File 'C:\\s\\get-token.ps1'; \"len=1\"", "C:\\s\\get-token.ps1")]
    // Clause 2: a pipe terminates the path token.
    [InlineData("pwsh -NoProfile -File 'C:\\s\\get-token.ps1' | Select-Object -First 1", "C:\\s\\get-token.ps1")]
    // Clause 3: a redirect terminates the path token.
    [InlineData("pwsh -NoProfile -File 'C:\\s\\get-token.ps1' 2>&1", "C:\\s\\get-token.ps1")]
    // Clause 4: a call-operator invocation inside a parenthesised assignment still binds cleanly.
    [InlineData("$x = (& pwsh -NoProfile -File 'C:\\s\\get-token.ps1')", "C:\\s\\get-token.ps1")]
    // Issue #3754 (AC3): the pipeline-chain operators. `&&` and `||` were named in #3566's
    // acceptance criteria but never pinned by a test, so nothing prevented a future extractor
    // regression from gluing "&&" onto the path exactly as ";" and "|" once were.
    [InlineData("pwsh -NoProfile -File 'C:\\s\\get-token.ps1' && Write-Output 'ok'", "C:\\s\\get-token.ps1")]
    [InlineData("pwsh -NoProfile -File 'C:\\s\\get-token.ps1' || Write-Output 'fallback'", "C:\\s\\get-token.ps1")]
    // An UNQUOTED path followed by a separator is the shape that dominated the corpus: 671 of the
    // absorbed characters were ';', and an unquoted token is precisely what a text-splitter runs
    // together with the separator that follows it.
    [InlineData("pwsh -NoProfile -File C:\\s\\get-token.ps1; Get-Date", "C:\\s\\get-token.ps1")]
    [InlineData("pwsh -NoProfile -File C:\\s\\get-token.ps1 | Select-Object -First 1", "C:\\s\\get-token.ps1")]
    public void TryGetFileTargetFromCommandLine_TerminatorAfterPath_BindsPathWithoutTheTerminator(
        string command,
        string expected)
    {
        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out var path).ShouldBeTrue();

        path.ShouldBe(expected);

        // Non-vacuity (issue #3566, clause 6): asserting only "a path was bound" would pass even for
        // the defective text-split. The point of the fix is that NO terminator or redirect character
        // can survive into the reported path.
        path.ShouldNotEndWith(";");
        path.ShouldNotEndWith("|");
        path.ShouldNotEndWith(")");
        path.IndexOfAny([';', '|', ')', '(', '>', '<', '&']).ShouldBe(-1);
    }

    [Theory]
    // Clause 5: -File is another command's own switch, not a script path. The defective extractor
    // bound the pipe character here and resolved it against the workspace root.
    [InlineData("Get-ChildItem 'C:\\s' -Filter '*.ps1' -File | Select-Object -First 2")]
    [InlineData("Get-ChildItem -Path 'C:\\s' -File")]
    // A -File on a non-PowerShell executable is equally not a script path.
    [InlineData("git ls-files -File")]
    // Issue #3754 (AC2): the VERBATIM command from the reproduction, character for character,
    // including the -ExpandProperty tail. This is the command that produced
    // "Script not found: <workspace>\|" on the running gateway.
    [InlineData("Get-ChildItem 'C:\\Users\\jobullen\\.botnexus\\scripts' -Filter '*.ps1' -File | Select-Object -First 2 -ExpandProperty Name")]
    // -File as a trailing switch immediately before a chain operator: nothing may be bound from the
    // operator, nor from the command on its right-hand side.
    [InlineData("Get-ChildItem -Recurse -File && Write-Output 'ok'")]
    [InlineData("Get-ChildItem -Recurse -File; Get-Date")]
    public void TryGetFileTargetFromCommandLine_FileSwitchOnAnotherCommand_ReturnsFalse(string command)
    {
        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out var path).ShouldBeFalse();

        path.ShouldBeEmpty();
    }

    [Theory]
    // Clause 7: an unparseable command is allowed to run so the shell reports its own native error.
    [InlineData("pwsh -NoProfile -File 'C:\\s\\x.ps1")]                 // unterminated string
    [InlineData("foreach ($x in $xs) { $x } | Sort-Object")]            // statement piped from
    [InlineData("pwsh -NoProfile -File (Get-Thing 'a'")]                // unclosed parenthesis
    public void TryGetFileTargetFromCommandLine_UnparseableCommand_FailsOpen(string command)
    {
        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out var path).ShouldBeFalse();

        path.ShouldBeEmpty();
    }

    [Theory]
    // A path that is not a literal cannot be probed with confidence, so the preflight must fail open
    // rather than guess - a variable or subexpression can resolve to a real file at run time.
    [InlineData("pwsh -NoProfile -File $scriptPath")]
    [InlineData("pwsh -NoProfile -File (Join-Path $dir 'x.ps1')")]
    public void TryGetFileTargetFromCommandLine_NonLiteralPath_FailsOpen(string command)
    {
        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out var path).ShouldBeFalse();

        path.ShouldBeEmpty();
    }

    [Fact]
    public void TryGetFileTargetFromCommandLine_MissingScriptAfterSeparator_StillRefusesWithACleanPath()
    {
        // Clause 6: a genuinely missing script is STILL refused - the fix must not turn the preflight
        // off - and the path it reports carries no trailing separator.
        const string command = "pwsh -NoProfile -File 'C:\\s\\missing.ps1'; Get-Date";

        SkillScriptPreflight.TryGetFileTargetFromCommandLine(command, out var path).ShouldBeTrue();
        path.ShouldBe("C:\\s\\missing.ps1");

        var message = SkillScriptPreflight.Validate(path, _ => false, _ => Array.Empty<string>());

        message.ShouldNotBeNull();
        message.ShouldContain("C:\\s\\missing.ps1");
        message.ShouldNotContain("missing.ps1;");
    }

    // === Near-match ranking ===

    [Fact]
    public void FindClosestScripts_RanksTheNearestFirst()
    {
        var matches = SkillScriptPreflight.FindClosestScripts("ListMessages.ps1", TeamsScripts, 5);

        matches.ShouldNotBeEmpty();
        matches[0].ShouldBe("ListChatMessages.ps1");
        matches.ShouldContain("ListChannelMessages.ps1");
    }

    [Fact]
    public void FindClosestScripts_UnrelatedName_ReturnsNothing()
    {
        SkillScriptPreflight
            .FindClosestScripts("Zzzqqq.ps1", TeamsScripts, 5)
            .ShouldBeEmpty();
    }

    [Fact]
    public void FindClosestScripts_RespectsTheMaximum()
    {
        SkillScriptPreflight
            .FindClosestScripts("ListMessages.ps1", TeamsScripts, 2)
            .Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void FindClosestScripts_IsDeterministic()
    {
        var first = SkillScriptPreflight.FindClosestScripts("ListMessages.ps1", TeamsScripts, 5);
        var shuffled = TeamsScripts.Reverse().ToArray();
        var second = SkillScriptPreflight.FindClosestScripts("ListMessages.ps1", shuffled, 5);

        second.ShouldBe(first);
    }

    // === ThrowIfMissing ===

    [Fact]
    public void ThrowIfMissing_MissingWrapper_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            SkillScriptPreflight.ThrowIfMissing(TeamsScriptPath, ExistsNever, ListsTeams));

        ex.Message.Contains("ListChatMessages.ps1", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void ThrowIfMissing_ExistingScript_DoesNotThrow()
    {
        Should.NotThrow(() =>
            SkillScriptPreflight.ThrowIfMissing(TeamsScriptPath, _ => true, ListsTeams));
    }
}
