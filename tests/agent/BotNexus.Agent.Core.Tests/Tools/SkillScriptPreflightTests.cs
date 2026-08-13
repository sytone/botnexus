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
        @"C:\Users\jobullen\.botnexus\skills\teams\scripts\ListMessages.ps1";

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
        var path = @"C:\Users\jobullen\.botnexus\skills\teams\scripts\GetChatMessages.ps1";

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
    [InlineData(@"C:\Users\j\.botnexus\skills\teams\scripts\X.ps1", "teams")]
    [InlineData("/home/j/.botnexus/skills/ado-msdata/scripts/X.ps1", "ado-msdata")]
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
