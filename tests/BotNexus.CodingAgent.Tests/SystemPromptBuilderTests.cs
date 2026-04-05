using BotNexus.CodingAgent;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests;

public sealed class SystemPromptBuilderTests
{
    private readonly SystemPromptBuilder _builder = new();

    [Fact]
    public void Build_IncludesRoleToolsAndEnvironmentSections()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: @"C:\repo",
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "npm",
            ToolNames: ["read", "write", "bash"],
            Skills: [],
            CustomInstructions: null,
            ToolContributions:
            [
                new ToolPromptContribution("read", "Read files with line numbers.", ["Prefer read before edit."]),
                new ToolPromptContribution("write", "Write file content."),
                new ToolPromptContribution("bash", "Execute shell commands.")
            ]);

        var prompt = _builder.Build(context);

        prompt.Should().Contain("You are a coding assistant");
        prompt.Should().Contain("## Environment");
        prompt.Should().Contain("- Working directory: C:\\repo");
        prompt.Should().Contain("## Available Tools");
        prompt.Should().Contain("- read: Read files with line numbers.");
        prompt.Should().Contain("## Tool Guidelines");
        prompt.Should().Contain("Prefer read before edit.");
    }

    [Fact]
    public void Build_WithSkillsAndCustomInstructions_IncludesBothSections()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: @"C:\repo",
            GitBranch: null,
            GitStatus: null,
            PackageManager: "dotnet",
            ToolNames: ["read"],
            Skills:
            [
                """
                ---
                name: skill-a
                description: skill a description
                ---
                Skill A
                """,
                "Skill B"
            ],
            CustomInstructions: "Use concise responses.");

        var prompt = _builder.Build(context);

        prompt.Should().Contain("## Skills");
        prompt.Should().Contain("name: skill-a");
        prompt.Should().Contain("description: skill a description");
        prompt.Should().Contain("Skill A");
        prompt.Should().Contain("Skill B");
        prompt.Should().Contain("## Custom Instructions");
        prompt.Should().Contain("Use concise responses.");
    }

    [Fact]
    public void Build_WithContextFiles_IncludesProjectContextSection()
    {
        var context = new SystemPromptContext(
            WorkingDirectory: @"C:\repo",
            GitBranch: "main",
            GitStatus: "clean",
            PackageManager: "dotnet",
            ToolNames: ["read"],
            Skills: [],
            CustomInstructions: null,
            ContextFiles:
            [
                new PromptContextFile(".botnexus-agent/context/runtime.md", "Runtime details")
            ]);

        var prompt = _builder.Build(context);

        prompt.Should().Contain("## Project Context");
        prompt.Should().Contain("### .botnexus-agent/context/runtime.md");
        prompt.Should().Contain("Runtime details");
    }
}
