using System.Text.Json;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WhitelistPolicy_MalformedRole_ConverseAndDiscoveryHonorOnlyExplicitId(bool jsonRole, bool idGranted)
    {
        var initiator = AgentId.From("agent-a");
        var target = AgentId.From("agent-b");
        object role = jsonRole ? JsonSerializer.SerializeToElement(42) : 42;
        var registry = CreateRegistryWithRole(initiator, target,
            subAgentIds: idGranted ? ["AGENT-B"] : [], subAgentRoles: ["42"], targetRole: role);
        registry.Setup(r => r.GetAll()).Returns([registry.Object.Get(target).ShouldNotBeNull()]);
        var options = new AgentExchangeOptions { AccessPolicy = "whitelist" };
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Explicit ID granted" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var service = new AgentExchangeService(
            registry.Object, supervisor.Object, sessionStore, conversationStore,
            Options.Create(new GatewayOptions()), NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(options));

        var discovery = await new ListAgentsTool(registry.Object, initiator, options)
            .ExecuteAsync("discovery", new Dictionary<string, object?>());
        using var entries = JsonDocument.Parse(discovery.Content[0].Value);
        var entry = entries.RootElement.EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("agentId").GetString().ShouldBe(target.Value);
        entry.GetProperty("canConverse").GetBoolean().ShouldBe(idGranted);
        var request = new AgentExchangeRequest
        {
            InitiatorId = initiator, TargetId = target, Message = "Hello", MaxTurns = 1
        };
        if (idGranted)
        {
            var result = await service.ConverseAsync(request);
            result.Status.ShouldBe("sealed");
            result.FinalResponse.ShouldBe("Explicit ID granted");
            supervisor.Verify(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            await Should.ThrowAsync<UnauthorizedAccessException>(() => service.ConverseAsync(request));
            supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public void PeerAccessPolicy_UnregisteredFederationTarget_ExplicitIdGrantsWithoutDescriptor()
    {
        var target = AgentId.From("remote@peer");
        var caller = new AgentDescriptor
        {
            AgentId = AgentId.From("caller"), DisplayName = "Caller", ModelId = "m", ApiProvider = "p",
            SubAgentIds = ["REMOTE@PEER"], SubAgentRoles = ["reviewer"]
        };
        var options = new AgentExchangeOptions { AccessPolicy = "whitelist" };

        PeerAccessPolicy.IsAllowed(options, caller, target, target: null).ShouldBeTrue();
        PeerAccessPolicy.IsAllowed(options, caller with { SubAgentIds = [] }, target, target: null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeerAccessPolicy_WhitelistMissingCaller_DeniesLocalAndFederatedTargets(bool localTarget)
    {
        var targetId = AgentId.From("target");
        AgentDescriptor? target = localTarget ? new AgentDescriptor
        {
            AgentId = targetId, DisplayName = "Target", ModelId = "m", ApiProvider = "p",
            Metadata = new Dictionary<string, object?> { ["role"] = "reviewer" }
        } : null;

        PeerAccessPolicy.IsAllowed(new AgentExchangeOptions { AccessPolicy = "whitelist" },
            initiator: null, targetId, target).ShouldBeFalse();
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
        object targetRole)
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
