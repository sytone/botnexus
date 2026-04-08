using BotNexus.Skills;
using FluentAssertions;

namespace BotNexus.Skills.Tests;

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

        prompt.Should().Contain("## Skill: email-triage");
        prompt.Should().Contain("Classify emails by category.");
        prompt.Should().Contain("<!-- SKILLS_CONTEXT -->");
        prompt.Should().Contain("<!-- END_SKILLS_CONTEXT -->");
    }

    [Fact]
    public void Build_AvailableSkills_ListsWithDescription()
    {
        var available = new[] { MakeSkill("pptx", description: "Create PowerPoint presentations") };

        var prompt = SkillPromptBuilder.Build([], available);

        prompt.Should().Contain("Skills Available (not loaded)");
        prompt.Should().Contain("pptx");
        prompt.Should().Contain("Create PowerPoint presentations");
    }

    [Fact]
    public void Build_NoSkills_ReturnsEmpty()
    {
        SkillPromptBuilder.Build([], []).Should().BeEmpty();
    }

    [Fact]
    public void Build_MultipleLoaded_IncludesAll()
    {
        var loaded = new[] { MakeSkill("email-triage", "Email content"), MakeSkill("calendar", "Calendar content") };

        var prompt = SkillPromptBuilder.Build(loaded, []);

        prompt.Should().Contain("## Skill: email-triage");
        prompt.Should().Contain("Email content");
        prompt.Should().Contain("## Skill: calendar");
        prompt.Should().Contain("Calendar content");
    }

    [Fact]
    public void Build_LoadedSkills_ShowsActiveListing()
    {
        var loaded = new[] { MakeSkill("email-triage", description: "Email triage and classification") };

        var prompt = SkillPromptBuilder.Build(loaded, []);

        prompt.Should().Contain("email-triage");
        prompt.Should().Contain("Email triage and classification");
    }

    [Fact]
    public void Build_MixedLoadedAndAvailable_ShowsBoth()
    {
        var loaded = new[] { MakeSkill("email-triage") };
        var available = new[] { MakeSkill("calendar") };

        var prompt = SkillPromptBuilder.Build(loaded, available);

        prompt.Should().Contain("## Skill: email-triage");
        prompt.Should().Contain("Skills Available (not loaded)");
        prompt.Should().Contain("calendar");
    }
}
