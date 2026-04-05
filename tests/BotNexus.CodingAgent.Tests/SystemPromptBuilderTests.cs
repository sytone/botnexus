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
            CustomInstructions: null);

        var prompt = _builder.Build(context);

        prompt.Should().Contain("You are a coding assistant");
        prompt.Should().Contain("## Environment");
        prompt.Should().Contain("- Working directory: C:\\repo");
        prompt.Should().Contain("- Tools: read, write, bash");
        prompt.Should().Contain("## Tool Guidelines");
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
            Skills: ["Skill A", "Skill B"],
            CustomInstructions: "Use concise responses.");

        var prompt = _builder.Build(context);

        prompt.Should().Contain("## Skills");
        prompt.Should().Contain("Skill A");
        prompt.Should().Contain("Skill B");
        prompt.Should().Contain("## Custom Instructions");
        prompt.Should().Contain("Use concise responses.");
    }
}
