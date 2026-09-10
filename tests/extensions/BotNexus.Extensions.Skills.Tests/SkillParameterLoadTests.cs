using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.Skills;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Loading a parameterised skill: substitution, and the refusals that keep a wrong replay from
/// looking like a right one.
/// </summary>
public sealed class SkillParameterLoadTests
{
    private static SkillDefinition Skill(
        string content,
        IReadOnlyDictionary<string, string>? parameters = null) => new()
        {
            Name = "add-film",
            Description = "Adds a film.",
            Content = content,
            SourcePath = "/skills/add-film",
            Source = SkillSource.Workspace,
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    private static SkillTool Tool(SkillDefinition skill)
        => new([skill], new SkillsConfig());

    private static Dictionary<string, object?> LoadArgs(object? parameters = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["action"] = "load",
            ["skillName"] = "add-film"
        };
        if (parameters is not null)
            args["parameters"] = parameters;
        return args;
    }

    private static string Text(AgentToolResult result)
        => string.Join(string.Empty, result.Content.Select(c => c.Value));

    private static readonly Dictionary<string, string> Declared = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "The film to add."
    };

    [Fact]
    public async Task Load_SubstitutesEveryDeclaredSlot()
    {
        var tool = Tool(Skill("Add {{title}} to Radarr, then confirm {{title}} landed.", Declared));

        var text = Text(await tool.ExecuteAsync("call", LoadArgs(
            new Dictionary<string, object?> { ["title"] = "Dune" })));

        text.ShouldContain("Add Dune to Radarr, then confirm Dune landed.");
        text.ShouldNotContain("{{title}}");
    }

    [Fact]
    public async Task Load_EchoesTheValuesItSubstituted()
    {
        // A filled-in skill should be self-describing: the reader can tell which words came from
        // the caller and which were written into the skill.
        var tool = Tool(Skill("Add {{title}}.", Declared));

        Text(await tool.ExecuteAsync("call", LoadArgs(
                new Dictionary<string, object?> { ["title"] = "Dune" })))
            .ShouldContain("Parameters applied");
    }

    [Fact]
    public async Task Load_WithAMissingParameter_IsRefusedRatherThanSubstitutedBlank()
    {
        // The failure this whole mechanism exists to prevent. Left alone, the instructions would
        // tell the agent to add "{{title}}" — a replay that runs, and is wrong.
        var tool = Tool(Skill("Add {{title}}.", Declared));

        var text = Text(await tool.ExecuteAsync("call", LoadArgs()));

        text.ShouldContain("requires parameter(s) that were not supplied");
        text.ShouldContain("The film to add."); // the refusal says what to supply
    }

    [Fact]
    public async Task Load_RefusedForAMissingParameter_LeavesTheSkillUnloaded()
    {
        // Otherwise the retry with correct values reports "already loaded" and the caller never
        // gets the content — a refusal that costs the thing it was protecting.
        var tool = Tool(Skill("Add {{title}}.", Declared));

        await tool.ExecuteAsync("call", LoadArgs());
        var retry = Text(await tool.ExecuteAsync("call", LoadArgs(
            new Dictionary<string, object?> { ["title"] = "Dune" })));

        tool.SessionLoadedSkills.ShouldContain("add-film");
        retry.ShouldContain("Add Dune.");
    }

    [Fact]
    public async Task Load_WithAnUndeclaredParameter_IsRefused()
    {
        // Usually a renamed slot. Accepting it silently means the caller believes it changed
        // something it did not.
        var tool = Tool(Skill("Add {{title}}.", Declared));

        var text = Text(await tool.ExecuteAsync("call", LoadArgs(
            new Dictionary<string, object?> { ["title"] = "Dune", ["quality"] = "4K" })));

        text.ShouldContain("does not declare: quality");
    }

    [Fact]
    public async Task Load_SupplyingParametersToASkillThatDeclaresNone_IsRefused()
    {
        var tool = Tool(Skill("Add a film."));

        var text = Text(await tool.ExecuteAsync("call", LoadArgs(
            new Dictionary<string, object?> { ["title"] = "Dune" })));

        text.ShouldContain("declares no parameters");
    }

    [Fact]
    public async Task Load_OfAnOrdinarySkillIsUnchangedByAnyOfThis()
    {
        // Every skill that existed before recording did takes this path. It must behave exactly as
        // it always has, including carrying no parameter chatter into the prompt.
        var tool = Tool(Skill("Just do the thing."));

        var text = Text(await tool.ExecuteAsync("call", LoadArgs()));

        text.ShouldContain("Just do the thing.");
        text.ShouldNotContain("Parameters applied");
    }

    [Fact]
    public async Task List_NamesTheParametersASkillRequires()
    {
        // Otherwise they are discoverable only by being refused, which costs a whole turn.
        var tool = Tool(Skill("Add {{title}}.", Declared));

        Text(await tool.ExecuteAsync("call", new Dictionary<string, object?> { ["action"] = "list" }))
            .ShouldContain("Requires parameters: title");
    }

    [Fact]
    public void Parser_ReadsAParametersBlockOutOfFrontmatter()
    {
        var markdown = """
            ---
            name: add-film
            description: Adds a film.
            parameters:
              title: "The film to add."
              quality: "Quality profile."
            ---
            Add {{title}} at {{quality}}.
            """;

        var parsed = SkillParser.Parse("add-film", markdown, "/skills/add-film", SkillSource.Workspace);

        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters["title"].ShouldBe("The film to add.");
    }

    [Fact]
    public void Parser_LeavesParametersEmptyForASkillThatDeclaresNone()
    {
        var markdown = """
            ---
            name: plain
            description: No parameters here.
            ---
            Body.
            """;

        SkillParser.Parse("plain", markdown, "/skills/plain", SkillSource.Workspace)
            .Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Parser_FoldsSlotNamesThatDifferOnlyInCase()
    {
        // Not a preference — the frontmatter parser's nested-block reader is case-insensitive, so
        // two such declarations cannot survive parsing. Pinned because the loader must agree with
        // it: a case-SENSITIVE loader would refuse {{Title}} against a file that declares Title.
        var markdown = """
            ---
            name: cased
            description: x
            parameters:
              title: lower
              Title: upper
            ---
            {{title}} {{Title}}
            """;

        SkillParser.Parse("cased", markdown, "/skills/cased", SkillSource.Workspace)
            .Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Load_MatchesSlotNamesWithoutRegardToCase()
    {
        // Follows from the parser folding case. Without this, a skill declaring "Title" could never
        // be loaded at all: the declaration is real and no spelling of the argument would match it.
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "The film to add."
        };
        var tool = Tool(Skill("Add {{Title}}.", declared));

        Text(await tool.ExecuteAsync("call", LoadArgs(
                new Dictionary<string, object?> { ["title"] = "Dune" })))
            .ShouldContain("Add Dune.");
    }
}
