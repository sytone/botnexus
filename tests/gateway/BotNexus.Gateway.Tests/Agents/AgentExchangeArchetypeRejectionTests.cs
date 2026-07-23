using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Regression tests for #2136: reserved sub-agent archetype ids (researcher, coder, planner,
/// reviewer, writer, analyst) must be rejected as <c>agent_converse</c> targets BEFORE any session
/// or conversation is created, with actionable guidance pointing to <c>spawn_subagent(archetype)</c>.
/// Rejecting early prevents the descriptor-creation failure ("ModelId is required; ApiProvider is
/// required") that previously surfaced as a fatal UnobservedTaskException and drove retry storms
/// from stale conversations/sessions.
/// </summary>
public sealed class AgentExchangeArchetypeRejectionTests
{
    [Theory]
    [InlineData("coder")]
    [InlineData("reviewer")]
    [InlineData("researcher")]
    [InlineData("planner")]
    [InlineData("writer")]
    [InlineData("analyst")]
    public async Task ConverseAsync_ToArchetypeId_RejectedBeforeSessionCreation(string archetypeId)
    {
        var sessionStore = new Mock<ISessionStore>(MockBehavior.Strict);
        var conversationStore = new Mock<IConversationStore>(MockBehavior.Strict);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("nova"))).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("nova"),
            DisplayName = "Nova",
            ModelId = "gpt-4o",
            ApiProvider = "openai"
        });
        // The archetype id is NOT a registered named agent.
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore.Object,
            conversationStore.Object,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var request = new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("nova"),
            TargetId = AgentId.From(archetypeId),
            Message = "hello",
            MaxTurns = 1
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.ConverseAsync(request));
        Assert.Contains(archetypeId, ex.Message);
        Assert.Contains("spawn_subagent", ex.Message);

        // No session/conversation/supervisor interaction: the strict mocks would throw if touched.
        sessionStore.VerifyNoOtherCalls();
        conversationStore.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
    }
}
