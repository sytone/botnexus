using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// #2633: a sub-agent spawn that fails because the descriptor names an unregistered model must
/// (a) be observed rather than escaping to the finalizer as an UnobservedTaskException,
/// (b) come back to the requesting agent as a tool error naming the model and the provider, and
/// (c) leave the supervisor able to serve the next spawn.
/// </summary>
public sealed class SubAgentSpawnFaultObservationTests
{
    private const string UnregisteredModelMessage =
        "Model 'gpt-4.1' for provider 'github-copilot-messages' is not registered.";

    [Fact]
    public async Task GetOrCreateAsync_WhenCreationFails_DoesNotLeakUnobservedTaskException()
    {
        // A unique marker so a concurrently running test's unobserved fault cannot be mistaken
        // for ours - this suite asserts on OUR exception only.
        var marker = $"spawn-fault-2633-{Guid.NewGuid():N}";
        var observed = new List<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var text = e.Exception?.ToString() ?? string.Empty;
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                lock (observed) observed.Add(text);
                e.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // Scoped so the supervisor and its pending-create TaskCompletionSource become
            // collectable, which is what triggers the finalizer-thread rethrow when the faulted
            // task was never observed.
            await RunFailingCreateAsync(marker);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }

            lock (observed)
            {
                observed.ShouldBeEmpty(
                    "the faulted agent-creation task must be observed by the supervisor, not rethrown on the finalizer thread");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task RunFailingCreateAsync(string marker)
    {
        var supervisor = CreateSupervisor(_ => throw new InvalidOperationException($"{UnregisteredModelMessage} [{marker}]"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => supervisor.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-1")));
    }

    [Fact]
    public async Task GetOrCreateAsync_AfterFailedCreate_ServesASubsequentSuccessfulSpawn()
    {
        var fail = true;
        var handle = CreateHandleMock("agent-a", "session-2");
        var supervisor = CreateSupervisor(_ =>
        {
            if (fail)
                throw new InvalidOperationException(UnregisteredModelMessage);
            return handle.Object;
        });

        await Should.ThrowAsync<InvalidOperationException>(
            () => supervisor.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-1")));

        // The observable that matters: the gateway keeps serving.
        fail = false;
        var second = await supervisor.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-2"));

        second.ShouldBeSameAs(handle.Object);
    }

    [Fact]
    public async Task SpawnTool_WhenModelIsNotRegistered_ReturnsToolErrorNamingModelAndProvider()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(UnregisteredModelMessage));
        var tool = new SubAgentSpawnTool(
            manager.Object,
            AgentId.From("parent-agent"),
            SessionId.From("parent-session"),
            ConversationId.From("conv-1"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" });

        var text = string.Concat(result.Content
            .Where(c => c.Type == AgentToolContentType.Text)
            .Select(c => c.Value));

        // Anti-vacuous: the message must name BOTH the model and the provider, not merely fail.
        text.ShouldContain("gpt-4.1");
        text.ShouldContain("github-copilot-messages");
        text.ShouldContain("error");
    }

    [Fact]
    public async Task SpawnTool_WhenSpawnFails_DoesNotPropagateTheException()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(UnregisteredModelMessage));
        var tool = new SubAgentSpawnTool(
            manager.Object,
            AgentId.From("parent-agent"),
            SessionId.From("parent-session"),
            ConversationId.From("conv-1"));

        // The spawn failure is surfaced as a tool result, not thrown at the executor boundary.
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" });

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task SpawnTool_WhenCancelled_StillPropagatesCancellation()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var tool = new SubAgentSpawnTool(
            manager.Object,
            AgentId.From("parent-agent"),
            SessionId.From("parent-session"),
            ConversationId.From("conv-1"));

        // Cancellation is turn control flow, not a tool error - it must not be swallowed.
        await Should.ThrowAsync<OperationCanceledException>(
            () => tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" }));
    }

    private static DefaultAgentSupervisor CreateSupervisor(Func<AgentExecutionContext, IAgentHandle> create)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "github-copilot-messages",
            IsolationStrategy = "test"
        });

        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDescriptor _, AgentExecutionContext context, CancellationToken _) => Task.FromResult(create(context)));

        return new DefaultAgentSupervisor(
            registry,
            [strategy.Object],
            Mock.Of<ISessionStore>(),
            NullLogger<DefaultAgentSupervisor>.Instance);
    }

    private static Mock<IAgentHandle> CreateHandleMock(string agentId, string sessionId)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(agentId));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(sessionId));
        return handle;
    }
}
