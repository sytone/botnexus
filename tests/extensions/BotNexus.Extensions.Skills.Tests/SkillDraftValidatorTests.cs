using BotNexus.Extensions.Skills.Recording;

namespace BotNexus.Skills.Tests;

/// <summary>
/// The checks that make a proposal answerable rather than merely plausible.
/// </summary>
/// <remarks>
/// The measurement that shaped this feature said a trace cannot identify parameters. It can still
/// say whether a claimed value occurred, and that is the one job asked of it here. These tests pin
/// the difference: the validator never guesses what should be a parameter, and never accepts a
/// parameter whose value the run did not produce.
/// </remarks>
public sealed class SkillDraftValidatorTests
{
    private static RecordedStep Step(int ordinal, string tool, string args) => new()
    {
        Ordinal = ordinal,
        ToolName = tool,
        ArgumentsJson = args,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static IReadOnlyList<RecordedStep> RadarrRun() =>
    [
        Step(1, "bash", """{"command":"curl -s http://nas:7878/api/v3/movie -d '{\"title\":\"Dune\"}'"}"""),
        Step(2, "bash", """{"command":"curl -s http://nas:7878/api/v3/queue"}""")
    ];

    private static DraftParameter Parameter(string name, string observed, string description = "Varies per run.")
        => new() { Name = name, ObservedValue = observed, Description = description };

    [Fact]
    public void Validate_AcceptsAProposalWhoseValuesAppearInTheRun()
    {
        var content = "Add {{title}} to Radarr at http://nas:7878.";
        var result = SkillDraftValidator.Validate(content, [Parameter("title", "Dune")], RadarrRun());

        result.IsValid.ShouldBeTrue(); // errors: see result.Errors
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RejectsAParameterValueThatAppearsInNoRecordedCall()
    {
        // The anti-fabrication check, and the reason the trace is read at all. An agent describing
        // a run it did not have is exactly what the post-turn claim auditor (#1600) exists to catch;
        // a skill built on such a claim would be wrong in a way nothing downstream could detect.
        var content = "Add {{title}} to Radarr.";
        var result = SkillDraftValidator.Validate(content, [Parameter("title", "Arrival")], RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("appears in no recorded tool call"));
    }

    [Fact]
    public void Validate_AcceptsAValueThatOnlyAppearsJsonEscapedInTheArguments()
    {
        // The run's arguments embed a quoted JSON body, so "Dune" is stored as \"Dune\". A raw-only
        // comparison would reject honest proposals for precisely the values worth parameterising.
        var steps = new[]
        {
            Step(1, "bash", """{"command":"curl -d '{\"path\":\"C:\\media\\films\"}'"}""")
        };

        var result = SkillDraftValidator.Validate(
            "Write to {{target}}.", [Parameter("target", @"C:\media\films")], steps);

        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RejectsADeclaredParameterWithNoPlaceholderInTheBody()
    {
        // A slot declared but never placed looks live in the listing and changes nothing when
        // supplied — the skill silently replays with the value baked in.
        var result = SkillDraftValidator.Validate(
            "Add Dune to Radarr.", [Parameter("title", "Dune")], RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("supplying it would change nothing"));
    }

    [Fact]
    public void Validate_RejectsAPlaceholderWithNoDeclarationBehindIt()
    {
        // The mirror failure: the installed skill would carry the literal text "{{quality}}", which
        // a replay reads as an instruction.
        var result = SkillDraftValidator.Validate(
            "Add {{title}} at {{quality}}.", [Parameter("title", "Dune")], RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no parameter 'quality' is declared"));
    }

    [Fact]
    public void Validate_RejectsAParameterWithNoDescription()
    {
        var result = SkillDraftValidator.Validate(
            "Add {{title}}.", [Parameter("title", "Dune", description: "  ")], RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("has no description"));
    }

    [Fact]
    public void Validate_RejectsDuplicateParameterNames()
    {
        var result = SkillDraftValidator.Validate(
            "Add {{title}}.",
            [Parameter("title", "Dune"), Parameter("title", "Dune")],
            RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("declared more than once"));
    }

    [Fact]
    public void Validate_RejectsASlotNameThatCannotBeSubstituted()
    {
        var result = SkillDraftValidator.Validate(
            "Add {{Title Case}}.", [Parameter("Title Case", "Dune")], RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("not usable as a slot"));
    }

    [Fact]
    public void Validate_RejectsAProposalFromASessionThatRanNothing()
    {
        // Prose with no run behind it is not a recording, and the whole premise of this feature is
        // that the literals come from something that happened.
        var result = SkillDraftValidator.Validate("Do the thing.", [], []);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("recorded no tool calls"));
    }

    [Fact]
    public void Validate_WarnsRatherThanVerifyingWhenTheValueIsTooShortToCheck()
    {
        // "4" occurs by chance in almost any JSON, so the trace check cannot fail. Saying so is
        // better than applying a check that means nothing and reporting it as verified.
        var result = SkillDraftValidator.Validate(
            "Set quality to {{q}}.", [Parameter("q", "4")], RadarrRun());

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("accepted unverified"));
    }

    [Fact]
    public void Validate_ReportsLiteralsFromTheRunThatWereLeftHardCoded()
    {
        // The other half of the confirm question. The host really did occur in the run and really
        // is in the instructions, and the agent has decided it is part of the skill rather than
        // this run's specifics — a decision the operator should get to see and overrule.
        var content = "Add {{title}} to Radarr at http://nas:7878.";
        var result = SkillDraftValidator.Validate(content, [Parameter("title", "Dune")], RadarrRun());

        result.UnparameterisedLiterals.ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_DoesNotOfferAParameterisedValueBackAsAFixedLiteral()
    {
        // "Dune" is in the run AND in the body, so the literal scan sees it — but it is already the
        // parameter's observed value. Listing it would ask the operator to rule on a decision the
        // proposal has plainly already made, and train them to skim the list.
        var content = "Add {{title}} to Radarr. This run added Dune.";
        var result = SkillDraftValidator.Validate(content, [Parameter("title", "Dune")], RadarrRun());

        result.UnparameterisedLiterals.ShouldNotContain("Dune");
    }

    [Fact]
    public void Validate_ReportsTheHostTheInstructionsHardCoded()
    {
        // The case the list exists for, and the one a trace-first scan misses entirely: the body
        // names a host that really appeared in the run, so it is a constant by decision.
        var content = "Add {{title}} to Radarr at http://nas:7878.";
        var result = SkillDraftValidator.Validate(content, [Parameter("title", "Dune")], RadarrRun());

        result.UnparameterisedLiterals.ShouldContain("http://nas:7878");
    }

    [Fact]
    public void Validate_RefusesAnUppercaseSlotName_SoACaseCollisionCannotBeProposed()
    {
        // A recorded proposal cannot create the case ambiguity at all: slot names must be lowercase,
        // so "Title" is refused before the duplicate check is even reached. The frontmatter parser
        // folds case, so a file declaring both "title" and "Title" would keep one and lose the
        // other — this is where that is made unreachable rather than merely detected.
        //
        // The LOADER is still case-insensitive, and deliberately so: hand-authored skills predate
        // this tool and are not bound by its naming rule. The recorder is narrow, the loader is
        // tolerant, and neither is inconsistent with the other.
        var result = SkillDraftValidator.Validate(
            "{{title}} {{Title}}",
            [Parameter("title", "Dune"), Parameter("Title", "Dune")],
            RadarrRun());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("not usable as a slot"));
    }

    [Fact]
    public void Substitute_ReplacesEveryDeclaredSlotAndLeavesUnknownOnesAlone()
    {
        var filled = SkillDraftValidator.Substitute(
            "Add {{title}} at {{quality}}; keep {{title}}.",
            new Dictionary<string, string> { ["title"] = "Dune" });

        filled.ShouldBe("Add Dune at {{quality}}; keep Dune.");
    }

    [Fact]
    public void PlaceholdersIn_ListsEachSlotOnce()
    {
        SkillDraftValidator
            .PlaceholdersIn("{{a}} then {{b}} then {{a}}")
            .ShouldBe(["a", "b"]);
    }
}
