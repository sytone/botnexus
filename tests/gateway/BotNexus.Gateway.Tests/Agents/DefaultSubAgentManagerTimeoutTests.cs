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
        // #2979: this is the only test in the file that needs the delegate to BEAT the deadline, so a
        // 1-second budget made correctness depend on winning a wall-clock race against a loaded CI
        // runner (observed: a synchronous delegate took 3s to be scheduled, and the run classified
        // TimedOut instead of Failed). The timeout is never intended to fire here, so a large budget
        // costs nothing and removes the race. The timeout-seeking tests keep the 1-second default.
        var (manager, dispatcher) = CreateManager(handle, timeoutSeconds: NonExpiringTimeoutSeconds);

        var result = await SpawnAndAwaitTerminalAsync(manager, timeoutSeconds: NonExpiringTimeoutSeconds);
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

    private static async Task<SubAgentInfo> SpawnAndAwaitTerminalAsync(
        DefaultSubAgentManager manager,
        int timeoutSeconds = 1)
    {
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = timeoutSeconds,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        // Allow for the sub-agent's own budget plus scheduling slack, so a deliberately long budget
        // is not cut short by the harness's own polling deadline.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds + 5);
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

    /// <summary>
    /// A sub-agent budget large enough that it can never plausibly expire during a test, used by the
    /// cases that assert a classification the delegate must reach BEFORE the deadline (#2979). Any
    /// value well above worst-case CI scheduling latency works; the point is that the timer is not
    /// part of what the test is measuring.
    /// </summary>
    private const int NonExpiringTimeoutSeconds = 30;

    private static (DefaultSubAgentManager Manager, Mock<IChannelDispatcher> Dispatcher) CreateManager(
        Mock<IAgentHandle> handle,
        int timeoutSeconds = 1)
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
        // MaxTimeoutSeconds clamps the request, so it must move with the requested budget -- otherwise a
        // deliberately long, non-expiring budget is silently clamped straight back down to the racy 1s.
        options.SubAgents.MaxTimeoutSeconds = timeoutSeconds;
        options.SubAgents.DefaultTimeoutSeconds = timeoutSeconds;

        return (new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<DefaultSubAgentManager>.Instance), dispatcher);
    }
}
