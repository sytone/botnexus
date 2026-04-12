using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentConversationServiceTests
{
    [Fact]
    public async Task ConverseAsync_SingleTurn_CreatesSealedAgentAgentSessionVisibleToBothAgents()
    {
        var initiator = AgentId.From("nova");
        var target = AgentId.From("leela");
        var registry = CreateRegistry(initiator, target, ["leela"]);
        var sessionStore = new InMemorySessionStore();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("Review this design", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Looks good with two fixes." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentConversationService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentConversationService>.Instance);

        var result = await service.ConverseAsync(new ConversationRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Review this design",
            MaxTurns = 1
        });

        result.Status.Should().Be("sealed");
        result.Turns.Should().Be(2);
        result.Transcript.Should().ContainSingle(entry => entry.Role == "user" && entry.Content == "Review this design");
        result.Transcript.Should().ContainSingle(entry => entry.Role == "assistant" && entry.Content.Contains("Looks good"));

        var session = await sessionStore.GetAsync(result.SessionId);
        session.Should().NotBeNull();
        session!.SessionType.Should().Be(SessionType.AgentAgent);
        session.Status.Should().Be(GatewaySessionStatus.Sealed);
        session.Participants.Should().ContainSingle(p => p.Type == ParticipantType.Agent && p.Id == "nova" && p.Role == "initiator");
        session.Participants.Should().ContainSingle(p => p.Type == ParticipantType.Agent && p.Id == "leela" && p.Role == "target");

        var initiatorExistence = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        var targetExistence = await sessionStore.GetExistenceAsync(target, new ExistenceQuery());
        initiatorExistence.Should().Contain(item => item.SessionId == result.SessionId);
        targetExistence.Should().Contain(item => item.SessionId == result.SessionId);
    }

    [Fact]
    public async Task ConverseAsync_WhenTargetNotAllowed_ThrowsUnauthorizedAccessException()
    {
        var initiator = AgentId.From("nova");
        var target = AgentId.From("leela");
        var registry = CreateRegistry(initiator, target, []);
        var service = new AgentConversationService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentConversationService>.Instance);

        var action = () => service.ConverseAsync(new ConversationRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello"
        });

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ConverseAsync_WhenCycleDetected_ThrowsInvalidOperationException()
    {
        var initiator = AgentId.From("nova");
        var target = AgentId.From("leela");
        var registry = CreateRegistry(initiator, target, ["leela"]);
        var service = new AgentConversationService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions { AgentConversationMaxDepth = 4 }),
            NullLogger<AgentConversationService>.Instance);

        var action = () => service.ConverseAsync(new ConversationRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            CallChain = [AgentId.From("nova"), AgentId.From("leela")]
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cycle detected:*");
    }

    [Fact]
    public async Task ConverseAsync_WhenDepthExceeded_ThrowsInvalidOperationException()
    {
        var initiator = AgentId.From("nova");
        var target = AgentId.From("leela");
        var registry = CreateRegistry(initiator, target, ["leela"]);
        var service = new AgentConversationService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions { AgentConversationMaxDepth = 2 }),
            NullLogger<AgentConversationService>.Instance);

        var action = () => service.ConverseAsync(new ConversationRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            CallChain = [AgentId.From("alpha"), AgentId.From("nova")]
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded maximum configured depth*");
    }

    private static Mock<IAgentRegistry> CreateRegistry(AgentId initiator, AgentId target, IReadOnlyList<string> allowedTargets)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = allowedTargets
        });
        registry.Setup(r => r.Contains(target)).Returns(true);
        return registry;
    }
}
