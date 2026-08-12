using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

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

        ordered.Select(f => f.Path).ShouldBe(new[] { "AGENTS.md", "SOUL.md", "identity.md", "docs/README.md" });
    }

    [Fact]
    public void ContextFileOrdering_PrioritizesMemorySummaryThenDailyMemoryNotes()
    {
        var files = new List<ContextFile>
        {
            new("memory/2024-05-07.md", "older"),
            new("docs/README.md", "readme"),
            new("MEMORY.md", "long-term"),
            new("memory/2024-05-08.md", "today")
        };

        var ordered = ContextFileOrdering.SortForPrompt(files);

        ordered.Select(f => f.Path).ShouldBe(new[]
        {
            "MEMORY.md",
            "memory/2024-05-07.md",
            "memory/2024-05-08.md",
            "docs/README.md"
        });
    }

    [Theory]
    [InlineData("./memory/2026-08-11.md")]
    [InlineData(".\\memory\\2026-08-11.md")]
    [InlineData("  ./memory/2026-08-11.md  ")]
    [InlineData(".//memory//2026-08-11.md")]
    public void NormalizePath_CollapsesLeadingDotSlashAndRepeatedSeparators(string input)
    {
        // #2940: the identity consumer (AddContextFilesWithoutDuplicates) compares these under
        // OrdinalIgnoreCase, so a leading "./" must not make an equivalent path look distinct.
        ContextFileOrdering.NormalizePath(input)
            .ShouldBe(ContextFileOrdering.NormalizePath("memory/2026-08-11.md"), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void NormalizePath_DoesNotResolveParentSegments()
    {
        // #2940 AC6: workspace containment stays the sole responsibility of IsPathUnderWorkspace.
        // Collapsing ".." here would split a security check across two files.
        ContextFileOrdering.NormalizePath("memory/../secrets.md").ShouldBe("memory/../secrets.md");
        ContextFileOrdering.NormalizePath("../outside.md").ShouldBe("../outside.md");
    }

    [Theory]
    [InlineData("docs/./README.md", "docs/./README.md")]
    [InlineData(".hidden/notes.md", ".hidden/notes.md")]
    [InlineData("./", "")]
    [InlineData("", "")]
    public void NormalizePath_OnlyCollapsesLeadingDotSegments(string input, string expected)
    {
        // Sad paths: a mid-path "./" and a dot-prefixed directory name must survive untouched.
        ContextFileOrdering.NormalizePath(input).ShouldBe(expected);
    }

    [Fact]
    public void ContextFileOrdering_TreatsDotSlashDailyNoteAsDailyNoteForOrdering()
    {
        // Collapsing also fixes IsDailyMemoryNote, which tests StartsWith("memory/"): before the
        // fix "./memory/..." fell through to int.MaxValue. The companion file's basename is chosen
        // to sort ORDINALLY BEFORE the note's, so the un-fixed int.MaxValue tie-break puts it first
        // and this assertion genuinely discriminates (a "readme.md" companion would not).
        var files = new List<ContextFile>
        {
            new("docs/0-intro.md", "intro"),
            new("./memory/2024-05-08.md", "today"),
            new("MEMORY.md", "long-term")
        };

        ContextFileOrdering.SortForPrompt(files).Select(f => f.Path)
            .ShouldBe(new[] { "MEMORY.md", "./memory/2024-05-08.md", "docs/0-intro.md" });
    }

    [Fact]
    public void ContextFileOrdering_SortForPromptPreservesOriginalPathStrings()
    {
        // Normalization is an ordering/identity key only; SortForPrompt must never rewrite Path.
        var files = new List<ContextFile> { new(".\\docs\\README.md", "readme"), new("SOUL.md", "soul") };

        ContextFileOrdering.SortForPrompt(files).Select(f => f.Path)
            .ShouldBe(new[] { "SOUL.md", ".\\docs\\README.md" });
    }

    [Fact]
    public void ToolNameRegistry_ResolvesCanonicalToolNames()
    {
        var registry = new ToolNameRegistry(["Read", "exec"]);

        registry.Resolve("read").ShouldBe("Read");
        registry.Resolve("process").ShouldBe("process");
        registry.Contains("EXEC").ShouldBeTrue();
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

        line.ShouldBe("Runtime: agent=a | host=h | os=Windows (x64) | provider=openai | model=gpt | channel=signalr | capabilities=inlinebuttons,reactions");
    }

    [Fact]
    public void PromptPipeline_OrdersSectionsAndStandaloneContributors()
    {
        var pipeline = new PromptPipeline()
            .Add(new TestSection(200, ["second"]))
            .Add(new TestSection(100, ["first"]))
            .AddContributors([new TestContributor(150, "Extra", ["middle"])]);

        var result = pipeline.Build(new PromptContext { WorkspaceDir = "C:/repo" });

        result.ShouldBe("first\n## Extra\nmiddle\nsecond");
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
