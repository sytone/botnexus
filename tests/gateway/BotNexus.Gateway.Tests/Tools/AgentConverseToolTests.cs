using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class AgentConverseToolTests
{
    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));
        tool.Name.ShouldBe("agent_converse");
        tool.Label.ShouldBe("Agent Converse");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenRequiredArgsMissing_Throws()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await action.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionHasCallChain_ForwardsChainToAgentExchangeRequest()
    {
        AgentExchangeRequest? captured = null;
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AgentExchangeResult
            {
                SessionId = SessionId.From("nova::agent-agent::leela::abc123"),
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });

        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("test-agent"));
        session.Metadata["callChain"] = new[] { "alpha", "test-agent" };
        await store.SaveAsync(session);

        var tool = new AgentConverseTool(service.Object, store, AgentId.From("test-agent"), SessionId.From("session-1"));
        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Review this plan"
        });

        captured.ShouldNotBeNull();
        captured!.CallChain.Select(id => id.Value).ShouldBe(new[] { "alpha", "test-agent" });
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoCallChain_UsesInitiatorAsDefaultChain()
    {
        AgentExchangeRequest? captured = null;
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AgentExchangeResult
            {
                SessionId = SessionId.From("nova::agent-agent::leela::abc123"),
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });

        var store = new InMemorySessionStore();
        var tool = new AgentConverseTool(service.Object, store, AgentId.From("test-agent"), SessionId.From("session-1"));
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Review this plan",
            ["maxTurns"] = 3
        });

        captured.ShouldNotBeNull();
        captured!.CallChain.ShouldHaveSingleItem().Value.ShouldBe("test-agent");
        captured.MaxTurns.ShouldBe(3);
        ReadText(result).ShouldContain("\"sessionId\"");
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(item => item.Type == BotNexus.Agent.Core.Types.AgentToolContentType.Text).Value;
}
