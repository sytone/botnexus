using BotNexus.CodingAgent;

namespace BotNexus.CodingAgent.Tests;

public sealed class SystemPromptBuilderTests
{
    private readonly SystemPromptBuilder _builder = new();

    [Fact]
    public void Build_IncludesRoleToolsAndEnvironmentSections()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "npm",
            ToolNames: ["read", "write", "bash"],
            Skills: [],
            CustomInstructions: null,
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero),
            ToolContributions:
            [
                new ToolPromptContribution("read", "Read files with line numbers.", ["Prefer read before edit."]),
                new ToolPromptContribution("write", "Write file content."),
                new ToolPromptContribution("bash", "Execute shell commands.")
            ]);

        var prompt = _builder.Build(context);

        prompt.ShouldContain("You are a coding assistant");
        prompt.ShouldContain("## Environment");
        prompt.ShouldContain("- Working directory: C:/repo");
        prompt.ShouldContain("## Available Tools");
        prompt.ShouldContain("- read: Read files with line numbers.");
        prompt.ShouldContain("## Tool Guidelines");
        prompt.ShouldContain("Prefer read before edit.");
        prompt.ShouldContain("Current date/time: 2026-04-06T10:30:00.0000000+00:00");
        prompt.ShouldContain("Current working directory: C:/repo");
    }

    [Fact]
    public void Build_WithSkillsAndCustomInstructions_IncludesBothSections()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: null,
            GitStatus: null,
            PackageManager: "dotnet",
            ToolNames: ["read"],
            Skills:
            [
                """
                ---
                name: read
                description: skill a description
                ---
                Skill A
                """,
                "Skill B"
            ],
            CustomInstructions: "Use concise responses.",
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero));

        var prompt = _builder.Build(context);

        prompt.ShouldContain("## Skills");
        prompt.ShouldContain("name: read");
        prompt.ShouldContain("description: skill a description");
        prompt.ShouldContain("Skill A");
        prompt.ShouldContain("Skill B");
        prompt.ShouldContain("## Custom Instructions");
        prompt.ShouldContain("Use concise responses.");
    }

    [Fact]
    public void Build_WithContextFiles_IncludesProjectContextSection()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "dotnet",
            ToolNames: ["read"],
            Skills: [],
            CustomInstructions: null,
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero),
            ContextFiles:
            [
                new PromptContextFile(".botnexus-agent/context/runtime.md", "Runtime details")
            ]);

        var prompt = _builder.Build(context);

        prompt.ShouldContain("## Project Context");
        prompt.ShouldContain("### .botnexus-agent/context/runtime.md");
        prompt.ShouldContain("Runtime details");
    }

    [Fact]
    public void Build_WithEmptyOptionalSections_OmitsSectionHeadings()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "dotnet",
            ToolNames: ["read"],
            Skills: [],
            CustomInstructions: "   ",
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero),
            ToolContributions: [new ToolPromptContribution("read", null, ["  "])],
            ContextFiles: [new PromptContextFile("context.md", "   ")]);

        var prompt = _builder.Build(context);

        prompt.ShouldNotContain("## Skills");
    }

    [Fact]
    public void Build_WithCustomPrompt_ReplacesBaseAndAppendsConfiguredText()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: null,
            GitStatus: null,
            PackageManager: "dotnet",
            ToolNames: ["read"],
            Skills: [],
            CustomInstructions: null,
            CustomPrompt: "Custom base prompt",
            AppendSystemPrompt: "Appended prompt",
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero));

        var prompt = _builder.Build(context);

        prompt.ShouldStartWith("Custom base prompt");
        prompt.ShouldContain("Appended prompt");
        prompt.ShouldNotContain("## Environment");
    }

    [Fact]
    public void Build_WithOnlyBashTool_AddsBashFileOperationsGuideline()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "dotnet",
            ToolNames: ["bash"],
            Skills: [],
            CustomInstructions: null,
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero));

        var prompt = _builder.Build(context);

        prompt.ShouldContain("Use bash for file operations like ls, rg, find.");
    }

    [Fact]
    public void Build_WithBashAndDiscoveryTools_PrefersDedicatedToolsGuideline()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "dotnet",
            ToolNames: ["bash", "grep", "glob"],
            Skills: [],
            CustomInstructions: null,
            CurrentDateTime: new DateTimeOffset(2026, 4, 6, 10, 30, 0, TimeSpan.Zero));

        var prompt = _builder.Build(context);

        prompt.ShouldContain("Prefer grep/find/ls tools over bash for file exploration (faster, respects .gitignore).");
    }
}
