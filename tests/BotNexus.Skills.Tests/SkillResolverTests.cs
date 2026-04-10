using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillResolverTests
{
    private static SkillDefinition MakeSkill(string name, string? description = null)
        => new()
        {
            Name = name,
            Description = description ?? $"{name} skill description",
            Content = $"Content for {name}",
            Source = SkillSource.Global,
            SourcePath = $"/skills/{name}"
        };

    [Fact]
    public void Resolve_AutoLoadSkills_Included()
    {
        var skills = new[] { MakeSkill("email-triage"), MakeSkill("calendar") };
        var config = new SkillsConfig { AutoLoad = ["email-triage"] };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Should().ContainSingle(s => s.Name == "email-triage");
        result.Available.Should().ContainSingle(s => s.Name == "calendar");
    }

    [Fact]
    public void Resolve_DenyList_ExcludesSkills()
    {
        var skills = new[] { MakeSkill("email-triage"), MakeSkill("calendar") };
        var config = new SkillsConfig { AutoLoad = ["email-triage", "calendar"], Disabled = ["email-triage"] };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Should().ContainSingle(s => s.Name == "calendar");
        result.Denied.Should().ContainSingle(s => s.Name == "email-triage");
    }

    [Fact]
    public void Resolve_AllowList_OnlyAllowsListed()
    {
        var skills = new[] { MakeSkill("email-triage"), MakeSkill("calendar"), MakeSkill("ado") };
        var config = new SkillsConfig { Allowed = ["email-triage", "ado"] };

        var result = SkillResolver.Resolve(skills, config);

        result.Available.Select(s => s.Name).Should().BeEquivalentTo(["email-triage", "ado"]);
        result.Denied.Should().ContainSingle(s => s.Name == "calendar");
    }

    [Fact]
    public void Resolve_DenyTakesPrecedenceOverAllow()
    {
        var skills = new[] { MakeSkill("email-triage") };
        var config = new SkillsConfig { Allowed = ["email-triage"], Disabled = ["email-triage"] };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Should().BeEmpty();
        result.Available.Should().BeEmpty();
        result.Denied.Should().ContainSingle(s => s.Name == "email-triage");
    }

    [Fact]
    public void Resolve_ExplicitlyLoaded_AlwaysIncluded()
    {
        var skills = new[] { MakeSkill("email-triage"), MakeSkill("calendar") };
        var config = new SkillsConfig();

        var result = SkillResolver.Resolve(skills, config, explicitlyLoaded: ["calendar"]);

        result.Loaded.Should().ContainSingle(s => s.Name == "calendar");
        result.Available.Should().ContainSingle(s => s.Name == "email-triage");
    }

    [Fact]
    public void Resolve_MaxLoadedSkills_RespectsLimit()
    {
        var skills = Enumerable.Range(1, 5).Select(i => MakeSkill($"skill-{i}")).ToArray();
        var config = new SkillsConfig { AutoLoad = skills.Select(s => s.Name).ToList(), MaxLoadedSkills = 3 };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Should().HaveCount(3);
        result.Available.Should().HaveCount(2);
    }

    [Fact]
    public void Resolve_MaxContentChars_RespectsLimit()
    {
        var skills = new[]
        {
            new SkillDefinition { Name = "big", Description = "Big", Content = new string('x', 1000), Source = SkillSource.Global, SourcePath = "/s/big" },
            new SkillDefinition { Name = "small", Description = "Small", Content = "tiny", Source = SkillSource.Global, SourcePath = "/s/small" }
        };
        var config = new SkillsConfig { AutoLoad = ["big", "small"], MaxSkillContentChars = 500 };

        var result = SkillResolver.Resolve(skills, config);

        // big exceeds budget, small fits
        result.Loaded.Select(s => s.Name).Should().Contain("small");
        result.Loaded.Select(s => s.Name).Should().NotContain("big");
    }

    [Fact]
    public void Resolve_DisabledConfig_ReturnsNoSkills()
    {
        var skills = new[] { MakeSkill("email-triage") };
        var config = new SkillsConfig { Enabled = false, AutoLoad = ["email-triage"] };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Should().BeEmpty();
        result.Available.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NullConfig_UsesDefaults_AllAvailable()
    {
        var skills = new[] { MakeSkill("email-triage") };

        var result = SkillResolver.Resolve(skills, config: null);

        result.Available.Should().ContainSingle(s => s.Name == "email-triage");
        result.Loaded.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ExplicitLoadDenied_NotLoaded()
    {
        var skills = new[] { MakeSkill("email-triage") };
        var config = new SkillsConfig { Disabled = ["email-triage"] };

        var result = SkillResolver.Resolve(skills, config, explicitlyLoaded: ["email-triage"]);

        result.Loaded.Should().BeEmpty();
        result.Denied.Should().ContainSingle(s => s.Name == "email-triage");
    }

    [Fact]
    public void Resolve_NegativeMaxLoadedSkills_TreatedAsUnlimited()
    {
        var skills = Enumerable.Range(1, 30).Select(i => MakeSkill($"skill-{i}")).ToArray();
        var config = new SkillsConfig
        {
            AutoLoad = skills.Select(s => s.Name).ToList(),
            MaxLoadedSkills = -1
        };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Should().HaveCount(30);
        result.Available.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NegativeMaxContentChars_TreatedAsUnlimited()
    {
        var skills = new[]
        {
            new SkillDefinition { Name = "huge", Description = "Huge", Content = new string('x', 300_000), Source = SkillSource.Global, SourcePath = "/s/huge" },
            new SkillDefinition { Name = "small", Description = "Small", Content = "tiny", Source = SkillSource.Global, SourcePath = "/s/small" }
        };
        var config = new SkillsConfig
        {
            AutoLoad = ["huge", "small"],
            MaxSkillContentChars = -1
        };

        var result = SkillResolver.Resolve(skills, config);

        result.Loaded.Select(s => s.Name).Should().BeEquivalentTo(["huge", "small"]);
        result.Available.Should().BeEmpty();
    }
}
