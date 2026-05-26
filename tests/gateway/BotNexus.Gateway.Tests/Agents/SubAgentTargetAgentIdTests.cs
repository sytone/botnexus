using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for <see cref="DefaultSubAgentManager"/> TargetAgentId behaviour (issue #377).
/// </summary>
public sealed class SubAgentTargetAgentIdTests
{
    [Fact]
    public async Task SpawnAsync_WithTargetAgentId_UsesTargetDescriptor()
    {
        // parent "nova", target "farnsworth" - spawn should use farnsworth's descriptor
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        AgentDescriptor? registeredDescriptor = null;
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("nova")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai",
                SystemPrompt = "You are Nova."
            });
        registry
            .Setup(r => r.Get(AgentId.From("farnsworth")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "claude-sonnet-4",
                ApiProvider = "anthropic",
                SystemPrompt = "You are Farnsworth, a coding assistant."
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry
            .Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d => registeredDescriptor = d);

        var manager = CreateManager(supervisor.Object, registry.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Write some code",
            TimeoutSeconds = 600,
            Mode = new Mirror(AgentId.From("farnsworth")),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

        var result = await manager.SpawnAsync(request);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
        registeredDescriptor.ShouldNotBeNull();
        registeredDescriptor!.ModelId.ShouldBe("claude-sonnet-4");
        registeredDescriptor.SystemPrompt.ShouldBe("You are Farnsworth, a coding assistant.");
    }

    [Fact]
    public async Task SpawnAsync_WithTargetAgentId_NotRegistered_Throws()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("nova")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai"
            });
        // "ghost-agent" is not registered — Get returns null
        registry
            .Setup(r => r.Get(AgentId.From("ghost-agent")))
            .Returns((AgentDescriptor?)null);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var manager = CreateManager(supervisor.Object, registry.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Do something",
            TimeoutSeconds = 600,
            Mode = new Mirror(AgentId.From("ghost-agent")),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

        Func<Task> act = () => manager.SpawnAsync(request);

        (await act.ShouldThrowAsync<KeyNotFoundException>())
            .Message.ShouldContain("ghost-agent");
    }

    [Fact]
    public async Task SpawnAsync_WithoutTargetAgentId_ClonesParent()
    {
        // Regression: no targetAgentId → child registered with parent descriptor
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        AgentDescriptor? registeredDescriptor = null;
        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("nova")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai",
                SystemPrompt = "You are Nova."
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry
            .Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback<AgentDescriptor>(d => registeredDescriptor = d);

        var manager = CreateManager(supervisor.Object, registry.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

        await manager.SpawnAsync(request);

        registeredDescriptor.ShouldNotBeNull();
        registeredDescriptor!.ModelId.ShouldBe("gpt-4o");
        registeredDescriptor.SystemPrompt.ShouldBe("You are Nova.");
    }

    [Fact]
    public async Task SpawnAsync_WithTargetAgentId_InheritsParentDenyList()
    {
        // Even when using target descriptor, parent's deny-list is applied to the child
        AgentId? registeredChildId = null;
        IReadOnlyList<string>? registeredDenyList = null;

        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("nova")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "gpt-4o",
                ApiProvider = "openai"
            });
        registry
            .Setup(r => r.Get(AgentId.From("farnsworth")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "claude-sonnet-4",
                ApiProvider = "anthropic"
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()));

        var policyConfig = new BotNexus.Gateway.Configuration.PlatformConfig
        {
            Agents = new Dictionary<string, BotNexus.Gateway.Configuration.AgentDefinitionConfig>
            {
                ["nova"] = new() { ToolPolicy = new BotNexus.Gateway.Configuration.ToolPolicyConfig { Denied = ["exec", "bash"] } }
            }
        };
        var optionsMonitor = new TestOptionsMonitor<BotNexus.Gateway.Configuration.PlatformConfig>(policyConfig);
        var policyProvider = new DefaultToolPolicyProvider(
            optionsMonitor,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultToolPolicyProvider>>().Object);
        policyProvider.OnDynamicDenyListSet = (agentId, denyList) =>
        {
            registeredChildId = agentId;
            registeredDenyList = denyList;
        };

        var manager = CreateManager(supervisor.Object, registry.Object, policyProvider);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("nova"),
            ParentSessionId = SessionId.From("nova-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            Mode = new Mirror(AgentId.From("farnsworth")),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

        await manager.SpawnAsync(request);

        registeredChildId.ShouldNotBeNull();
        registeredDenyList.ShouldNotBeNull();
        registeredDenyList.ShouldContain("exec");
        registeredDenyList.ShouldContain("bash");
    }

    private static DefaultSubAgentManager CreateManager(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        DefaultToolPolicyProvider? policyProvider = null)
    {
        var options = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions());

        return new DefaultSubAgentManager(
            supervisor,
            registry,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object,
            policyProvider: policyProvider);
    }

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("nova"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }
}
