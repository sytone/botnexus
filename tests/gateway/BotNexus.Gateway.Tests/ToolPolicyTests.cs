using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Hooks;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

public sealed class ToolPolicyTests
{
    private static DefaultToolPolicyProvider CreateProvider(PlatformConfig? config = null)
    {
        config ??= new PlatformConfig();
        var monitor = new Moq.Mock<IOptionsMonitor<PlatformConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);
        return new DefaultToolPolicyProvider(
            monitor.Object,
            NullLogger<DefaultToolPolicyProvider>.Instance);
    }

    // ── Default risk levels ──────────────────────────────────────────

    [Theory]
    [InlineData("exec", ToolRiskLevel.Dangerous)]
    [InlineData("write", ToolRiskLevel.Dangerous)]
    [InlineData("edit", ToolRiskLevel.Dangerous)]
    [InlineData("process", ToolRiskLevel.Dangerous)]
    [InlineData("bash", ToolRiskLevel.Dangerous)]
    [InlineData("sessions_spawn", ToolRiskLevel.Dangerous)]
    [InlineData("sessions_send", ToolRiskLevel.Dangerous)]
    [InlineData("cron", ToolRiskLevel.Dangerous)]
    [InlineData("gateway", ToolRiskLevel.Dangerous)]
    public void GetRiskLevel_KnownDangerousTools_ReturnsDangerous(string toolName, ToolRiskLevel expected)
    {
        var provider = CreateProvider();
        provider.GetRiskLevel(toolName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("search")]
    [InlineData("list_files")]
    public void GetRiskLevel_SafeTools_ReturnsSafe(string toolName)
    {
        var provider = CreateProvider();
        provider.GetRiskLevel(toolName).ShouldBe(ToolRiskLevel.Safe);
    }

    // ── Approval defaults ────────────────────────────────────────────

    [Theory]
    [InlineData("exec")]
    [InlineData("write")]
    [InlineData("bash")]
    public void RequiresApproval_DangerousTool_ReturnsTrue(string toolName)
    {
        var provider = CreateProvider();
        provider.RequiresApproval(toolName).ShouldBeTrue();
    }

    [Fact]
    public void RequiresApproval_SafeTool_ReturnsFalse()
    {
        var provider = CreateProvider();
        provider.RequiresApproval("read").ShouldBeFalse();
    }

    // ── Per-agent override: NeverApprove ──────────────────────────────

    [Fact]
    public void RequiresApproval_AgentNeverApproveOverride_ReturnsFalse()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["test-agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        NeverApprove = ["exec", "bash"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);
        provider.RequiresApproval("exec", "test-agent-1").ShouldBeFalse();
        provider.RequiresApproval("bash", "test-agent-1").ShouldBeFalse();
        // Other agents still require approval
        provider.RequiresApproval("exec", "test-agent-2").ShouldBeTrue();
    }

    // ── Per-agent override: AlwaysApprove ─────────────────────────────

    [Fact]
    public void RequiresApproval_AgentAlwaysApproveOverride_ReturnsTrue()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["test-agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        AlwaysApprove = ["read"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);
        // read is normally safe, but this agent requires approval
        provider.RequiresApproval("read", "test-agent-1").ShouldBeTrue();
    }

    // ── HTTP deny list ───────────────────────────────────────────────

    [Fact]
    public void GetDeniedForHttp_ContainsExpectedTools()
    {
        var provider = CreateProvider();
        var denied = provider.GetDeniedForHttp();

        denied.ShouldContain("sessions_spawn");
        denied.ShouldContain("sessions_send");
        denied.ShouldContain("cron");
        denied.ShouldContain("gateway");
        denied.ShouldContain("whatsapp_login");
    }

    // ── Per-agent denied tools ───────────────────────────────────────

    [Fact]
    public void IsDenied_AgentDeniedTool_ReturnsTrue()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["test-agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        Denied = ["exec"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);
        provider.IsDenied("exec", "test-agent-1").ShouldBeTrue();
        provider.IsDenied("read", "test-agent-1").ShouldBeFalse();
    }

    // ── Hook handler: denied tool ────────────────────────────────────

    [Fact]
    public async Task HookHandler_DeniedTool_ReturnsDenyResult()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["test-agent-1"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        Denied = ["exec"]
                    }
                }
            }
        };

        var provider = CreateProvider(config);
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            "test-agent-1", "exec", "tc-1",
            new Dictionary<string, object?> { ["cmd"] = "rm -rf /" });

        var result = await handler.HandleAsync(evt);

        result.ShouldNotBeNull();
        result!.Denied.ShouldBeTrue();
        result.DenyReason.ShouldContain("exec");
    }

    [Fact]
    public async Task HookHandler_AllowedTool_ReturnsNull()
    {
        var provider = CreateProvider();
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            "test-agent-1", "read", "tc-2",
            new Dictionary<string, object?> { ["file"] = "readme.md" });

        var result = await handler.HandleAsync(evt);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task HookHandler_DangerousButNotDenied_ReturnsNull()
    {
        // Dangerous tools log a warning but don't deny (approval UI not yet built)
        var provider = CreateProvider();
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            "test-agent-1", "exec", "tc-3",
            new Dictionary<string, object?> { ["cmd"] = "echo hello" });

        var result = await handler.HandleAsync(evt);
        result.ShouldBeNull();
    }

    [Fact]
    public void HookHandler_HasHighPriority()
    {
        var provider = CreateProvider();
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        handler.Priority.ShouldBeLessThan(0, "policy handler should run before other handlers");
    }
}
