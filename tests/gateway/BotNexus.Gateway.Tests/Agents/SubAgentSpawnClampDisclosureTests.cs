using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Covers #2789: a spawn whose budget is clamped must DISCLOSE the clamp on the result the
/// caller actually reads, instead of recording it only in the gateway's own Warning log.
/// <para>
/// The disclosure must be a signal, not boilerplate - an in-range spawn carries none (AC3) -
/// and the disclosed effective values are asserted BY DERIVATION from what the run actually
/// received (AC4): the turn budget is read back out of the run's own <c>TurnsUsed</c> at
/// budget exhaustion, and the timeout out of the run's own timed-out diagnostic. Neither is a
/// restatement of a literal the test also supplied.
/// </para>
/// </summary>
public sealed class SubAgentSpawnClampDisclosureTests
{
    /// <summary>AC1: an above-ceiling maxTurns names requested, effective, and that a ceiling applied.</summary>
    [Fact]
    public async Task SpawnAsync_MaxTurnsAboveCeiling_DisclosesRequestedAndEffectiveTurns()
    {
        var manager = CreateManager(new TurnDrivingHandle(turnsToAttempt: 0), maxTurnsCeiling: 3);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 1_000, timeoutSeconds: 5));

        var clamp = spawned.BudgetClamp.ShouldNotBeNull();
        clamp.MaxTurnsClamped.ShouldBeTrue();
        clamp.RequestedMaxTurns.ShouldBe(1_000);
        clamp.EffectiveMaxTurns.ShouldBe(3);
        clamp.PolicyTier.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>AC1 (tool surface): the clamp reaches the JSON the calling model actually reads.</summary>
    [Fact]
    public async Task SpawnTool_MaxTurnsAboveCeiling_ToolResultDisclosesTurnClamp()
    {
        var manager = CreateManager(new TurnDrivingHandle(turnsToAttempt: 0), maxTurnsCeiling: 3);

        using var payload = await ExecuteSpawnToolAsync(manager, maxTurns: 1_000, timeoutSeconds: 5);

        var clamp = payload.RootElement.GetProperty("budgetClamp");
        clamp.GetProperty("maxTurnsClamped").GetBoolean().ShouldBeTrue();
        clamp.GetProperty("requestedMaxTurns").GetInt32().ShouldBe(1_000);
        clamp.GetProperty("effectiveMaxTurns").GetInt32().ShouldBe(3);
    }

    /// <summary>AC2: the same holds independently for timeoutSeconds.</summary>
    [Fact]
    public async Task SpawnAsync_TimeoutAboveCeiling_DisclosesRequestedAndEffectiveTimeout()
    {
        var manager = CreateManager(new TurnDrivingHandle(turnsToAttempt: 0), maxTimeoutSeconds: 2);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 2, timeoutSeconds: 9_999));

        var clamp = spawned.BudgetClamp.ShouldNotBeNull();
        clamp.TimeoutSecondsClamped.ShouldBeTrue();
        clamp.RequestedTimeoutSeconds.ShouldBe(9_999);
        clamp.EffectiveTimeoutSeconds.ShouldBe(2);
    }

    /// <summary>AC2 (tool surface): the timeout clamp reaches the JSON the calling model reads.</summary>
    [Fact]
    public async Task SpawnTool_TimeoutAboveCeiling_ToolResultDisclosesTimeoutClamp()
    {
        var manager = CreateManager(new TurnDrivingHandle(turnsToAttempt: 0), maxTimeoutSeconds: 2);

        using var payload = await ExecuteSpawnToolAsync(manager, maxTurns: 2, timeoutSeconds: 9_999);

        var clamp = payload.RootElement.GetProperty("budgetClamp");
        clamp.GetProperty("timeoutSecondsClamped").GetBoolean().ShouldBeTrue();
        clamp.GetProperty("requestedTimeoutSeconds").GetInt32().ShouldBe(9_999);
        clamp.GetProperty("effectiveTimeoutSeconds").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// AC3 (the load-bearing negative): a spawn inside both ceilings carries NO disclosure at
    /// all, so a disclosure is a signal rather than boilerplate the caller learns to skip.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_WithinBothCeilings_HasNoClampDisclosure()
    {
        var manager = CreateManager(new TurnDrivingHandle(turnsToAttempt: 0), maxTurnsCeiling: 30, maxTimeoutSeconds: 600);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 5, timeoutSeconds: 60));

        spawned.BudgetClamp.ShouldBeNull();

        using var payload = await ExecuteSpawnToolAsync(manager, maxTurns: 5, timeoutSeconds: 60);
        payload.RootElement.TryGetProperty("budgetClamp", out _).ShouldBeFalse();
    }

    /// <summary>
    /// AC4 (turns, by derivation): the DISCLOSED effective turn budget equals the budget the run
    /// was actually given. Read back from the run itself - a budget-exhausted run reports
    /// <c>TurnsUsed</c> equal to the bound <c>RunSubAgentAsync</c> enforced - and compared to the
    /// disclosure. No literal appears on both sides of the assertion.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_ClampedTurns_DisclosedEffectiveEqualsBudgetTheRunEnforced()
    {
        var manager = CreateManager(new TurnDrivingHandle(turnsToAttempt: 500), maxTurnsCeiling: 4);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 1_000, timeoutSeconds: 30));
        var terminal = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        terminal.Status.ShouldBe(SubAgentStatus.BudgetExhausted);

        var disclosed = spawned.BudgetClamp.ShouldNotBeNull().EffectiveMaxTurns;
        var enforcedByTheRun = terminal.TurnsUsed;
        disclosed.ShouldBe(enforcedByTheRun);
    }

    /// <summary>
    /// AC4 (timeout, by derivation): the DISCLOSED effective timeout equals the deadline the run
    /// was actually given, recovered from the run's own timed-out diagnostic rather than restated.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_ClampedTimeout_DisclosedEffectiveEqualsDeadlineTheRunUsed()
    {
        var manager = CreateManager(
            new TurnDrivingHandle(turnsToAttempt: 0, hangUntilCancelled: true),
            maxTurnsCeiling: 30,
            maxTimeoutSeconds: 1);

        var spawned = await manager.SpawnAsync(CreateRequest(maxTurns: 5, timeoutSeconds: 9_999));
        var terminal = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        terminal.Status.ShouldBe(SubAgentStatus.TimedOut);

        var summary = terminal.ResultSummary.ShouldNotBeNull();
        var match = Regex.Match(summary, @"(\d+)");
        match.Success.ShouldBeTrue($"Timed-out summary did not carry the deadline: '{summary}'.");
        var deadlineTheRunUsed = int.Parse(match.Groups[1].Value);

        var disclosed = spawned.BudgetClamp.ShouldNotBeNull().EffectiveTimeoutSeconds;
        disclosed.ShouldBe(deadlineTheRunUsed);
    }

    private static async Task<JsonDocument> ExecuteSpawnToolAsync(
        ISubAgentManager manager,
        int maxTurns,
        int timeoutSeconds)
    {
        var tool = new SubAgentSpawnTool(
            manager,
            AgentId.From("parent-agent"),
            SessionId.From("parent-session"),
            ConversationId.From("inherited-conversation"));

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?>
            {
                ["task"] = "Do background work",
                ["maxTurns"] = maxTurns,
                ["timeoutSeconds"] = timeoutSeconds
            });

        var text = result.Content[0].Value;
        text.ShouldNotBeNullOrWhiteSpace();
        return JsonDocument.Parse(text);
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
        int maxTurnsCeiling = 30,
        int maxTimeoutSeconds = 1800)
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

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = new GatewayOptions();
        options.SubAgents.MaxDepth = 8;
        options.SubAgents.MaxTurnsCeiling = maxTurnsCeiling;
        options.SubAgents.MaxTimeoutSeconds = maxTimeoutSeconds;
        options.SubAgents.DefaultTimeoutSeconds = Math.Min(600, maxTimeoutSeconds);

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    /// <summary>
    /// Drives a deterministic number of turns through the <c>ObserveTurns</c> seam so the budget
    /// bound the run enforced is observable without any wall-clock dependency (#2589).
    /// </summary>
    private sealed class TurnDrivingHandle(int turnsToAttempt, bool hangUntilCancelled = false) : IAgentHandle
    {
        private readonly List<Action> _observers = [];

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

                Action[] snapshot;
                lock (_observers)
                {
                    snapshot = [.. _observers];
                }

                foreach (var observer in snapshot)
                    observer();

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
}
