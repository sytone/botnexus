using BotNexus.Extensions.Skills;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillPromptBuilderTests
{
    private static SkillDefinition MakeSkill(string name, string content = "Skill content", string? description = null)
        => new()
        {
            Name = name,
            Description = description ?? $"{name} description",
            Content = content,
            Source = SkillSource.Global,
            SourcePath = $"/skills/{name}"
        };

    [Fact]
    public void Build_LoadedSkills_IncludesContent()
    {
        var loaded = new[] { MakeSkill("email-triage", "Classify emails by category.") };

        var prompt = SkillPromptBuilder.Build(loaded, []);

        prompt.ShouldContain("## Skill: email-triage");
        prompt.ShouldContain("Classify emails by category.");
        prompt.ShouldContain("<!-- SKILLS_CONTEXT -->");
        prompt.ShouldContain("<!-- END_SKILLS_CONTEXT -->");
    }

    [Fact]
    public void Build_AvailableSkills_ListsWithDescription()
    {
        var available = new[] { MakeSkill("pptx", description: "Create PowerPoint presentations") };

        var prompt = SkillPromptBuilder.Build([], available);

        prompt.ShouldContain("Skills Available (not loaded)");
        prompt.ShouldContain("pptx");
        prompt.ShouldContain("Create PowerPoint presentations");
    }

    [Fact]
    public void Build_NoSkills_ReturnsEmpty()
    {
        SkillPromptBuilder.Build([], []).ShouldBeEmpty();
    }

    [Fact]
    public void Build_MultipleLoaded_IncludesAll()
    {
        var loaded = new[] { MakeSkill("email-triage", "Email content"), MakeSkill("calendar", "Calendar content") };

        var prompt = SkillPromptBuilder.Build(loaded, []);

        prompt.ShouldContain("## Skill: email-triage");
        prompt.ShouldContain("Email content");
        prompt.ShouldContain("## Skill: calendar");
        prompt.ShouldContain("Calendar content");
    }

    [Fact]
    public void Build_LoadedSkills_ShowsActiveListing()
    {
        var loaded = new[] { MakeSkill("email-triage", description: "Email triage and classification") };

        var prompt = SkillPromptBuilder.Build(loaded, []);

        prompt.ShouldContain("email-triage");
        prompt.ShouldContain("Email triage and classification");
    }

    [Fact]
    public void Build_MixedLoadedAndAvailable_ShowsBoth()
    {
        var loaded = new[] { MakeSkill("email-triage") };
        var available = new[] { MakeSkill("calendar") };

        var prompt = SkillPromptBuilder.Build(loaded, available);

        prompt.ShouldContain("## Skill: email-triage");
        prompt.ShouldContain("Skills Available (not loaded)");
        prompt.ShouldContain("calendar");
    }
}
