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

/// <summary>
/// Covers the spawn-only handoff classification (#2725): a run that accepted a sub-agent spawn,
/// produced zero delivery payloads and emitted no synthesized text is a <b>handoff</b>, not an
/// empty-response failure, and it delivers the descendant's result.
/// <para>
/// The load-bearing test in this file is
/// <see cref="EmptyRun_WithoutAcceptedSpawn_StillRecordsEmptyResponseDiagnostic"/> - the
/// DISCRIMINATION. Without it, a change that simply stopped reporting empty responses would pass
/// every other test here while being a suppression rather than a fix.
/// </para>
/// </summary>
public sealed class SubAgentSpawnOnlyHandoffTests
{
    private const string EmptyResponseDiagnostic =
        "Sub-agent failed because it returned an empty final response.";

    /// <summary>
    /// AC1: a run that spawns exactly one descendant and emits no text of its own is NOT recorded
    /// with the empty-response diagnostic.
    /// </summary>
    [Fact]
    public async Task SpawnOnlyRun_WithSilentParent_DoesNotRecordEmptyResponseDiagnostic()
    {
        var (manager, spawned) = await RunSpawnOnlyAsync(descendantResult: "child work product");

        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        result.ResultSummary.ShouldNotBe(EmptyResponseDiagnostic);
        result.Status.ShouldNotBe(SubAgentStatus.Failed);
    }

    /// <summary>
    /// AC2: the descendant's final response is what gets delivered, and it is dispatched exactly
    /// once (no double-announce from both the handoff path and the ordinary completion path).
    /// </summary>
    [Fact]
    public async Task SpawnOnlyRun_DeliversDescendantResult_ExactlyOnce()
    {
        var (manager, spawned, dispatcher) =
            await RunSpawnOnlyWithDispatcherAsync(descendantResult: "child work product");

        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary!.ShouldContain("child work product");

        dispatcher.Verify(
            d => d.DispatchAsync(
                It.Is<InboundMessage>(m =>
                    m.SenderId == $"subagent:{spawned.SubAgentId}" &&
                    m.Content.Contains("child work product", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// AC3: the handoff status is a success state distinguishable from BOTH a normal text run
    /// (<see cref="SubAgentStatus.Completed"/>) and a genuine empty response
    /// (<see cref="SubAgentStatus.Failed"/>).
    /// </summary>
    [Fact]
    public async Task SpawnOnlyRun_RecordsHandedOffStatus_DistinctFromCompletedAndFailed()
    {
        var (handoffManager, handoffSpawn) = await RunSpawnOnlyAsync(descendantResult: "child work product");
        var handoff = await AwaitTerminalAsync(handoffManager, handoffSpawn.SubAgentId);

        var (textManager, textSpawn) = await RunPlainAsync(parentResult: "parent said something");
        var text = await AwaitTerminalAsync(textManager, textSpawn.SubAgentId);

        var (emptyManager, emptySpawn) = await RunPlainAsync(parentResult: "   ");
        var empty = await AwaitTerminalAsync(emptyManager, emptySpawn.SubAgentId);

        handoff.Status.ShouldBe(SubAgentStatus.HandedOff);
        text.Status.ShouldBe(SubAgentStatus.Completed);
        empty.Status.ShouldBe(SubAgentStatus.Failed);

        handoff.Status.ShouldNotBe(text.Status);
        handoff.Status.ShouldNotBe(empty.Status);

        SubAgentStatusPolicy.IsUnsuccessfulTermination(handoff.Status).ShouldBeFalse(
            "A handoff succeeded - the work was delegated and the descendant's result was "
            + "delivered. Classifying it as a fault re-reddens every alert keyed on run status.");
    }

    /// <summary>
    /// AC4 - THE DISCRIMINATION. A run that produced neither text NOR an accepted spawn must
    /// STILL record the empty-response diagnostic. This is the test that separates a fix from a
    /// suppression: any change that makes every silent run "successful" fails here by name.
    /// </summary>
    [Fact]
    public async Task EmptyRun_WithoutAcceptedSpawn_StillRecordsEmptyResponseDiagnostic()
    {
        var (manager, spawned) = await RunPlainAsync(parentResult: "   ");

        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        result.Status.ShouldBe(SubAgentStatus.Failed);
        result.ResultSummary.ShouldBe(EmptyResponseDiagnostic);
    }

    /// <summary>
    /// AC5: a spawn-only parent whose descendant FAILS records the failure, not a spurious
    /// success. The handoff classification must not launder a failed delegation.
    /// </summary>
    [Fact]
    public async Task SpawnOnlyRun_WithFailingDescendant_RecordsFailure()
    {
        var (manager, spawned) = await RunSpawnOnlyAsync(descendantResult: null);

        var result = await AwaitTerminalAsync(manager, spawned.SubAgentId);

        result.Status.ShouldBe(SubAgentStatus.Failed);
        SubAgentStatusPolicy.IsUnsuccessfulTermination(result.Status).ShouldBeTrue();
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary!.ShouldContain("empty final response");
    }

    private static async Task<(DefaultSubAgentManager Manager, SubAgentInfo Spawned)> RunSpawnOnlyAsync(
        string? descendantResult)
    {
        var (manager, spawned, _) = await RunSpawnOnlyWithDispatcherAsync(descendantResult);
        return (manager, spawned);
    }

    private static async Task<(DefaultSubAgentManager Manager, SubAgentInfo Spawned, Mock<IChannelDispatcher> Dispatcher)>
        RunSpawnOnlyWithDispatcherAsync(string? descendantResult)
    {
        var handleFactory = new SpawningHandleFactory(descendantResult);
        var manager = CreateManager(handleFactory, out var dispatcher);
        handleFactory.Manager = manager;

        var spawned = await manager.SpawnAsync(CreateRequest(SessionId.From("parent-session")));
        return (manager, spawned, dispatcher);
    }

    private static async Task<(DefaultSubAgentManager Manager, SubAgentInfo Spawned)> RunPlainAsync(
        string parentResult)
    {
        var handleFactory = new SpawningHandleFactory(
            descendantResult: null,
            spawnDescendant: false,
            ownResult: parentResult);
        var manager = CreateManager(handleFactory, out _);
        handleFactory.Manager = manager;

        var spawned = await manager.SpawnAsync(CreateRequest(SessionId.From("parent-session")));
        return (manager, spawned);
    }

    private static async Task<SubAgentInfo> AwaitTerminalAsync(ISubAgentManager manager, string subAgentId)
    {
        for (var i = 0; i < 1000; i++)
        {
            var current = await manager.GetAsync(subAgentId);
            if (current is not null && SubAgentStatusPolicy.IsTerminal(current.Status))
                return current;
            await Task.Yield();
            await Task.Delay(10);
        }

        throw new TimeoutException("Sub-agent did not reach a terminal state.");
    }

    private static SubAgentSpawnRequest CreateRequest(SessionId parentSessionId)
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = parentSessionId,
            Task = "Delegate the work",
            MaxTurns = 30,
            TimeoutSeconds = 30,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        };

    private static DefaultSubAgentManager CreateManager(
        SpawningHandleFactory handleFactory,
        out Mock<IChannelDispatcher> dispatcher)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentId _, SessionId sessionId, CancellationToken _) => handleFactory.Create(sessionId));
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
        options.SubAgents.MaxTurnsCeiling = 30;
        options.SubAgents.MaxTimeoutSeconds = 30;
        options.SubAgents.DefaultTimeoutSeconds = 30;
        options.SubAgents.MaxConcurrentPerSession = 5;
        // The fixture models a parent sub-agent that itself delegates, so the descendant sits at
        // depth 2. SubAgentOptions.MaxDepth defaults to 1, which refuses that spawn with a depth
        // diagnostic BEFORE the silent-run classification under test is ever reached - the whole
        // scenario would be exercised vacuously.
        options.SubAgents.MaxDepth = 2;

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    /// <summary>
    /// Produces handles bound to the session they were created for. The FIRST handle (the run
    /// under test) optionally spawns exactly one descendant against its own child session - the
    /// real production seam through which a spawn is "accepted" - and then returns no text at
    /// all, which is precisely the silent-parent shape #2725 describes.
    /// </summary>
    private sealed class SpawningHandleFactory(
        string? descendantResult,
        bool spawnDescendant = true,
        string ownResult = "")
    {
        private int _created;

        public DefaultSubAgentManager? Manager { get; set; }

        public IAgentHandle Create(SessionId sessionId)
        {
            var ordinal = Interlocked.Increment(ref _created);
            return ordinal == 1
                ? new ScriptedHandle(sessionId, async () =>
                {
                    if (spawnDescendant)
                    {
                        _ = await Manager!.SpawnAsync(new SubAgentSpawnRequest
                        {
                            ParentAgentId = AgentId.From("parent-agent"),
                            ParentSessionId = sessionId,
                            Task = "Do the actual work",
                            MaxTurns = 30,
                            TimeoutSeconds = 30,
                            Mode = new Embody(SubAgentArchetype.General),
                            InheritedConversationId = ConversationId.From("inherited-conversation")
                        });
                    }

                    return ownResult;
                })
                : new ScriptedHandle(sessionId, () => Task.FromResult(descendantResult ?? string.Empty));
        }
    }

    private sealed class ScriptedHandle(SessionId sessionId, Func<Task<string>> script) : IAgentHandle
    {
        public AgentId AgentId { get; } = AgentId.From("child-agent");

        public SessionId SessionId { get; } = sessionId;

        public bool IsRunning => false;

        public IDisposable? ObserveTurns(Action onTurnCompleted) => null;

        public async Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => new() { Content = await script() };

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
    }
}
