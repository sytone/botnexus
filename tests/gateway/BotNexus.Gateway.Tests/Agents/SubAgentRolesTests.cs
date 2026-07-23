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

/// <summary>
/// Tests for role-based sub-agent grants (SubAgentRoles on AgentDescriptor).
/// </summary>
public sealed class SubAgentRolesTests
{
    // -------------------------------------------------------------------------
    // AgentExchangeService — role-based authorization
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConverseAsync_WithMatchingSubAgentRole_Succeeds()
    {
        var initiator = AgentId.From("orchestrator");
        var target = AgentId.From("specialist-agent");

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Orchestrator",
            ModelId = "test",
            ApiProvider = "test",
            SubAgentIds = [],
            SubAgentRoles = ["specialist"]
        });
        var targetDescriptor = new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Researcher",
            ModelId = "test",
            ApiProvider = "test",
            Metadata = new Dictionary<string, object?> { ["role"] = "specialist" }
        };
        registry.Setup(r => r.Get(target)).Returns(targetDescriptor);
        registry.Setup(r => r.Contains(target)).Returns(true);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Done." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
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
    public async Task ConverseAsync_WithNonMatchingSubAgentRole_ThrowsUnauthorized()
    {
        var initiator = AgentId.From("orchestrator");
        var target = AgentId.From("specialist-agent");

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Orchestrator",
            ModelId = "test",
            ApiProvider = "test",
            SubAgentIds = [],
            SubAgentRoles = ["analyst"] // does not match "specialist"
        });
        var targetDescriptor = new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Researcher",
            ModelId = "test",
            ApiProvider = "test",
            Metadata = new Dictionary<string, object?> { ["role"] = "specialist" }
        };
        registry.Setup(r => r.Get(target)).Returns(targetDescriptor);
        registry.Setup(r => r.Contains(target)).Returns(true);

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator,
                TargetId = target,
                Message = "Hello",
                MaxTurns = 1
            }));
    }

    [Fact]
    public async Task ConverseAsync_RoleMatchIsCaseInsensitive()
    {
        var initiator = AgentId.From("orchestrator");
        var target = AgentId.From("specialist-agent");

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Orchestrator",
            ModelId = "test",
            ApiProvider = "test",
            SubAgentIds = [],
            SubAgentRoles = ["Specialist"] // uppercase
        });
        var targetDescriptor = new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Researcher",
            ModelId = "test",
            ApiProvider = "test",
            Metadata = new Dictionary<string, object?> { ["role"] = "specialist" } // lowercase
        };
        registry.Setup(r => r.Get(target)).Returns(targetDescriptor);
        registry.Setup(r => r.Contains(target)).Returns(true);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Done." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
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
    public async Task ConverseAsync_TargetWithNoRole_RoleGrantDoesNotApply()
    {
        var initiator = AgentId.From("orchestrator");
        var target = AgentId.From("specialist-agent");

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Orchestrator",
            ModelId = "test",
            ApiProvider = "test",
            SubAgentIds = [],
            SubAgentRoles = ["specialist"]
        });
        var targetDescriptor = new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Researcher",
            ModelId = "test",
            ApiProvider = "test"
            // No metadata.role
        };
        registry.Setup(r => r.Get(target)).Returns(targetDescriptor);
        registry.Setup(r => r.Contains(target)).Returns(true);

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            service.ConverseAsync(new AgentExchangeRequest
            {
                InitiatorId = initiator,
                TargetId = target,
                Message = "Hello",
                MaxTurns = 1
            }));
    }

    // -------------------------------------------------------------------------
    // ListAgentsTool — CanConverse reflects role grants
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListAgentsTool_CanConverse_TrueForRoleMatch()
    {
        var callerDescriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("caller"),
            DisplayName = "Caller",
            ModelId = "test",
            ApiProvider = "test",
            SubAgentRoles = ["specialist"]
        };
        var specialistAgent = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-b"),
            DisplayName = "Agent B",
            ModelId = "test",
            ApiProvider = "test",
            Metadata = new Dictionary<string, object?> { ["role"] = "specialist" }
        };
        var notGranted = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-c"),
            DisplayName = "Agent C",
            ModelId = "test",
            ApiProvider = "test"
        };

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([callerDescriptor, specialistAgent, notGranted]);
        registry.Setup(r => r.Get(callerDescriptor.AgentId)).Returns(callerDescriptor);

        var whitelistPolicy = new AgentExchangeOptions { AccessPolicy = "whitelist" };
        var tool = new ListAgentsTool(registry.Object, callerDescriptor.AgentId, whitelistPolicy);
        var result = await tool.ExecuteAsync("tc", new Dictionary<string, object?>());

        var entries = JsonSerializer.Deserialize<JsonElement>(result.Content[0].Value);
        var bEntry = entries.EnumerateArray().First(e => e.GetProperty("agentId").GetString() == "agent-b");
        var cEntry = entries.EnumerateArray().First(e => e.GetProperty("agentId").GetString() == "agent-c");

        bEntry.GetProperty("canConverse").GetBoolean().ShouldBeTrue();
        cEntry.GetProperty("canConverse").GetBoolean().ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // AgentDescriptor — SubAgentRoles default
    // -------------------------------------------------------------------------

    [Fact]
    public void AgentDescriptor_SubAgentRoles_DefaultsToEmpty()
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test"),
            DisplayName = "Test",
            ModelId = "m",
            ApiProvider = "p"
        };

        descriptor.SubAgentRoles.ShouldBeEmpty();
    }
}
