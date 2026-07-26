using BotNexus.Domain.Primitives;
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
            AgentId.From("test-agent-1"), "exec", "tc-1",
            new Dictionary<string, object?> { ["cmd"] = "rm -rf /" });

        var result = await handler.HandleAsync(evt);

        result.ShouldNotBeNull();
        var deniedResult = result ?? throw new InvalidOperationException("Expected denied result.");
        deniedResult.Denied.ShouldBeTrue();
        deniedResult.DenyReason.ShouldNotBeNull();
        var denyReason = deniedResult.DenyReason ?? throw new InvalidOperationException("Expected deny reason.");
        denyReason.ShouldContain("exec");
    }

    [Fact]
    public async Task HookHandler_AllowedTool_ReturnsNull()
    {
        var provider = CreateProvider();
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            AgentId.From("test-agent-1"), "read", "tc-2",
            new Dictionary<string, object?> { ["file"] = "readme.md" });

        var result = await handler.HandleAsync(evt);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task HookHandler_DangerousButNotDenied_DefaultFallbackAllows_ReturnsNull()
    {
        // #2391: the default askFallback posture is 'allow', which preserves unattended operation.
        var provider = CreateProvider();
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            AgentId.From("test-agent-1"), "exec", "tc-3",
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

    // ─── Issue #667: runtime-pinned tools bypass deny-list ──────────────────────

    /// <summary>Static RuntimePinnedTools cannot be denied by per-agent deny-list config.</summary>
    [Theory]
    [InlineData("ask_user")]
    [InlineData("canvas")]
    [InlineData("memory_save")]
    [InlineData("memory_search")]
    [InlineData("memory_get")]
    [InlineData("session")]
    [InlineData("conversation")]
    public void IsDenied_RuntimePinnedTool_ReturnsFalse_EvenWhenInDenyList(string toolName)
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["test-agent"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig { Denied = [toolName] }
                }
            }
        };
        var provider = CreateProvider(config);

        // Tool in deny-list but also in RuntimePinnedTools -- must not be denied.
        provider.IsDenied(toolName, "test-agent").ShouldBeFalse(
            $"runtime-pinned tool '{toolName}' must never be denied");
    }

    /// <summary>PinTool registers a dynamic pin; the pinned tool bypasses deny-list.</summary>
    [Fact]
    public void IsDenied_DynamicallyPinnedTool_ReturnsFalse_EvenWhenInDenyList()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["a"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig { Denied = ["my_custom_tool"] }
                }
            }
        };
        var provider = CreateProvider(config);
        provider.PinTool("my_custom_tool");

        provider.IsDenied("my_custom_tool", "a").ShouldBeFalse(
            "dynamically-pinned tool must bypass deny-list");
    }

    /// <summary>Non-pinned tools in the deny-list are still denied normally.</summary>
    [Fact]
    public void IsDenied_NonPinnedDeniedTool_ReturnsTrueAsNormal()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["a"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig { Denied = ["some_blocked_tool"] }
                }
            }
        };
        var provider = CreateProvider(config);

        provider.IsDenied("some_blocked_tool", "a").ShouldBeTrue();
    }

    /// <summary>ToolPolicyHookHandler does not deny runtime-pinned tools
    /// even when they appear in the agent's deny-list.</summary>
    [Fact]
    public async Task HookHandler_RuntimePinnedTool_NotDenied_EvenWhenInDenyList()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["test-agent-pinned"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig { Denied = ["ask_user"] }
                }
            }
        };
        var provider = CreateProvider(config);
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            AgentId.From("test-agent-pinned"), "ask_user", "tc-pinned",
            new Dictionary<string, object?> { ["prompt"] = "Are you sure?" });

        var result = await handler.HandleAsync(evt);
        result.ShouldBeNull("runtime-pinned ask_user must not be denied even when in deny-list");
    }

    // --- Issue #2391: approval fallback posture (askFallback) -------------------

    /// <summary>Collecting sink so tests can assert the emitted approval-boundary events.</summary>
    private sealed class CollectingSecurityEventSink : ISecurityEventSink
    {
        public List<SecurityEvent> Events { get; } = [];
        public void Record(SecurityEvent securityEvent) => Events.Add(securityEvent);
        public IReadOnlyList<SecurityEvent> Snapshot() => Events;
        public int Count => Events.Count;
        public void Clear() => Events.Clear();
    }

    private sealed class ThrowingSecurityEventSink : ISecurityEventSink
    {
        public void Record(SecurityEvent securityEvent) => throw new InvalidOperationException("sink fault");
        public IReadOnlyList<SecurityEvent> Snapshot() => [];
        public int Count => 0;
        public void Clear() { }
    }

    private static PlatformConfig ConfigWithAskFallback(
        string agentId,
        string? askFallback,
        List<string>? askFallbackAllow = null) =>
        new()
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                [agentId] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        AskFallback = askFallback,
                        AskFallbackAllow = askFallbackAllow
                    }
                }
            }
        };

    [Fact]
    public void GetApprovalFallback_NoConfiguration_DefaultsToAllow()
    {
        var provider = CreateProvider();
        provider.GetApprovalFallback("exec", "unconfigured-agent")
            .ShouldBe(ToolApprovalFallback.Allow, "the default must preserve unattended operation");
    }

    [Fact]
    public void GetApprovalFallback_NullAgent_DefaultsToAllow()
    {
        var provider = CreateProvider();
        provider.GetApprovalFallback("exec").ShouldBe(ToolApprovalFallback.Allow);
    }

    [Theory]
    [InlineData("deny", ToolApprovalFallback.Deny)]
    [InlineData("DENY", ToolApprovalFallback.Deny)]
    [InlineData("allow", ToolApprovalFallback.Allow)]
    [InlineData("nonsense", ToolApprovalFallback.Allow)]
    [InlineData("", ToolApprovalFallback.Allow)]
    public void GetApprovalFallback_ConfiguredValue_ResolvesPosture(string configured, ToolApprovalFallback expected)
    {
        var provider = CreateProvider(ConfigWithAskFallback("a", configured));
        provider.GetApprovalFallback("exec", "a").ShouldBe(expected);
    }

    [Fact]
    public void GetApprovalFallback_ExemptedTool_ReturnsAllow_EvenUnderDeny()
    {
        var provider = CreateProvider(ConfigWithAskFallback("a", "deny", ["write"]));
        provider.GetApprovalFallback("write", "a")
            .ShouldBe(ToolApprovalFallback.Allow, "askFallbackAllow exempts a named tool");
        provider.GetApprovalFallback("exec", "a").ShouldBe(ToolApprovalFallback.Deny);
    }

    /// <summary>
    /// The core #2391 regression: an approval-required tool under askFallback=deny must NOT
    /// silently execute. Before the fix the handler returned null here for every tool.
    /// </summary>
    [Theory]
    [InlineData("exec")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("bash")]
    [InlineData("process")]
    public async Task HookHandler_ApprovalRequired_AskFallbackDeny_DoesNotSilentlyExecute(string toolName)
    {
        var provider = CreateProvider(ConfigWithAskFallback("locked-agent", "deny"));
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            AgentId.From("locked-agent"), toolName, "tc-fc",
            new Dictionary<string, object?> { ["cmd"] = "rm -rf /" });

        var result = await handler.HandleAsync(evt);

        result.ShouldNotBeNull(
            $"approval-required tool '{toolName}' must not fall through to execution under askFallback=deny");
        var denied = result ?? throw new InvalidOperationException("Expected a result.");
        denied.Denied.ShouldBeTrue();
        var reason = denied.DenyReason ?? throw new InvalidOperationException("Expected a deny reason.");
        reason.ShouldContain("ask-fallback-deny");
        reason.ShouldContain(toolName);
    }

    [Fact]
    public async Task HookHandler_ApprovalRequired_AskFallbackDeny_EmitsDenySecurityEvent()
    {
        var sink = new CollectingSecurityEventSink();
        var provider = CreateProvider(ConfigWithAskFallback("locked-agent", "deny"));
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance,
            sink);

        var evt = new BeforeToolCallEvent(
            AgentId.From("locked-agent"), "exec", "tc-ev",
            new Dictionary<string, object?> { ["cmd"] = "whoami" });

        await handler.HandleAsync(evt);

        var recorded = sink.Events.ShouldHaveSingleItem();
        recorded.Category.ShouldBe(SecurityEventCategory.Approval);
        recorded.Policy.ShouldBe(SecurityPolicyDecision.Deny);
        recorded.Outcome.ShouldBe(SecurityEventOutcome.Denied);
        recorded.Severity.ShouldBe(SecurityEventSeverity.Medium);
        var target = recorded.Target ?? throw new InvalidOperationException("Expected target.");
        target.Reference.ShouldBe("exec");
        // The actor id must be a pseudonym, never the raw agent id.
        var actor = recorded.Actor ?? throw new InvalidOperationException("Expected actor.");
        actor.Id.ShouldNotBe("locked-agent");
    }

    [Fact]
    public async Task HookHandler_ApprovalRequired_AskFallbackAllow_EmitsAllowSecurityEventAndProceeds()
    {
        var sink = new CollectingSecurityEventSink();
        var provider = CreateProvider();
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance,
            sink);

        var evt = new BeforeToolCallEvent(
            AgentId.From("unattended-agent"), "exec", "tc-allow",
            new Dictionary<string, object?> { ["cmd"] = "whoami" });

        var result = await handler.HandleAsync(evt);

        result.ShouldBeNull("the default posture must keep unattended automation working");
        var recorded = sink.Events.ShouldHaveSingleItem();
        recorded.Policy.ShouldBe(SecurityPolicyDecision.Allow);
        recorded.Category.ShouldBe(SecurityEventCategory.Approval);
    }

    /// <summary>A safe tool is never subject to the approval fallback, even under deny.</summary>
    [Fact]
    public async Task HookHandler_SafeTool_AskFallbackDeny_StillAllowedAndEmitsNothing()
    {
        var sink = new CollectingSecurityEventSink();
        var provider = CreateProvider(ConfigWithAskFallback("locked-agent", "deny"));
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance,
            sink);

        var evt = new BeforeToolCallEvent(
            AgentId.From("locked-agent"), "read", "tc-safe",
            new Dictionary<string, object?> { ["file"] = "readme.md" });

        var result = await handler.HandleAsync(evt);

        result.ShouldBeNull("safe tools never require approval");
        sink.Events.ShouldBeEmpty();
    }

    /// <summary>
    /// A per-agent NeverApprove entry short-circuits before the fallback, so a trusted tool keeps
    /// working even when the agent is otherwise fail-closed.
    /// </summary>
    [Fact]
    public async Task HookHandler_NeverApproveTool_AskFallbackDeny_StillAllowed()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["locked-agent"] = new AgentDefinitionConfig
                {
                    ToolPolicy = new ToolPolicyConfig
                    {
                        AskFallback = "deny",
                        NeverApprove = ["write"]
                    }
                }
            }
        };
        var provider = CreateProvider(config);
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance);

        var evt = new BeforeToolCallEvent(
            AgentId.From("locked-agent"), "write", "tc-trusted",
            new Dictionary<string, object?> { ["path"] = "a.txt" });

        (await handler.HandleAsync(evt)).ShouldBeNull();
    }

    /// <summary>A faulting sink must never change the policy outcome.</summary>
    [Fact]
    public async Task HookHandler_FaultingSecurityEventSink_DoesNotChangeDenyOutcome()
    {
        var provider = CreateProvider(ConfigWithAskFallback("locked-agent", "deny"));
        var handler = new ToolPolicyHookHandler(
            provider,
            NullLogger<ToolPolicyHookHandler>.Instance,
            new ThrowingSecurityEventSink());

        var evt = new BeforeToolCallEvent(
            AgentId.From("locked-agent"), "exec", "tc-throw",
            new Dictionary<string, object?> { ["cmd"] = "whoami" });

        var result = await handler.HandleAsync(evt);
        var denied = result ?? throw new InvalidOperationException("Expected a denied result.");
        denied.Denied.ShouldBeTrue();
    }
}
