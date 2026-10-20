using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Api;

public sealed class InterruptedTriggerRunTests
{
    [Theory]
    [InlineData("soul")]
    [InlineData("heartbeat")]
    [InlineData("memory")]
    public async Task CreateSessionAsync_InterruptedRun_PersistsPartialToolsAndRethrowsCancellation(string type)
    {
        var agentId = AgentId.From("agent-4101");
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        var registry = new Mock<IAgentRegistry>();
        using var caller = new CancellationTokenSource();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
            {
                caller.Cancel();
                throw new AgentPromptInterruptedException(new AgentResponse
                {
                    Content = "unfinished",
                    ToolCalls = [new("partial-4101", "read", true, "{}", null, IsIncomplete: true)]
                }, ct);
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        IInternalTrigger trigger = type switch
        {
            "soul" => new SoulTrigger(supervisor.Object, registry.Object, sessions, NullLogger<SoulTrigger>.Instance),
            "heartbeat" => new HeartbeatTrigger(supervisor.Object, registry.Object, conversations, sessions, NullLogger<HeartbeatTrigger>.Instance),
            "memory" => new MemoryTrigger(supervisor.Object, sessions, NullLogger<MemoryTrigger>.Instance),
            _ => throw new InvalidOperationException("Unexpected trigger")
        };

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => trigger.CreateSessionAsync(agentId, "work", caller.Token));

        exception.CancellationToken.ShouldBe(caller.Token);
        var summaries = await sessions.ListAsync(agentId);
        var session = summaries.ShouldHaveSingleItem();
        var history = session.GetHistorySnapshot();
        history.Where(row => row.Role == MessageRole.User).ShouldHaveSingleItem().Content.ShouldBe("work");
        history.Where(row => row.ToolCallId == "partial-4101").ShouldHaveSingleItem().ToolIsIncomplete.ShouldBeTrue();
        history.ShouldNotContain(row => row.Role == MessageRole.Assistant);
        session.Status.ShouldBe(type == "memory" ? SessionStatus.Sealed : SessionStatus.Active);
    }
}
