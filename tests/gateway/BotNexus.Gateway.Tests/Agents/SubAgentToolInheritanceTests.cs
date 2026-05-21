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

    private static DefaultToolPolicyProvider CreatePolicyWithDenied(
        string agentId,
        IReadOnlyList<string> denied,
        Action<AgentId, IReadOnlyList<string>>? onSetDynamic = null)
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
            ToolIds = toolIds
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
