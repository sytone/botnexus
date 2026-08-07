using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Verifies that <see cref="DefaultSubAgentManager"/> enforces tool deny-list inheritance:
/// sub-agents cannot be granted tools that the parent is denied, and the child's
/// effective deny-list includes the parent's.
/// </summary>
public sealed class SubAgentToolInheritanceTests
{
    [Fact]
    public async Task SpawnAsync_ChildCannotBeGrantedParentDeniedTool()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var policyProvider = CreatePolicyWithDenied("parent-agent", ["exec", "write"]);
        var manager = CreateManager(supervisor.Object, policyProvider);

        // The spawn request asks for tools including one the parent is denied
        var request = CreateSpawnRequest(toolIds: ["read", "exec"]);

        Func<Task> act = () => manager.SpawnAsync(request);

        (await act.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("exec");
    }

    [Fact]
    public async Task SpawnAsync_ChildAllowedTools_NotInParentDenyList_Succeeds()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var policyProvider = CreatePolicyWithDenied("parent-agent", ["exec", "write"]);
        var manager = CreateManager(supervisor.Object, policyProvider);

        // All requested tools are safe — not in parent deny-list
        var request = CreateSpawnRequest(toolIds: ["read", "list"]);
        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SpawnAsync_RegistersChildDenyListInPolicyProvider()
    {
        AgentId? registeredChildId = null;
        IReadOnlyList<string>? registeredDenyList = null;

        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var policyProvider = CreatePolicyWithDenied("parent-agent", ["exec", "bash"],
            onSetDynamic: (agentId, denyList) =>
            {
                registeredChildId = agentId;
                registeredDenyList = denyList;
            });

        var manager = CreateManager(supervisor.Object, policyProvider);
        var request = CreateSpawnRequest();

        await manager.SpawnAsync(request);

        registeredChildId.ShouldNotBeNull();
        registeredDenyList.ShouldNotBeNull();
        registeredDenyList.ShouldContain("exec");
        registeredDenyList.ShouldContain("bash");
    }

    [Fact]
    public async Task SpawnAsync_NoParentDenyList_SpawnsSuccessfully()
    {
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var policyProvider = CreatePolicyWithDenied("parent-agent", []);
        var manager = CreateManager(supervisor.Object, policyProvider);

        var request = CreateSpawnRequest(toolIds: ["read", "write"]);
        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
    }

    /// <summary>
    /// #2847 clause 3: the inherited deny-list must already be installed when the child's runtime
    /// handle is created. The supervisor double interrogates the policy provider from INSIDE
    /// GetOrCreateAsync, which is the only vantage point that can tell "set before" from "set
    /// eventually" - the pre-existing RegistersChildDenyListInPolicyProvider test asserts the
    /// latter and passes either way, which is precisely why it did not catch this.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_InstallsInheritedDenyList_BeforeChildHandleIsCreated()
    {
        IReadOnlyList<string>? denyListVisibleAtHandleCreation = null;
        AgentId? childIdAtHandleCreation = null;

        var childHandle = CreateHandle();
        DefaultToolPolicyProvider? policyProvider = null;

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns((AgentId agentId, SessionId _, CancellationToken _) =>
            {
                // Sampled at the exact moment the child becomes able to reach the model.
                childIdAtHandleCreation = agentId;
                denyListVisibleAtHandleCreation = policyProvider!.GetEffectiveDenyList(agentId.Value);
                return Task.FromResult(childHandle.Object);
            });

        policyProvider = CreatePolicyWithDenied("parent-agent", ["exec", "bash"]);
        var manager = CreateManager(supervisor.Object, policyProvider);

        await manager.SpawnAsync(CreateSpawnRequest());

        childIdAtHandleCreation.ShouldNotBeNull();
        denyListVisibleAtHandleCreation.ShouldNotBeNull();
        denyListVisibleAtHandleCreation.ShouldContain(
            "exec",
            "the child handle can reach the model immediately, so a parent-denied tool must already be denied for it");
        denyListVisibleAtHandleCreation.ShouldContain("bash");
    }

    /// <summary>
    /// #2847 clause 2: an empty parent deny-list must still install an EXPLICIT empty policy for
    /// the child. The previous `if (count > 0)` guard left the slot absent, which reads identically
    /// to "never configured" and was asymmetric with RemoveDynamicDenyList, which always removes.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_EmptyParentDenyList_StillRegistersAnExplicitChildPolicy()
    {
        var registrations = new List<(AgentId AgentId, IReadOnlyList<string> DenyList)>();

        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var policyProvider = CreatePolicyWithDenied(
            "parent-agent",
            [],
            onSetDynamic: (agentId, denyList) => registrations.Add((agentId, denyList)));

        var manager = CreateManager(supervisor.Object, policyProvider);

        await manager.SpawnAsync(CreateSpawnRequest());

        registrations.ShouldHaveSingleItem();
        registrations[0].DenyList.ShouldBeEmpty();
    }

    /// <summary>
    /// #2847 clause 5: the child's policy slot must not outlive the spawn. The ephemeral child agent
    /// id is unique per spawn, so a leaked entry is unreachable rather than dangerous - but an
    /// unbounded map of dead ids is still a leak, and "unreachable" is an argument that stops being
    /// true the moment id minting changes. Now that registration is unconditional, removal is the
    /// only thing keeping the two symmetric, so it gets a test rather than an assumption.
    /// </summary>
    [Fact]
    public async Task CompletedSpawn_RemovesTheChildDenyListEntry()
    {
        var set = new List<AgentId>();
        var removed = new List<AgentId>();

        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var policyProvider = CreatePolicyWithDenied(
            "parent-agent",
            ["exec"],
            onSetDynamic: (agentId, _) => set.Add(agentId),
            onRemoveDynamic: removed.Add);

        var manager = CreateManager(supervisor.Object, policyProvider);

        var result = await manager.SpawnAsync(CreateSpawnRequest());
        await manager.KillAsync(result.SubAgentId, SessionId.From("root-session"));

        set.ShouldHaveSingleItem();
        removed.ShouldContain(
            set[0],
            "the deny-list entry installed for the ephemeral child id must not survive its spawn");
    }

    private static DefaultToolPolicyProvider CreatePolicyWithDenied(
        string agentId,
        IReadOnlyList<string> denied,
        Action<AgentId, IReadOnlyList<string>>? onSetDynamic = null,
        Action<AgentId>? onRemoveDynamic = null)
    {
        var policyConfig = new BotNexus.Gateway.Configuration.PlatformConfig
        {
            Agents = new Dictionary<string, BotNexus.Gateway.Configuration.AgentDefinitionConfig>
            {
                [agentId] = new() { ToolPolicy = new BotNexus.Gateway.Configuration.ToolPolicyConfig { Denied = [.. denied] } }
            }
        };
        var optionsMonitor = new TestOptionsMonitor<BotNexus.Gateway.Configuration.PlatformConfig>(policyConfig);
        var provider = new DefaultToolPolicyProvider(optionsMonitor, new Mock<Microsoft.Extensions.Logging.ILogger<DefaultToolPolicyProvider>>().Object);

        if (onSetDynamic is not null)
            provider.OnDynamicDenyListSet = onSetDynamic;

        if (onRemoveDynamic is not null)
            provider.OnDynamicDenyListRemoved = onRemoveDynamic;

        return provider;
    }

    private static DefaultSubAgentManager CreateManager(
        IAgentSupervisor supervisor,
        DefaultToolPolicyProvider policyProvider)
    {
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "openai"
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var options = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions());

        return new DefaultSubAgentManager(
            supervisor,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object,
            policyProvider: policyProvider);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest(IReadOnlyList<string>? toolIds = null)
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("root-session"),
            Task = "Do something",
            TimeoutSeconds = 600,
            Mode = new Embody(SubAgentArchetype.General, EmbodyCustomizations.Default with { ToolIds = toolIds }),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }
}
