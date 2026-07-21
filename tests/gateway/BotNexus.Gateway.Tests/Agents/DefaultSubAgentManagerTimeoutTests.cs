using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class DefaultSubAgentManagerTimeoutTests
{
    [Fact]
    public async Task RunSubAgentAsync_PromptThrowsAfterTimeout_ReportsTimedOut()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var (manager, dispatcher) = CreateManager(handle);

        var result = await SpawnAndAwaitTerminalAsync(manager);

        await AssertTimedOutAsync(result, dispatcher);
    }

    [Fact]
    public async Task RunSubAgentAsync_PromptReturnsEmptyAfterTimeout_ReportsTimedOut()
    {
        var handle = CreateHandle(async token =>
        {
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancellationObserved.SetResult());
            await cancellationObserved.Task;
            return new AgentResponse { Content = string.Empty };
        });
        var (manager, dispatcher) = CreateManager(handle);

        var result = await SpawnAndAwaitTerminalAsync(manager);

        await AssertTimedOutAsync(result, dispatcher);
    }

    [Fact]
    public async Task RunSubAgentAsync_EmptyResponseBeforeTimeout_ReportsFailed()
    {
        var handle = CreateHandle(_ => Task.FromResult(new AgentResponse { Content = "  " }));
        var (manager, dispatcher) = CreateManager(handle);

        var result = await SpawnAndAwaitTerminalAsync(manager);
        await WaitForDiagnosticAsync(dispatcher);

        result.Status.ShouldBe(SubAgentStatus.Failed);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("empty final response");
        VerifyDiagnostic(dispatcher, "failed", "empty final response");
    }

    [Fact]
    public async Task RunSubAgentAsync_NonEmptyResponseBeforeTimeout_ReportsCompleted()
    {
        var handle = CreateHandle(_ => Task.FromResult(new AgentResponse { Content = "Implemented the fix." }));
        var (manager, dispatcher) = CreateManager(handle);

        var result = await SpawnAndAwaitTerminalAsync(manager);
        await WaitForDiagnosticAsync(dispatcher);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldBe("Implemented the fix.");
        VerifyDiagnostic(dispatcher, "completed", "Implemented the fix.");
    }

    [Fact]
    public async Task RunSubAgentAsync_TimeoutRacesWithEmptyPromptReturn_NeverReportsCompleted()
    {
        var handle = CreateHandle(async token =>
        {
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancellationObserved.SetResult());
            await cancellationObserved.Task;
            await Task.Yield();
            return new AgentResponse { Content = string.Empty };
        });
        var (manager, dispatcher) = CreateManager(handle);

        var result = await SpawnAndAwaitTerminalAsync(manager);

        await AssertTimedOutAsync(result, dispatcher);
        result.Status.ShouldNotBe(SubAgentStatus.Completed);
    }

    private static async Task<SubAgentInfo> SpawnAndAwaitTerminalAsync(DefaultSubAgentManager manager)
    {
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 1,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await manager.GetAsync(spawned.SubAgentId);
            if (current is { Status: not SubAgentStatus.Running })
                return current;
            await Task.Delay(20);
        }

        throw new TimeoutException("Sub-agent did not reach a terminal state.");
    }

    private static async Task AssertTimedOutAsync(
        SubAgentInfo result,
        Mock<IChannelDispatcher> dispatcher)
    {
        await WaitForDiagnosticAsync(dispatcher);
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("timed out after 1 second");
        VerifyDiagnostic(dispatcher, "timed out", "timed out after 1 second");
    }

    private static async Task WaitForDiagnosticAsync(Mock<IChannelDispatcher> dispatcher)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (dispatcher.Invocations.Any(invocation => invocation.Method.Name == nameof(IChannelDispatcher.DispatchAsync)))
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Sub-agent completion diagnostic was not dispatched.");
    }

    private static void VerifyDiagnostic(Mock<IChannelDispatcher> dispatcher, string status, string diagnostic)
    {
        dispatcher.Verify(d => d.DispatchAsync(
            It.Is<InboundMessage>(message =>
                message.Content.Contains(status, StringComparison.OrdinalIgnoreCase) &&
                message.Content.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IAgentHandle> CreateHandle(Func<CancellationToken, Task<AgentResponse>> prompt)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) => prompt(token));
        return handle;
    }

    private static (DefaultSubAgentManager Manager, Mock<IChannelDispatcher> Dispatcher) CreateManager(Mock<IAgentHandle> handle)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("parent-agent"))).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = new GatewayOptions();
        options.SubAgents.MaxTimeoutSeconds = 1;
        options.SubAgents.DefaultTimeoutSeconds = 1;

        return (new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<DefaultSubAgentManager>.Instance), dispatcher);
    }
}
