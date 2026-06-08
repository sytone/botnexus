using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class PromptSectionWiringTests
{
    private static string BuildFullPrompt(string? model = null, IReadOnlyList<string>? tools = null)
    {
        return SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "test-workspace"),
            ToolNames = tools ?? ["read", "write", "shell", "skills", "skill_manage"],
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "test-agent",
                Channel = "signalr",
                Model = model
            }
        });
    }

    [Fact]
    public void ToolEnforcementSection_AppearsInFullPrompt()
    {
        var prompt = BuildFullPrompt();

        prompt.ShouldContain("## Tool Enforcement");
        prompt.ShouldContain("execute the tool immediately");
    }

    [Fact]
    public void ShellEfficiencySection_AppearsInFullPrompt()
    {
        var prompt = BuildFullPrompt();

        prompt.ShouldContain("## Shell Efficiency");
        prompt.ShouldContain("temporary script file");
    }

    [Fact]
    public void SkillsGuidanceSection_AppearsWhenSkillToolsAvailable()
    {
        var prompt = BuildFullPrompt(tools: ["read", "skills", "skill_manage"]);

        prompt.ShouldContain("## Skills");
        prompt.ShouldContain("check available skills");
    }

    [Fact]
    public void SkillsGuidanceSection_OmittedWhenNoSkillTools()
    {
        var prompt = BuildFullPrompt(tools: ["read", "write", "shell"]);

        prompt.ShouldNotContain("check available skills");
    }

    [Fact]
    public void ModelGuidanceSection_AppearsForClaudeModel()
    {
        var prompt = BuildFullPrompt(model: "claude-sonnet-4-20250514");

        prompt.ShouldContain("## Model Guidance (Claude)");
        prompt.ShouldContain("edit tool over write");
    }

    [Fact]
    public void ModelGuidanceSection_AppearsForGptModel()
    {
        var prompt = BuildFullPrompt(model: "gpt-4.1");

        prompt.ShouldContain("## Model Guidance (GPT)");
        prompt.ShouldContain("Never answer from memory");
    }

    [Fact]
    public void ModelGuidanceSection_AppearsForGeminiModel()
    {
        var prompt = BuildFullPrompt(model: "gemini-2.5-pro");

        prompt.ShouldContain("## Model Guidance (Gemini)");
        prompt.ShouldContain("absolute paths");
    }

    [Fact]
    public void ModelGuidanceSection_OmittedForUnknownModel()
    {
        var prompt = BuildFullPrompt(model: "some-unknown-model-v1");

        prompt.ShouldNotContain("## Model Guidance");
    }

    [Fact]
    public void ModelGuidanceSection_OmittedWhenModelIsNull()
    {
        var prompt = BuildFullPrompt(model: null);

        prompt.ShouldNotContain("## Model Guidance");
    }

    [Fact]
    public void SectionOrder_ToolEnforcement_BeforeSafety()
    {
        var prompt = BuildFullPrompt();

        var toolEnforcementIdx = prompt.IndexOf("## Tool Enforcement", StringComparison.Ordinal);
        var safetyIdx = prompt.IndexOf("## Safety", StringComparison.Ordinal);

        toolEnforcementIdx.ShouldBeGreaterThan(-1);
        safetyIdx.ShouldBeGreaterThan(-1);
        toolEnforcementIdx.ShouldBeLessThan(safetyIdx);
    }

    [Fact]
    public void SectionOrder_ShellEfficiency_BeforeSafety()
    {
        var prompt = BuildFullPrompt();

        var shellIdx = prompt.IndexOf("## Shell Efficiency", StringComparison.Ordinal);
        var safetyIdx = prompt.IndexOf("## Safety", StringComparison.Ordinal);

        shellIdx.ShouldBeGreaterThan(-1);
        safetyIdx.ShouldBeGreaterThan(-1);
        shellIdx.ShouldBeLessThan(safetyIdx);
    }

    [Fact]
    public void SectionOrder_ModelGuidance_AfterWorkspaceFiles()
    {
        var prompt = BuildFullPrompt(model: "claude-sonnet-4-20250514");

        var workspaceIdx = prompt.IndexOf("## Workspace Files (injected)", StringComparison.Ordinal);
        var modelIdx = prompt.IndexOf("## Model Guidance (Claude)", StringComparison.Ordinal);

        workspaceIdx.ShouldBeGreaterThan(-1);
        modelIdx.ShouldBeGreaterThan(-1);
        workspaceIdx.ShouldBeLessThan(modelIdx);
    }
}
