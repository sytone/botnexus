using BotNexus.Agent;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class SkillsLoaderTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<ILogger<SkillsLoader>> _loggerMock;
    private readonly BotNexusConfig _config;

    public SkillsLoaderTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "skills-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _loggerMock = new Mock<ILogger<SkillsLoader>>();
        _config = new BotNexusConfig();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private SkillsLoader CreateLoader(BotNexusConfig? config = null)
    {
        return new SkillsLoader(
            _testRoot,
            Options.Create(config ?? _config),
            _loggerMock.Object);
    }

    private void CreateSkillFile(string path, string name, string? frontmatter = null, string? content = null)
    {
        var dir = Path.Combine(_testRoot, path, name);
        Directory.CreateDirectory(dir);
        
        var sb = new System.Text.StringBuilder();
        if (frontmatter != null)
        {
            sb.AppendLine("---");
            sb.AppendLine(frontmatter);
            sb.AppendLine("---");
        }
        if (content != null)
        {
            sb.AppendLine(content);
        }
        
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), sb.ToString());
    }

    [Fact]
    public async Task LoadSkillsAsync_LoadsFromGlobalDirectory()
    {
        CreateSkillFile("skills", "data-analysis", 
            "description: Analyze data\nversion: 1.0", 
            "## Data Analysis\nAnalyze datasets carefully.");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("data-analysis");
        skills[0].Description.Should().Be("Analyze data");
        skills[0].Version.Should().Be("1.0");
        skills[0].Content.Should().Contain("Analyze datasets carefully");
        skills[0].Scope.Should().Be(SkillScope.Global);
    }

    [Fact]
    public async Task LoadSkillsAsync_LoadsFromAgentDirectory()
    {
        CreateSkillFile("agents/test-agent/skills", "custom-skill",
            "description: Custom agent skill\nversion: 2.0",
            "Custom instructions for this agent.");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("custom-skill");
        skills[0].Description.Should().Be("Custom agent skill");
        skills[0].Version.Should().Be("2.0");
        skills[0].Content.Should().Contain("Custom instructions");
        skills[0].Scope.Should().Be(SkillScope.Agent);
    }

    [Fact]
    public async Task LoadSkillsAsync_AgentSkillOverridesGlobalSkill()
    {
        CreateSkillFile("skills", "shared-skill",
            "description: Global version",
            "Global content");

        CreateSkillFile("agents/test-agent/skills", "shared-skill",
            "description: Agent version",
            "Agent-specific content");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("shared-skill");
        skills[0].Description.Should().Be("Agent version");
        skills[0].Content.Should().Contain("Agent-specific content");
        skills[0].Scope.Should().Be(SkillScope.Agent);
    }

    [Fact]
    public async Task LoadSkillsAsync_EmptyDirectories_ReturnsNoSkills()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "skills"));
        Directory.CreateDirectory(Path.Combine(_testRoot, "agents", "test-agent", "skills"));

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSkillsAsync_MissingDirectories_HandledGracefully()
    {
        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSkillsAsync_ValidYamlFrontmatter_ParsedCorrectly()
    {
        CreateSkillFile("skills", "yaml-skill",
            "description: YAML test\nversion: 3.1.4\nalways: true",
            "Content with frontmatter");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Description.Should().Be("YAML test");
        skills[0].Version.Should().Be("3.1.4");
        skills[0].AlwaysLoad.Should().BeTrue();
        skills[0].Content.Should().Contain("Content with frontmatter");
    }

    [Fact]
    public async Task LoadSkillsAsync_NoFrontmatter_LoadsContentOnly()
    {
        CreateSkillFile("skills", "plain-skill",
            frontmatter: null,
            content: "Just plain markdown content\nwithout any frontmatter");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("plain-skill");
        skills[0].Description.Should().Be("Skill: plain-skill");
        skills[0].Version.Should().BeNull();
        skills[0].AlwaysLoad.Should().BeFalse();
        skills[0].Content.Should().Contain("Just plain markdown content");
    }

    [Fact]
    public async Task LoadSkillsAsync_MalformedYamlFrontmatter_HandledGracefully()
    {
        var dir = Path.Combine(_testRoot, "skills", "malformed-skill");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), 
            "---\nthis is: [not: valid: yaml\n---\nSome content");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().BeEmpty();
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to load skill")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadSkillsAsync_DisabledSkills_ExactMatch_FiltersSkill()
    {
        CreateSkillFile("skills", "skill-one", "description: First", "Content 1");
        CreateSkillFile("skills", "skill-two", "description: Second", "Content 2");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["skill-one"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("skill-two");
    }

    [Fact]
    public async Task LoadSkillsAsync_DisabledSkills_WildcardAll_DisablesAllSkills()
    {
        CreateSkillFile("skills", "skill-one", "description: First", "Content 1");
        CreateSkillFile("skills", "skill-two", "description: Second", "Content 2");
        CreateSkillFile("skills", "skill-three", "description: Third", "Content 3");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["*"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSkillsAsync_DisabledSkills_WildcardPrefix_DisablesMatchingSkills()
    {
        CreateSkillFile("skills", "web-scraper", "description: Web scraper", "Content");
        CreateSkillFile("skills", "web-search", "description: Web search", "Content");
        CreateSkillFile("skills", "data-analysis", "description: Data analysis", "Content");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["web-*"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("data-analysis");
    }

    [Fact]
    public async Task LoadSkillsAsync_DisabledSkills_WildcardSuffix_DisablesMatchingSkills()
    {
        CreateSkillFile("skills", "coding-tool", "description: Coding", "Content");
        CreateSkillFile("skills", "testing-tool", "description: Testing", "Content");
        CreateSkillFile("skills", "data-analyzer", "description: Analyzer", "Content");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["*-tool"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("data-analyzer");
    }

    [Fact]
    public async Task LoadSkillsAsync_DisabledSkills_CaseInsensitive()
    {
        CreateSkillFile("skills", "DataAnalysis", "description: Analysis", "Content");
        CreateSkillFile("skills", "WebSearch", "description: Search", "Content");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["dataanalysis", "WEBSEARCH"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSkillsAsync_EmptyDisabledSkills_DisablesNothing()
    {
        CreateSkillFile("skills", "skill-one", "description: First", "Content 1");
        CreateSkillFile("skills", "skill-two", "description: Second", "Content 2");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = []
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(2);
    }

    [Fact]
    public async Task LoadSkillsAsync_NoAgentConfig_LoadsAllSkills()
    {
        CreateSkillFile("skills", "skill-one", "description: First", "Content 1");
        CreateSkillFile("skills", "skill-two", "description: Second", "Content 2");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(2);
    }

    [Fact]
    public async Task LoadSkillsAsync_MultipleGlobalAndAgentSkills_MergesCorrectly()
    {
        CreateSkillFile("skills", "global-one", "description: G1", "Global 1");
        CreateSkillFile("skills", "global-two", "description: G2", "Global 2");
        CreateSkillFile("skills", "shared", "description: Global shared", "Global");
        CreateSkillFile("agents/test-agent/skills", "agent-one", "description: A1", "Agent 1");
        CreateSkillFile("agents/test-agent/skills", "shared", "description: Agent shared", "Agent");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(4);
        skills.Should().Contain(s => s.Name == "global-one" && s.Scope == SkillScope.Global);
        skills.Should().Contain(s => s.Name == "global-two" && s.Scope == SkillScope.Global);
        skills.Should().Contain(s => s.Name == "agent-one" && s.Scope == SkillScope.Agent);
        skills.Should().Contain(s => s.Name == "shared" && s.Scope == SkillScope.Agent);
    }

    [Fact]
    public async Task LoadSkillsAsync_SkillsSortedAlphabetically()
    {
        CreateSkillFile("skills", "zebra", "description: Z", "Content");
        CreateSkillFile("skills", "apple", "description: A", "Content");
        CreateSkillFile("skills", "mango", "description: M", "Content");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(3);
        skills[0].Name.Should().Be("apple");
        skills[1].Name.Should().Be("mango");
        skills[2].Name.Should().Be("zebra");
    }

    [Fact]
    public async Task LoadSkillsAsync_SkillDirectoryWithoutSKILLmd_Skipped()
    {
        var dir = Path.Combine(_testRoot, "skills", "incomplete-skill");
        Directory.CreateDirectory(dir);

        CreateSkillFile("skills", "valid-skill", "description: Valid", "Content");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("valid-skill");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("missing SKILL.md")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadSkillsAsync_WildcardInMiddle_DisablesMatchingSkills()
    {
        CreateSkillFile("skills", "api-client-v1", "description: V1", "Content");
        CreateSkillFile("skills", "api-server-v2", "description: V2", "Content");
        CreateSkillFile("skills", "data-processor", "description: Data", "Content");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["api-*-v1"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(2);
        skills.Should().NotContain(s => s.Name == "api-client-v1");
    }

    [Fact]
    public async Task LoadSkillsAsync_QuestionMarkWildcard_MatchesSingleChar()
    {
        CreateSkillFile("skills", "tool1", "description: T1", "Content");
        CreateSkillFile("skills", "tool2", "description: T2", "Content");
        CreateSkillFile("skills", "tool10", "description: T10", "Content");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["tool?"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("tool10");
    }

    [Fact]
    public async Task LoadSkillsAsync_PartialFrontmatter_UsesDefaults()
    {
        CreateSkillFile("skills", "partial-skill",
            "description: Has description only",
            "Some content");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Description.Should().Be("Has description only");
        skills[0].Version.Should().BeNull();
        skills[0].AlwaysLoad.Should().BeFalse();
    }

    [Fact]
    public async Task LoadSkillsAsync_EmptyFrontmatter_UsesDefaults()
    {
        var dir = Path.Combine(_testRoot, "skills", "empty-frontmatter");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\n\n---\nSome content");

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().HaveCount(1);
        skills[0].Name.Should().Be("empty-frontmatter");
        skills[0].Description.Should().Be("Skill: empty-frontmatter");
        skills[0].Content.Should().Be("Some content");
    }

    [Fact]
    public async Task LoadSkillsAsync_CancellationToken_ThrowsWhenCancelled()
    {
        CreateSkillFile("skills", "test-skill", "description: Test", "Content");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var loader = CreateLoader();
        
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await loader.LoadSkillsAsync("test-agent", cts.Token));
    }

    [Fact]
    public async Task LoadSkillsAsync_ComplexDisabledSkillsPattern_FiltersCorrectly()
    {
        CreateSkillFile("skills", "web-scraper", "description: WS", "Content");
        CreateSkillFile("skills", "web-search", "description: WS", "Content");
        CreateSkillFile("skills", "api-client", "description: AC", "Content");
        CreateSkillFile("skills", "data-tool", "description: DT", "Content");
        CreateSkillFile("skills", "debug-tool", "description: DT", "Content");

        _config.Agents.Named["test-agent"] = new AgentConfig
        {
            DisabledSkills = ["web-*", "*-tool", "api-client"]
        };

        var loader = CreateLoader();
        var skills = await loader.LoadSkillsAsync("test-agent");

        skills.Should().BeEmpty();
    }
}
