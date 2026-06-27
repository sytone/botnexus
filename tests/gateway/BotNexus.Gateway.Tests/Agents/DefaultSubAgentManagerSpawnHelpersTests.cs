using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Unit tests for the cohesive helpers extracted from <see cref="DefaultSubAgentManager.SpawnAsync"/>
/// (#1630) - <see cref="DefaultSubAgentManager.ValidateToolGrants"/> (the deny-list / grant-escalation
/// guard) and <see cref="DefaultSubAgentManager.BuildChildFileAccessPolicy"/> (workspace-isolation
/// policy composition). These exercise the guard and the policy-build logic directly, without driving
/// the full 200+ line spawn path. The end-to-end behaviour is additionally guarded by
/// <see cref="SubAgentToolInheritanceTests"/> and <see cref="SubAgentWorkspaceIsolationTests"/>, which
/// continue to drive the same logic through <c>SpawnAsync</c> - this file is the direct-seam companion.
/// </summary>
public sealed class DefaultSubAgentManagerSpawnHelpersTests
{
    private const string ParentId = "parent-agent";

    // ---------------- ValidateToolGrants ----------------

    [Fact]
    public void ValidateToolGrants_ChildRequestsParentDeniedTool_Throws()
    {
        // Parent is denied exec+write. A child requesting exec is a privilege escalation
        // and must be rejected before any session is created.
        var manager = BuildManager(denied: ["exec", "write"]);
        var request = Request();

        var ex = Should.Throw<InvalidOperationException>(
            () => manager.ValidateToolGrants(request, toolIds: ["read", "exec"]));

        ex.Message.ShouldContain("exec");
    }

    [Fact]
    public void ValidateToolGrants_ChildWithinGrants_DoesNotThrow()
    {
        // None of the requested tools are in the parent deny-list - allowed.
        var manager = BuildManager(denied: ["exec", "write"]);
        var request = Request();

        Should.NotThrow(() => manager.ValidateToolGrants(request, toolIds: ["read", "list"]));
    }

    [Fact]
    public void ValidateToolGrants_DenyMatchIsCaseInsensitive_Throws()
    {
        // The escalation check must not be defeated by casing (OrdinalIgnoreCase).
        var manager = BuildManager(denied: ["Exec"]);
        var request = Request();

        Should.Throw<InvalidOperationException>(
            () => manager.ValidateToolGrants(request, toolIds: ["exec"]));
    }

    [Fact]
    public void ValidateToolGrants_NoParentDenyList_DoesNotThrow()
    {
        // Empty parent deny-list: nothing to escalate against.
        var manager = BuildManager(denied: []);
        var request = Request();

        Should.NotThrow(() => manager.ValidateToolGrants(request, toolIds: ["read", "write"]));
    }

    [Fact]
    public void ValidateToolGrants_NoRequestedTools_DoesNotThrow()
    {
        // A child that requests no tools cannot escalate, even with a populated parent deny-list.
        var manager = BuildManager(denied: ["exec"]);
        var request = Request();

        Should.NotThrow(() => manager.ValidateToolGrants(request, toolIds: null));
        Should.NotThrow(() => manager.ValidateToolGrants(request, toolIds: []));
    }

    // ---------------- BuildChildFileAccessPolicy ----------------

    [Fact]
    public void BuildChildFileAccessPolicy_DefaultIsolation_ReturnsNull()
    {
        // ShareWorkspace=false and no granted paths: the child stays fully isolated and the
        // helper returns null so the caller falls back to the base descriptor's FileAccess.
        var manager = BuildManager(denied: []);

        var policy = manager.BuildChildFileAccessPolicy(Request(shareWorkspace: false, grantedPaths: null));

        policy.ShouldBeNull();
    }

    [Fact]
    public void BuildChildFileAccessPolicy_ShareWorkspace_GrantsParentWorkspaceReadAndWrite()
    {
        const string parentWorkspace = "/home/user/.botnexus/agents/parent-agent/workspace";
        var manager = BuildManager(denied: [], parentWorkspacePath: parentWorkspace);

        var policy = manager.BuildChildFileAccessPolicy(Request(shareWorkspace: true));

        policy.ShouldNotBeNull();
        policy!.AllowedReadPaths.ShouldContain(p => p.Contains("parent-agent"));
        policy.AllowedWritePaths.ShouldContain(p => p.Contains("parent-agent"));
        policy.AllowedReadPaths.Count.ShouldBe(1);
        policy.AllowedWritePaths.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildChildFileAccessPolicy_GrantedPaths_AreReadOnly()
    {
        var manager = BuildManager(denied: []);

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedPaths: ["/data/shared", "/repos/project"]));

        policy.ShouldNotBeNull();
        policy!.AllowedReadPaths.Count.ShouldBe(2);
        // Granted paths confer read-only access; no write paths are added.
        policy.AllowedWritePaths.Count.ShouldBe(0);
    }

    [Fact]
    public void BuildChildFileAccessPolicy_ShareWorkspaceAndGrantedPaths_CombinesBoth()
    {
        const string parentWorkspace = "/home/user/.botnexus/agents/parent-agent/workspace";
        var manager = BuildManager(denied: [], parentWorkspacePath: parentWorkspace);

        var policy = manager.BuildChildFileAccessPolicy(
            Request(shareWorkspace: true, grantedPaths: ["/data/shared"]));

        policy.ShouldNotBeNull();
        // Parent workspace (read+write) plus one granted path (read only).
        policy!.AllowedReadPaths.Count.ShouldBe(2);
        policy.AllowedWritePaths.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildChildFileAccessPolicy_GrantedPathsWithBlankEntries_AreFiltered()
    {
        var manager = BuildManager(denied: []);

        var policy = manager.BuildChildFileAccessPolicy(
            Request(grantedPaths: ["/valid/path", "", "  ", "/another/path"]));

        policy.ShouldNotBeNull();
        // Blank / whitespace-only entries are skipped so they never widen access.
        policy!.AllowedReadPaths.Count.ShouldBe(2);
    }

    // ---------------- helpers ----------------

    private static SubAgentSpawnRequest Request(
        bool shareWorkspace = false,
        IReadOnlyList<string>? grantedPaths = null)
        => new()
        {
            ParentAgentId = AgentId.From(ParentId),
            ParentSessionId = SessionId.From($"{ParentId}-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = new Embody(SubAgentArchetype.General),
            ShareWorkspace = shareWorkspace,
            GrantedPaths = grantedPaths
        };

    private static DefaultToolPolicyProvider CreatePolicy(IReadOnlyList<string> denied)
    {
        var policyConfig = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                [ParentId] = new() { ToolPolicy = new ToolPolicyConfig { Denied = [.. denied] } }
            }
        };
        var optionsMonitor = new TestOptionsMonitor<PlatformConfig>(policyConfig);
        return new DefaultToolPolicyProvider(
            optionsMonitor,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultToolPolicyProvider>>().Object);
    }

    private static DefaultSubAgentManager BuildManager(
        IReadOnlyList<string> denied,
        string? parentWorkspacePath = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From(ParentId),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "openai"
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        if (parentWorkspacePath is not null)
        {
            workspaceManager
                .Setup(w => w.GetWorkspacePath(ParentId))
                .Returns(parentWorkspacePath);
        }

        return new DefaultSubAgentManager(
            new Mock<IAgentSupervisor>().Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object,
            workspaceManager: workspaceManager.Object,
            policyProvider: CreatePolicy(denied));
    }
}
