using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentExchangeAccessPolicyTests
{
    [Fact]
    public async Task OpenPolicy_AllowsUnlistedAgentPair()
    {
        // Initiator does NOT list target in SubAgentIds
        var initiator = AgentId.From("agent-a");
        var target = AgentId.From("agent-b");
        var registry = CreateRegistry(initiator, target, subAgentIds: []);
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Hello back" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "open" }));

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
        result.FinalResponse.ShouldBe("Hello back");
    }

    [Fact]
    public async Task WhitelistPolicy_RejectsUnlistedAgentPair()
    {
        var initiator = AgentId.From("agent-a");
        var target = AgentId.From("agent-b");
        var registry = CreateRegistry(initiator, target, subAgentIds: []);

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

        Func<Task> action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Hello"
        });

        await action.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WhitelistPolicy_AllowsListedAgentPair()
    {
        var initiator = AgentId.From("agent-a");
        var target = AgentId.From("agent-b");
        var registry = CreateRegistry(initiator, target, subAgentIds: ["agent-b"]);
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Allowed" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
    }

    [Fact]
    public async Task DefaultPolicy_IsOpen()
    {
        // No explicit exchangeOptions — default should be "open"
        var initiator = AgentId.From("agent-a");
        var target = AgentId.From("agent-b");
        var registry = CreateRegistry(initiator, target, subAgentIds: []);
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Default open" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
    }

    [Fact]
    public async Task WhitelistPolicy_AllowsRoleGrantedPair()
    {
        var initiator = AgentId.From("agent-a");
        var target = AgentId.From("agent-b");
        var registry = CreateRegistryWithRole(initiator, target, subAgentIds: [], subAgentRoles: ["researcher"], targetRole: "researcher");

        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Role granted" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
    }

    [Fact]
    public void IsOpen_CaseInsensitive()
    {
        new AgentExchangeOptions { AccessPolicy = "OPEN" }.IsOpen.ShouldBeTrue();
        new AgentExchangeOptions { AccessPolicy = "Open" }.IsOpen.ShouldBeTrue();
        new AgentExchangeOptions { AccessPolicy = "whitelist" }.IsOpen.ShouldBeFalse();
        new AgentExchangeOptions { AccessPolicy = "" }.IsOpen.ShouldBeFalse();
    }

    private static Mock<IAgentRegistry> CreateRegistry(AgentId initiator, AgentId target, IReadOnlyList<string> subAgentIds)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = subAgentIds
        });
        registry.Setup(r => r.Get(target)).Returns(new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Target",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = []
        });
        registry.Setup(r => r.Contains(target)).Returns(true);
        return registry;
    }

    private static Mock<IAgentRegistry> CreateRegistryWithRole(
        AgentId initiator, AgentId target,
        IReadOnlyList<string> subAgentIds, IReadOnlyList<string> subAgentRoles,
        string targetRole)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = subAgentIds,
            SubAgentRoles = subAgentRoles
        });
        registry.Setup(r => r.Get(target)).Returns(new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Target",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = [],
            Metadata = new Dictionary<string, object?> { ["role"] = targetRole }
        });
        registry.Setup(r => r.Contains(target)).Returns(true);
        return registry;
    }
}
