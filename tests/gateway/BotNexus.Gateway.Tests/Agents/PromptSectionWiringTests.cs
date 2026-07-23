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

        prompt.ShouldContain("<tool_use>");
        prompt.ShouldContain("</tool_use>");
        prompt.ShouldContain("MUST use your tools");
    }

    [Fact]
    public void ToolEnforcementSection_IncludesCrossAgentFabricationTripwire()
    {
        var prompt = BuildFullPrompt();

        // Guards the cross-agent relay trip-wire: relaying what another agent "said"/"confirmed"
        // with no matching tool result in-turn is the fabrication failure mode this line prevents.
        prompt.ShouldContain("Never report what another agent, service, or person");
        prompt.ShouldContain("agent_converse");
    }

    [Fact]
    public void ToolEnforcementSection_IncludesTodoBoundaryGuidance()
    {
        var prompt = BuildFullPrompt();

        // Guards the todo boundary guidance (#2071): the todo tool is the agent's own
        // per-conversation execution checklist for its work loop, and the guidance stays generic
        // -- it must NOT name any particular external task/work-tracking system.
        prompt.ShouldContain("per-conversation execution checklist");
        prompt.ShouldContain("loop or set of loops");
        prompt.ShouldContain("not a durable or user-facing system of record");
        prompt.ShouldNotContain("TaskNexus");
    }

    [Fact]
    public void ShellEfficiencySection_AppearsInFullPrompt()
    {
        var prompt = BuildFullPrompt();

        prompt.ShouldContain("<shell>");
        prompt.ShouldContain("</shell>");
        prompt.ShouldContain("temporary script file");
    }

    [Fact]
    public void SkillsGuidanceSection_AppearsWhenSkillToolsAvailable()
    {
        var prompt = BuildFullPrompt(tools: ["read", "skills", "skill_manage"]);

        prompt.ShouldContain("<skills>");
        prompt.ShouldContain("</skills>");
        prompt.ShouldContain("partially relevant");
    }

    [Fact]
    public void SkillsGuidanceSection_OmittedWhenNoSkillTools()
    {
        var prompt = BuildFullPrompt(tools: ["read", "write", "shell"]);

        prompt.ShouldNotContain("partially relevant");
    }

    [Fact]
    public void ModelGuidanceSection_AppearsForClaudeModel()
    {
        var prompt = BuildFullPrompt(model: "claude-sonnet-4-20250514");

        prompt.ShouldContain("<model_guidance>");
        prompt.ShouldContain("</model_guidance>");
        prompt.ShouldContain("edit tool over write");
    }

    [Fact]
    public void ModelGuidanceSection_AppearsForGptModel()
    {
        var prompt = BuildFullPrompt(model: "gpt-4.1");

        prompt.ShouldContain("<model_guidance>");
        prompt.ShouldContain("Never answer from memory");
    }

    [Fact]
    public void ModelGuidanceSection_AppearsForGeminiModel()
    {
        var prompt = BuildFullPrompt(model: "gemini-2.5-pro");

        prompt.ShouldContain("<model_guidance>");
        prompt.ShouldContain("absolute paths");
    }

    [Fact]
    public void ModelGuidanceSection_OmittedForUnknownModel()
    {
        var prompt = BuildFullPrompt(model: "some-unknown-model-v1");

        prompt.ShouldNotContain("<model_guidance>");
    }

    [Fact]
    public void ModelGuidanceSection_OmittedWhenModelIsNull()
    {
        var prompt = BuildFullPrompt(model: null);

        prompt.ShouldNotContain("<model_guidance>");
    }

    [Fact]
    public void SectionOrder_ToolEnforcement_BeforeSafety()
    {
        var prompt = BuildFullPrompt();

        var toolEnforcementIdx = prompt.IndexOf("<tool_use>", StringComparison.Ordinal);
        var safetyIdx = prompt.IndexOf("<safety>", StringComparison.Ordinal);

        toolEnforcementIdx.ShouldBeGreaterThan(-1);
        safetyIdx.ShouldBeGreaterThan(-1);
        toolEnforcementIdx.ShouldBeLessThan(safetyIdx);
    }

    [Fact]
    public void SectionOrder_ShellEfficiency_BeforeSafety()
    {
        var prompt = BuildFullPrompt();

        var shellIdx = prompt.IndexOf("<shell>", StringComparison.Ordinal);
        var safetyIdx = prompt.IndexOf("<safety>", StringComparison.Ordinal);

        shellIdx.ShouldBeGreaterThan(-1);
        safetyIdx.ShouldBeGreaterThan(-1);
        shellIdx.ShouldBeLessThan(safetyIdx);
    }

    [Fact]
    public void SectionOrder_ModelGuidance_AfterWorkspaceFiles()
    {
        var prompt = BuildFullPrompt(model: "claude-sonnet-4-20250514");

        var workspaceIdx = prompt.IndexOf("## Workspace Files (injected)", StringComparison.Ordinal);
        var modelIdx = prompt.IndexOf("<model_guidance>", StringComparison.Ordinal);

        workspaceIdx.ShouldBeGreaterThan(-1);
        modelIdx.ShouldBeGreaterThan(-1);
        workspaceIdx.ShouldBeLessThan(modelIdx);
    }

    [Fact]
    public void RuntimeLine_WithMobileClientKind_EmitsClientField()
    {
        // Proves the gateway-side RuntimeInfo.ClientKind threads through SystemPromptBuilder ->
        // PromptRuntimeInfo -> the runtime line in a full prompt (#1209 AC#4).
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "test-workspace"),
            ToolNames = ["read", "write"],
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "test-agent",
                Channel = "signalr",
                ClientKind = "mobile"
            }
        });

        prompt.ShouldContain("client=mobile");
    }

    [Fact]
    public void RuntimeLine_WithoutClientKind_OmitsClientField()
    {
        // Back-compat (AC#5): a session with no client kind renders the existing runtime line.
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "test-workspace"),
            ToolNames = ["read", "write"],
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "test-agent",
                Channel = "signalr"
            }
        });

        prompt.ShouldNotContain("client=");
    }

    [Fact]
    public void RuntimeLine_WithDesktopClientKind_OmitsClientField()
    {
        // Back-compat (AC#5): the default desktop kind is suppressed so desktop prompts are
        // unchanged from before this feature.
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "test-workspace"),
            ToolNames = ["read", "write"],
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "test-agent",
                Channel = "signalr",
                ClientKind = "desktop"
            }
        });

        prompt.ShouldNotContain("client=");
    }
}
