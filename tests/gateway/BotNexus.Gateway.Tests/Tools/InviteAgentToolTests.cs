using System.Text.Json;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class InviteAgentToolTests
{
    private static AgentDescriptor MakeDescriptor(string id) => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = id,
        ModelId = "test-model",
        ApiProvider = "test-provider"
    };

    private static AgentExchangeResult MakeResult(string response = "Acknowledged") => new()
    {
        SessionId = "target::agent-agent::initiator::abc123",
        Status = "sealed",
        Turns = 1,
        FinalResponse = response,
        Transcript = []
    };

    private static IReadOnlyDictionary<string, object?> Args(string agentId, string context, string? role = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["agentId"] = agentId,
            ["context"] = context
        };
        if (role is not null)
            dict["role"] = role;
        return dict;
    }

    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new InviteAgentTool(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IAgentExchangeService>(),
            new InMemorySessionStore(),
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        tool.Name.ShouldBe("invite_agent");
        tool.Label.ShouldBe("Invite Agent");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenAgentIdMissing_Throws()
    {
        var tool = new InviteAgentTool(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IAgentExchangeService>(),
            new InMemorySessionStore(),
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["context"] = "hello" });
        await action.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenContextMissing_Throws()
    {
        var tool = new InviteAgentTool(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IAgentExchangeService>(),
            new InMemorySessionStore(),
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["agentId"] = "agent-b" });
        await action.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetAgentNotFound_ReturnsError()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-1", "host-agent");

        var tool = new InviteAgentTool(
            registry.Object,
            Mock.Of<IAgentExchangeService>(),
            store,
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        var result = await tool.ExecuteAsync("call-1", Args("unknown-agent", "help me"));

        var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("error").GetString()!.ShouldContain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_WhenInvitingSelf_ReturnsError()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns(MakeDescriptor("host-agent"));

        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-1", "host-agent");

        var tool = new InviteAgentTool(
            registry.Object,
            Mock.Of<IAgentExchangeService>(),
            store,
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        var result = await tool.ExecuteAsync("call-1", Args("host-agent", "briefing"));
        var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("error").GetString()!.ShouldContain("cannot invite itself");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentAlreadyParticipant_ReturnsError()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("agent-b")))
            .Returns(MakeDescriptor("agent-b"));

        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("session-1", "host-agent");
        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.Agent,
            Id = "agent-b"
        });
        await store.SaveAsync(session);

        var tool = new InviteAgentTool(
            registry.Object,
            Mock.Of<IAgentExchangeService>(),
            store,
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        var result = await tool.ExecuteAsync("call-1", Args("agent-b", "briefing"));
        var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("error").GetString()!.ShouldContain("already a participant");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAtMaxDepth_ReturnsError()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("agent-new")))
            .Returns(MakeDescriptor("agent-new"));

        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("session-1", "host-agent");
        for (var i = 0; i < 5; i++)
        {
            session.Participants.Add(new SessionParticipant
            {
                Type = ParticipantType.Agent,
                Id = $"agent-{i}"
            });
        }
        await store.SaveAsync(session);

        var tool = new InviteAgentTool(
            registry.Object,
            Mock.Of<IAgentExchangeService>(),
            store,
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        var result = await tool.ExecuteAsync("call-1", Args("agent-new", "briefing"));
        var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("error").GetString()!.ShouldContain("Maximum multi-agent depth");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_AddsParticipantAndUpgradesSessionType()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("agent-b")))
            .Returns(MakeDescriptor("agent-b"));

        var exchange = new Mock<IAgentExchangeService>();
        exchange.Setup(e => e.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("Ready to collaborate."));

        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-1", "host-agent");

        var tool = new InviteAgentTool(
            registry.Object,
            exchange.Object,
            store,
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        var result = await tool.ExecuteAsync("call-1", Args("agent-b", "Please review the plan.", "reviewer"));

        var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("status").GetString().ShouldBe("invited");
        json.RootElement.GetProperty("agentId").GetString().ShouldBe("agent-b");
        json.RootElement.GetProperty("role").GetString().ShouldBe("reviewer");
        json.RootElement.GetProperty("sessionType").GetString().ShouldBe("multi-agent");
        json.RootElement.GetProperty("participantCount").GetInt32().ShouldBe(1);

        // Verify session was persisted with MultiAgent type and participant added
        var updatedSession = await store.GetAsync("session-1");
        updatedSession.ShouldNotBeNull();
        updatedSession!.SessionType.ShouldBe(SessionType.MultiAgent);
        updatedSession.Participants.ShouldContain(p => p.Id == "agent-b" && p.Role == "reviewer");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_SendsBriefingWithRoleInContext()
    {
        AgentExchangeRequest? capturedRequest = null;
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("agent-b")))
            .Returns(MakeDescriptor("agent-b"));

        var exchange = new Mock<IAgentExchangeService>();
        exchange.Setup(e => e.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(MakeResult());

        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("session-1", "host-agent");

        var tool = new InviteAgentTool(
            registry.Object,
            exchange.Object,
            store,
            AgentId.From("host-agent"),
            SessionId.From("session-1"));

        await tool.ExecuteAsync("call-1", Args("agent-b", "This is the briefing context.", "specialist"));

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.TargetId.Value.ShouldBe("agent-b");
        capturedRequest.Message.ShouldContain("host-agent");
        capturedRequest.Message.ShouldContain("specialist");
        capturedRequest.Message.ShouldContain("This is the briefing context.");
    }
}
