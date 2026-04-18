using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using FluentAssertions;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class AgentConverseToolTests
{
    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentConversationService>(), new InMemorySessionStore(), "nova", "session-1");
        tool.Name.Should().Be("agent_converse");
        tool.Label.Should().Be("Agent Converse");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenRequiredArgsMissing_Throws()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentConversationService>(), new InMemorySessionStore(), "nova", "session-1");

        var action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionHasCallChain_ForwardsChainToConversationRequest()
    {
        ConversationRequest? captured = null;
        var service = new Mock<IAgentConversationService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<ConversationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AgentConversationResult
            {
                SessionId = "nova::agent-agent::leela::abc123",
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });

        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("session-1", "nova");
        session.Metadata["callChain"] = new[] { "alpha", "nova" };
        await store.SaveAsync(session);

        var tool = new AgentConverseTool(service.Object, store, "nova", "session-1");
        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "leela",
            ["message"] = "Review this plan"
        });

        captured.Should().NotBeNull();
        captured!.CallChain.Select(id => id.Value).Should().Equal("alpha", "nova");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoCallChain_UsesInitiatorAsDefaultChain()
    {
        ConversationRequest? captured = null;
        var service = new Mock<IAgentConversationService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<ConversationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AgentConversationResult
            {
                SessionId = "nova::agent-agent::leela::abc123",
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });

        var store = new InMemorySessionStore();
        var tool = new AgentConverseTool(service.Object, store, "nova", "session-1");
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "leela",
            ["message"] = "Review this plan",
            ["maxTurns"] = 3
        });

        captured.Should().NotBeNull();
        captured!.CallChain.Select(id => id.Value).Should().Equal("nova");
        captured.MaxTurns.Should().Be(3);
        ReadText(result).Should().Contain("\"sessionId\"");
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(item => item.Type == BotNexus.Agent.Core.Types.AgentToolContentType.Text).Value;
}
