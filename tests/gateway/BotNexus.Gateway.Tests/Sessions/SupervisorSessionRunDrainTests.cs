using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
using AgentUserMessage = BotNexus.Gateway.Abstractions.Models.AgentUserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Issue #2903: the production fence. Proves the drain aborts and waits for the run bound to the
/// exact target session, leaves other sessions alone (AC3), and reports a timeout rather than
/// pretending it drained a run that is still live (AC2).
/// </summary>
public sealed class SupervisorSessionRunDrainTests
{
    private static readonly AgentId Agent = AgentId.From("agent-drain");
    private static readonly SessionId Target = SessionId.From("session-target");
    private static readonly SessionId Other = SessionId.From("session-other");

    [Fact]
    public async Task DrainAsync_WithNoSupervisorRegistered_ReportsNoActiveRun()
    {
        var drain = new SupervisorSessionRunDrain(new ServiceCollection().BuildServiceProvider());

        var outcome = await drain.DrainAsync(Target, TimeSpan.FromSeconds(1));

        outcome.ShouldBe(SessionDrainOutcome.NoActiveRun);
    }

    [Fact]
    public async Task DrainAsync_WithNoRunningHandle_ReportsNoActiveRun()
    {
        var handle = new FakeHandle(Target) { IsRunning = false };
        var drain = BuildDrain(handle);

        var outcome = await drain.DrainAsync(Target, TimeSpan.FromSeconds(1));

        outcome.ShouldBe(SessionDrainOutcome.NoActiveRun);
        handle.AbortCount.ShouldBe(0);
    }

    [Fact]
    public async Task DrainAsync_AbortsRunningHandle_AndReportsDrainedOnceItSettles()
    {
        var handle = new FakeHandle(Target) { IsRunning = true, StopOnAbort = true };
        var drain = BuildDrain(handle);

        var outcome = await drain.DrainAsync(Target, TimeSpan.FromSeconds(5));

        outcome.ShouldBe(SessionDrainOutcome.Drained);
        handle.AbortCount.ShouldBe(1);
        handle.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task DrainAsync_WhenRunNeverSettles_ReportsTimedOut()
    {
        var handle = new FakeHandle(Target) { IsRunning = true, StopOnAbort = false };
        var drain = BuildDrain(handle);

        var outcome = await drain.DrainAsync(Target, TimeSpan.FromMilliseconds(150));

        outcome.ShouldBe(SessionDrainOutcome.TimedOut);
        handle.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAsync_DoesNotAbortRunsOnUnrelatedSessionsOfTheSameAgent()
    {
        var target = new FakeHandle(Target) { IsRunning = true, StopOnAbort = true };
        var unrelated = new FakeHandle(Other) { IsRunning = true, StopOnAbort = true };
        var drain = BuildDrain(target, unrelated);

        var outcome = await drain.DrainAsync(Target, TimeSpan.FromSeconds(5));

        outcome.ShouldBe(SessionDrainOutcome.Drained);
        unrelated.AbortCount.ShouldBe(0);
        unrelated.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAsync_WhenAbortThrows_StillWaitsAndReportsTimeoutRatherThanClaimingSuccess()
    {
        var handle = new FakeHandle(Target) { IsRunning = true, StopOnAbort = false, ThrowOnAbort = true };
        var drain = BuildDrain(handle);

        var outcome = await drain.DrainAsync(Target, TimeSpan.FromMilliseconds(150));

        outcome.ShouldBe(SessionDrainOutcome.TimedOut);
    }

    private static SupervisorSessionRunDrain BuildDrain(params FakeHandle[] handles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentSupervisor>(new FakeSupervisor(handles));
        return new SupervisorSessionRunDrain(services.BuildServiceProvider());
    }

    private sealed class FakeSupervisor(IReadOnlyList<FakeHandle> handles) : IAgentSupervisor
    {
        public Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentHandle>(handles.First(h => h.SessionId.Equals(sessionId)));

        public Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public AgentInstance? GetInstance(AgentId agentId, SessionId sessionId)
            => handles.Any(h => h.SessionId.Equals(sessionId))
                ? Instance(agentId, sessionId)
                : null;

        public IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId)
            => handles.FirstOrDefault(h => h.SessionId.Equals(sessionId));

        public IReadOnlyList<AgentInstance> GetAllInstances()
            => [.. handles.Select(h => Instance(h.AgentId, h.SessionId))];

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static AgentInstance Instance(AgentId agentId, SessionId sessionId) => new()
        {
            InstanceId = $"{agentId.Value}:{sessionId.Value}",
            IsolationStrategy = "in-process",
            AgentId = agentId,
            SessionId = sessionId
        };
    }

    /// <summary>
    /// Minimal handle whose <c>IsRunning</c> the drain polls. <c>StopOnAbort</c> models a run that
    /// unwinds when aborted; leaving it false models the wedged run the timeout exists for.
    /// </summary>
    private sealed class FakeHandle(SessionId sessionId) : IAgentHandle
    {
        private int _abortCount;

        public AgentId AgentId => Agent;
        public SessionId SessionId => sessionId;
        public bool IsRunning { get; set; }
        public bool StopOnAbort { get; init; }
        public bool ThrowOnAbort { get; init; }
        public int AbortCount => Volatile.Read(ref _abortCount);

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _abortCount);
            if (ThrowOnAbort)
                throw new InvalidOperationException("abort failed");
            if (StopOnAbort)
                IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentResponse> PromptAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SteerAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FollowUpAsync(AgentTranscriptMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
