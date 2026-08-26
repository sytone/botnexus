using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Pins that the spawn CALL SITE hands the base descriptor's policy to the composition step, by
/// driving the public <c>SpawnAsync</c> and reading the descriptor the child actually runs on.
/// <para>
/// <see cref="SubAgentInheritedDeniedPathsTests"/> covers the composition contract but supplies the
/// base policy itself, so it holds even when the orchestrator supplies nothing - which is exactly
/// what the bug was. These touch only public API, so they compile against the pre-fix manager and
/// fail there on the authorization assertion rather than on a signature change.
/// </para>
/// </summary>
public sealed class SubAgentSpawnInheritedDeniedPathsTests : IDisposable
{
    private const string ParentId = "parent-agent";
    private const string TargetId = "target-agent";

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));

    public SubAgentSpawnInheritedDeniedPathsTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the suite.
        }
    }

    /// <summary>
    /// Embody clones the PARENT descriptor, so the parent's denies must reach the registered child.
    /// The grant deliberately contains the denied directory: a child that kept the deny only by
    /// falling back to the base policy wholesale would lose the grant, so passing this needs both.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_EmbodyWithGrant_RegistersChildWithTheParentDeny()
    {
        var granted = Path.Combine(_tempRoot, "worktree");
        var denied = Path.Combine(granted, "secrets");
        var parent = Descriptor(ParentId, denies: [denied]);

        AgentDescriptor? child = null;
        var manager = BuildManager(parent, d => child = d);

        await manager.SpawnAsync(Request(grantedWritePaths: [granted]));

        child.ShouldNotBeNull();
        child!.FileAccess.ShouldNotBeNull();
        child.FileAccess!.DeniedPaths.ShouldBe([denied]);
        child.FileAccess.AllowedWritePaths.ShouldBe([Path.GetFullPath(granted)]);
    }

    /// <summary>
    /// Mirror bases the child on the TARGET descriptor (#562), so the target's deny has to survive
    /// the grant. The INITIATING parent's deny is asserted absent to make the mirror boundary
    /// explicit rather than merely unasserted: today path denies stop at it while tool denies cross
    /// (<c>ValidateToolGrants</c>, <c>SetDynamicDenyList</c>). This records that asymmetry so
    /// closing it has to come here first; it does not endorse it.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_MirrorWithGrant_RegistersChildWithTheTargetDenyNotTheParents()
    {
        var targetDenied = Path.Combine(_tempRoot, "target-secrets");
        var parentDenied = Path.Combine(_tempRoot, "parent-secrets");
        var parent = Descriptor(ParentId, denies: [parentDenied]);
        var target = Descriptor(TargetId, denies: [targetDenied]);

        AgentDescriptor? child = null;
        var manager = BuildManager(parent, d => child = d, mirrorTarget: target);

        await manager.SpawnAsync(Request(
            mode: new Mirror(AgentId.From(TargetId)),
            grantedWritePaths: [Path.Combine(_tempRoot, "worktree")]));

        child.ShouldNotBeNull();
        child!.FileAccess.ShouldNotBeNull();
        child.FileAccess!.DeniedPaths.ShouldContain(targetDenied);
        child.FileAccess.DeniedPaths.ShouldNotContain(parentDenied);
    }

    /// <summary>
    /// A relative deny means "&lt;my workspace&gt;/x" to the validator, so the call site has to
    /// re-anchor it onto the owning agent's workspace before the policy crosses onto the child.
    /// Carried across verbatim it re-points at the CHILD's workspace, leaving the parent's copy
    /// reachable through the grant.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_RelativeParentDeny_IsAnchoredToTheParentWorkspace()
    {
        var parentWorkspace = Path.Combine(_tempRoot, "parent-workspace");
        Directory.CreateDirectory(parentWorkspace);
        var parent = Descriptor(ParentId, denies: ["secrets"]);

        AgentDescriptor? child = null;
        var manager = BuildManager(parent, d => child = d, parentWorkspacePath: parentWorkspace);

        await manager.SpawnAsync(Request(grantedWritePaths: [parentWorkspace]));

        child.ShouldNotBeNull();
        child!.FileAccess.ShouldNotBeNull();
        child.FileAccess!.DeniedPaths.ShouldBe(
            [Path.GetFullPath(Path.Combine(parentWorkspace, "secrets"))]);
    }

    // ---------------- helpers ----------------

    private static SubAgentSpawnRequest Request(
        IReadOnlyList<string>? grantedWritePaths = null,
        SubAgentSpawnMode? mode = null)
        => new()
        {
            ParentAgentId = AgentId.From(ParentId),
            ParentSessionId = SessionId.From($"{ParentId}-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            InheritedConversationId = ConversationId.From("inherited-conv"),
            Mode = mode ?? new Embody(SubAgentArchetype.General),
            GrantedWritePaths = grantedWritePaths
        };

    private static AgentDescriptor Descriptor(string agentId, IReadOnlyList<string> denies)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "gpt-5-mini",
            ApiProvider = "openai",
            FileAccess = new FileAccessPolicy { DeniedPaths = denies }
        };

    /// <summary>
    /// Builds a manager whose collaborators are just sufficient for <c>SpawnAsync</c> to reach
    /// registration. The optional session/conversation/policy dependencies are deliberately left
    /// unwired so the spawn runs the real orchestration path with no stores standing in for it.
    /// The workspace manager is wired because a relative deny cannot be re-anchored without one.
    /// </summary>
    private static DefaultSubAgentManager BuildManager(
        AgentDescriptor parent,
        Action<AgentDescriptor> onRegister,
        AgentDescriptor? mirrorTarget = null,
        string? parentWorkspacePath = null)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(parent.AgentId);
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From($"{ParentId}-session"));
        handle
            .Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        handle
            .Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(parent.AgentId)).Returns(parent);
        if (mirrorTarget is not null)
            registry.Setup(r => r.Get(mirrorTarget.AgentId)).Returns(mirrorTarget);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>())).Callback(onRegister);

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        if (parentWorkspacePath is not null)
        {
            workspaceManager
                .Setup(w => w.GetWorkspacePath(ParentId))
                .Returns(parentWorkspacePath);
        }

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            new Mock<ILogger<DefaultSubAgentManager>>().Object,
            workspaceManager: workspaceManager.Object);
    }
}
