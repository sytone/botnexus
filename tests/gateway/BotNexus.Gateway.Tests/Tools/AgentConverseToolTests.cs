using System.Runtime.CompilerServices;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
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
                ConversationId = ConversationId.Create(),
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
                ConversationId = ConversationId.Create(),
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

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsExceedsCeiling_ClampsToCeiling()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 10 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Run forever",
            ["maxTurns"] = 100000
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(10);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsWithinCeiling_PassesThrough()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 30 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Iterate a few times",
            ["maxTurns"] = 7
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(7);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsBelowOne_ClampsToOne()
    {
        var (service, captured) = CreateCapturingService();
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Hello",
            ["maxTurns"] = 0
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoOptions_DefaultsToCeilingOfThirty()
    {
        var (service, captured) = CreateCapturingService();
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Run forever",
            ["maxTurns"] = 999
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(30);
    }

    [Fact]
    public void Definition_AdvertisesMaximumMatchingCeiling()
    {
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 12 };
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        var schema = tool.Definition.Parameters;
        var maximum = schema.GetProperty("properties").GetProperty("maxTurns").GetProperty("maximum").GetInt32();
        maximum.ShouldBe(12);
    }

    [Fact]
    public void Definition_SurfacesConverseAllowListGuidance()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        tool.Definition.Description.ShouldContain("list_agents");
        tool.Definition.Description.ShouldContain("canConverse");

        var agentIdDescription = tool.Definition.Parameters
            .GetProperty("properties").GetProperty("agentId").GetProperty("description").GetString();
        agentIdDescription.ShouldContain("canConverse");
    }

    private static (Mock<IAgentExchangeService> Service, StrongBox<AgentExchangeRequest?> Captured) CreateCapturingService()
    {
        var captured = new StrongBox<AgentExchangeRequest?>(null);
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((request, _) => captured.Value = request)
            .ReturnsAsync(new AgentExchangeResult
            {
                SessionId = SessionId.From("nova::agent-agent::leela::abc123"),
                ConversationId = ConversationId.Create(),
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });
        return (service, captured);
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(item => item.Type == BotNexus.Agent.Core.Types.AgentToolContentType.Text).Value;
}
