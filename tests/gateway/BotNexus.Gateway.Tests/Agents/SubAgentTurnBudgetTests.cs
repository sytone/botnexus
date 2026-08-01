using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Covers the sub-agent turn budget (#2656): <c>maxTurns</c> is clamped AND enforced, and
/// <see cref="SubAgentInfo.TurnsUsed"/> reports the same counter the enforcement uses.
/// The fake handle below drives turns deterministically — it emits turn notifications in a
/// tight loop and stops when its own cancellation token fires — so no test in this file
/// depends on elapsed wall-clock time (#2589).
/// </summary>
public sealed class SubAgentTurnBudgetTests
{
    /// <summary>AC1: the observed turn count stops at the budget rather than running unbounded.</summary>
    [Fact]
    public async Task RunSubAgent_TurnsExceedBudget_StopsAtBudget()
    {
        var handle = new TurnDrivingHandle(turnsToAttempt: 50);
        var manager = CreateManager(handle, out _);

        var result = await SpawnAndAwaitTerminalAsync(manager, maxTurns: 4);

        handle.ObservedTurns.ShouldBe(4);
        result.Status.ShouldBe(SubAgentStatus.BudgetExhausted);
    }

    /// <summary>AC2: budget exhaustion is a DIFFERENT disposition from a wall-clock timeout.</summary>
    [Fact]
    public async Task RunSubAgent_BudgetExhausted_DispositionDiffersFromTimeout()
    {
        var budgetHandle = new TurnDrivingHandle(turnsToAttempt: 50);
        var budgetManager = CreateManager(budgetHandle, out _);
        var budgetResult = await SpawnAndAwaitTerminalAsync(budgetManager, maxTurns: 2);

        var timeoutHandle = new TurnDrivingHandle(turnsToAttempt: 0, hangUntilCancelled: true);
        var timeoutManager = CreateManager(timeoutHandle, out _, timeoutSeconds: 1);
        var timeoutResult = await SpawnAndAwaitTerminalAsync(timeoutManager, maxTurns: 30, timeoutSeconds: 1);

        budgetResult.Status.ShouldBe(SubAgentStatus.BudgetExhausted);
        timeoutResult.Status.ShouldBe(SubAgentStatus.TimedOut);
        budgetResult.Status.ShouldNotBe(timeoutResult.Status);
        budgetResult.ResultSummary.ShouldNotBeNull();
        budgetResult.ResultSummary.ShouldContain("turn budget");
        timeoutResult.ResultSummary.ShouldNotBeNull();
        timeoutResult.ResultSummary.ShouldNotContain("turn budget");
    }

    /// <summary>AC3: a completed multi-turn run reports a non-zero TurnsUsed via ISubAgentManager.</summary>
    [Fact]
    public async Task RunSubAgent_CompletedMultiTurnRun_ReportsNonZeroTurnsUsed()
    {
        var handle = new TurnDrivingHandle(turnsToAttempt: 3);
        ISubAgentManager manager = CreateManager(handle, out _);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 30, timeoutSeconds: 30));
        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.TurnsUsed.ShouldBe(3);
        result.TurnsUsed.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// AC4 (single-counter proof): a run stopped by the budget reports TurnsUsed EQUAL to the
    /// budget. A separate display counter would let these two numbers disagree.
    /// </summary>
    [Fact]
    public async Task RunSubAgent_StoppedAtBudget_ReportsTurnsUsedEqualToBudget()
    {
        var handle = new TurnDrivingHandle(turnsToAttempt: 50);
        ISubAgentManager manager = CreateManager(handle, out _);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 5, timeoutSeconds: 30));
        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        result.Status.ShouldBe(SubAgentStatus.BudgetExhausted);
        result.TurnsUsed.ShouldBe(5);
    }

    /// <summary>AC5: the #1344 clamp is intact — an above-ceiling request is clamped and warns.</summary>
    [Fact]
    public async Task SpawnAsync_MaxTurnsAboveCeiling_StillClampedAndWarns()
    {
        var handle = new TurnDrivingHandle(turnsToAttempt: 50);
        var logger = new CapturingLogger();
        var manager = CreateManager(handle, out _, maxTurnsCeiling: 3, logger: logger);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 1_000_000, timeoutSeconds: 30));
        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        // Clamped to the ceiling, and the enforcement uses the clamped value.
        result.TurnsUsed.ShouldBe(3);
        result.Status.ShouldBe(SubAgentStatus.BudgetExhausted);
        logger.Warnings.ShouldContain(w => w.Contains("spawn budget clamped", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SubAgentInfo> SpawnAndAwaitTerminalAsync(
        ISubAgentManager manager,
        int maxTurns,
        int timeoutSeconds = 30)
    {
        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns, timeoutSeconds));
        return await AwaitTerminalAsync(manager, spawned.SubAgentId);
    }

    private static async Task<SubAgentInfo> AwaitTerminalAsync(ISubAgentManager manager, string subAgentId)
    {
        for (var i = 0; i < 1000; i++)
        {
            var current = await manager.GetAsync(subAgentId);
            if (current is { Status: not SubAgentStatus.Running })
                return current;
            await Task.Yield();
            await Task.Delay(10);
        }

        throw new TimeoutException("Sub-agent did not reach a terminal state.");
    }

    private static SubAgentSpawnRequest CreateRequest(int maxTurns, int timeoutSeconds)
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            MaxTurns = maxTurns,
            TimeoutSeconds = timeoutSeconds,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        };

    private static DefaultSubAgentManager CreateManager(
        IAgentHandle handle,
        out Mock<IChannelDispatcher> dispatcher,
        int timeoutSeconds = 30,
        int maxTurnsCeiling = 30,
        ILogger<DefaultSubAgentManager>? logger = null)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });

        dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = new GatewayOptions();
        options.SubAgents.MaxTurnsCeiling = maxTurnsCeiling;
        options.SubAgents.MaxTimeoutSeconds = timeoutSeconds;
        options.SubAgents.DefaultTimeoutSeconds = timeoutSeconds;

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            logger ?? NullLogger<DefaultSubAgentManager>.Instance);
    }

    /// <summary>
    /// A handle that drives a deterministic number of turns through the <c>ObserveTurns</c> seam.
    /// It fires turn notifications in a tight loop with no timing dependency and stops as soon as
    /// its prompt token is cancelled, which is exactly what the budget enforcement does.
    /// </summary>
    private sealed class TurnDrivingHandle(int turnsToAttempt, bool hangUntilCancelled = false) : IAgentHandle
    {
        private readonly List<Action> _observers = [];
        private int _observedTurns;

        /// <summary>The number of turns the loop actually completed before it was stopped.</summary>
        public int ObservedTurns => Volatile.Read(ref _observedTurns);

        public AgentId AgentId { get; } = AgentId.From("child-agent");
        public SessionId SessionId { get; } = SessionId.From("child-session");
        public bool IsRunning => false;

        public IDisposable? ObserveTurns(Action onTurnCompleted)
        {
            lock (_observers)
            {
                _observers.Add(onTurnCompleted);
            }

            return new NoopDisposable();
        }

        public async Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
        {
            if (hangUntilCancelled)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await cancelled.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var i = 0; i < turnsToAttempt; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Interlocked.Increment(ref _observedTurns);

                Action[] snapshot;
                lock (_observers)
                {
                    snapshot = [.. _observers];
                }

                foreach (var observer in snapshot)
                    observer();

                // Yield so the manager's cancellation is observable at the next loop check
                // without any dependency on elapsed time.
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new AgentResponse { Content = "Completed the work." };
        }

        public Task<AgentResponse> PromptAsync(
            BotNexus.Agent.Core.Types.UserMessage message,
            CancellationToken cancellationToken = default)
            => PromptAsync(message.Content, cancellationToken);

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            BotNexus.Agent.Core.Types.UserMessage message,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SteerAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FollowUpAsync(
            BotNexus.Agent.Core.Types.AgentMessage message,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>Captures warning-level log messages so the #1344 clamp warning can be asserted.</summary>
    private sealed class CapturingLogger : ILogger<DefaultSubAgentManager>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (Warnings)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
