using BotNexus.Prompts;
using FluentAssertions;

namespace BotNexus.Prompts.Tests;

public sealed class PromptPrimitivesTests
{
    [Fact]
    public void ContextFileOrdering_SortsKnownFilesBeforeOthers()
    {
        var files = new List<ContextFile>
        {
            new("docs/README.md", "readme"),
            new("SOUL.md", "soul"),
            new("AGENTS.md", "agents"),
            new("identity.md", "identity")
        };

        var ordered = ContextFileOrdering.SortForPrompt(files);

        ordered.Select(f => f.Path).Should().Equal("AGENTS.md", "SOUL.md", "identity.md", "docs/README.md");
    }

    [Fact]
    public void ToolNameRegistry_ResolvesCanonicalToolNames()
    {
        var registry = new ToolNameRegistry(["Read", "exec"]);

        registry.Resolve("read").Should().Be("Read");
        registry.Resolve("process").Should().Be("process");
        registry.Contains("EXEC").Should().BeTrue();
    }

    [Fact]
    public void SkillsParser_ParsesFrontmatterAndBody()
    {
        var parsed = SkillsParser.Parse("""
            ---
            name: skill-a
            description: desc
            ---
            Body line
            """);

        parsed.Name.Should().Be("skill-a");
        parsed.Description.Should().Be("desc");
        parsed.Content.Should().Be("Body line");
    }

    [Fact]
    public void RuntimeLineFormatter_FormatsRuntimeFieldsDeterministically()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "a",
            Host = "h",
            Os = "Windows",
            Arch = "x64",
            Provider = "openai",
            Model = "gpt",
            Channel = "SignalR",
            Capabilities = ["InlineButtons", "Reactions"]
        });

        line.Should().Be("Runtime: agent=a | host=h | os=Windows (x64) | provider=openai | model=gpt | channel=signalr | capabilities=inlinebuttons,reactions");
    }

    [Fact]
    public void PromptPipeline_OrdersSectionsAndStandaloneContributors()
    {
        var pipeline = new PromptPipeline()
            .Add(new TestSection(200, ["second"]))
            .Add(new TestSection(100, ["first"]))
            .AddContributors([new TestContributor(150, "Extra", ["middle"])]);

        var result = pipeline.Build(new PromptContext { WorkspaceDir = "C:/repo" });

        result.Should().Be("first\n## Extra\nmiddle\nsecond");
    }

    private sealed class TestSection(int order, IReadOnlyList<string> lines) : IPromptSection
    {
        public int Order => order;

        public bool ShouldInclude(PromptContext context) => true;

        public IReadOnlyList<string> Build(PromptContext context) => lines;
    }

    private sealed class TestContributor(int priority, string heading, IReadOnlyList<string> lines) : IPromptContributor
    {
        public PromptSection? Target => null;

        public int Priority => priority;

        public bool ShouldInclude(PromptContext context) => true;

        public PromptContribution GetContribution(PromptContext context) => new()
        {
            SectionHeading = heading,
            Lines = lines
        };
    }
}