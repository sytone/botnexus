using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Agents;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

// Shares the existing fake-handle factory, virtual clock, and public terminal join boundary.
public sealed partial class DefaultSubAgentManagerTimeoutTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task RunSubAgentAsync_RepeatedFailingActivity_FinalizesAtConfiguredTurnWindow(int window)
    {
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray());
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, window));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        run.ExplorationTurns.ShouldBe(window);
        run.FinalizationCalls.ShouldBe(1);
        result.TurnsUsed.ShouldBe(window + 1);
        run.ExplorationToken.IsCancellationRequested.ShouldBeTrue();
        run.FinalizationToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(5, true)]
    public async Task RunSubAgentAsync_FailureRepetitionsSeparatedByIdleTurns_UsesBoundedWindow(int window, bool shouldStop)
    {
        var run = CreateActivityRun([FailedTurn(), [], FailedTurn(), [], FailedTurn()]);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options =>
            {
                EnableNoProgress(options, window);
                options.NoProgressRepeatedFailureThreshold = 3;
            });

        var result = await JoinActivityRunAsync(manager);

        if (shouldStop)
        {
            AssertNoProgress(result);
            run.FinalizationCalls.ShouldBe(1);
        }
        else
        {
            result.Status.ShouldBe(SubAgentStatus.Completed);
            run.FinalizationCalls.ShouldBe(0);
        }
        run.ExplorationTurns.ShouldBe(5);
    }

    [Fact]
    public async Task RunSubAgentAsync_NoProgressDisabledByDefault_DoesNotStopRepeatedFailures()
    {
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray());
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(), timeoutSecondsBudget: 300);

        var result = await JoinActivityRunAsync(manager);

        new SubAgentOptions().NoProgressEnabled.ShouldBeFalse();
        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.PartialResult.ShouldBeNull();
        run.ExplorationTurns.ShouldBe(10);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunSubAgentAsync_NewSuccessfulReadOrResearchEvidence_ResetsFailingWindow(bool research)
    {
        // Tool names are deliberately absent at this boundary: read/research successes have the
        // same semantics as all other completed tools. Their new result identity is the evidence.
        var evidence = research ? 200UL : 100UL;
        var run = CreateActivityRun([FailedTurn(), FailedTurn(),
            [Tool(AgentActivityKind.ToolSucceeded, evidence)], FailedTurn(), FailedTurn()]);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.PartialResult.ShouldBeNull();
        run.ExplorationTurns.ShouldBe(5);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunSubAgentAsync_NewEvidenceThenFreshFailureWindow_FinalizesOnlyAfterResetWindow()
    {
        var run = CreateActivityRun([FailedTurn(), FailedTurn(),
            [Tool(AgentActivityKind.ToolSucceeded, 100)], FailedTurn(), FailedTurn(), FailedTurn(), FailedTurn()]);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        run.ExplorationTurns.ShouldBe(6);
        run.FinalizationCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RunSubAgentAsync_RepeatedIdenticalSuccessfulOutput_DoesNotResetFailingWindow()
    {
        var staleEvidence = Tool(AgentActivityKind.ToolSucceeded, 100);
        var run = CreateActivityRun([[staleEvidence],
            [.. FailedTurn(), staleEvidence], [.. FailedTurn(), staleEvidence], [.. FailedTurn(), staleEvidence],
            FailedTurn(), FailedTurn()]);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        run.ExplorationTurns.ShouldBe(4);
        run.FinalizationCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RunSubAgentAsync_DistinctSuccessfulEvidenceEveryTurn_DoesNotStopProductiveResearch()
    {
        var turns = Enumerable.Range(1, 10)
            .Select(index => new[] { Tool(AgentActivityKind.ToolSucceeded, (ulong)index) }).ToArray();
        var run = CreateActivityRun(turns);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        run.ExplorationTurns.ShouldBe(10);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunSubAgentAsync_DifferentFailureIdentities_DoesNotInferAnIdenticalLoop()
    {
        var turns = Enumerable.Range(1, 10)
            .Select(index => new[] { Tool(AgentActivityKind.ToolFailed, (ulong)index) }).ToArray();
        var run = CreateActivityRun(turns);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunSubAgentAsync_RepeatingMultiToolFailureSequence_FinalizesWithoutExposingToolPayloads()
    {
        var sequence = new[] { Tool(AgentActivityKind.ToolFailed, 1), Tool(AgentActivityKind.ToolFailed, 2) };
        var run = CreateActivityRun(Enumerable.Repeat(sequence, 10).ToArray());
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        run.ExplorationTurns.ShouldBe(3);
        run.FinalizationCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RunSubAgentAsync_ManyIdenticalFailuresWithinOneTurn_DoesNotCountCallbacksAsTurns()
    {
        var run = CreateActivityRun([Enumerable.Repeat(Tool(AgentActivityKind.ToolFailed, 1), 20).ToArray()]);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.TurnsUsed.ShouldBe(1);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunSubAgentAsync_RepeatedProseWithoutToolEvidence_DoesNotInferNoProgress()
    {
        var run = CreateActivityRun(Enumerable.Range(0, 10).Select(_ => Array.Empty<AgentActivityObservation>()).ToArray(),
            explorationText: "Still blocked. Retrying the same error. Still blocked.");
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunSubAgentAsync_NoActivityVisibility_DoesNotInventNoProgress()
    {
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(), exposeActivity: false);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunSubAgentAsync_NoProgressFinalizerEmptyOrThrows_PreservesPartialStopReason(bool throws)
    {
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(), finalText: string.Empty,
            finalizerThrows: throws);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        run.FinalizationCalls.ShouldBe(1);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.Summary.ShouldBe("Exploration evidence remains unverified.");
    }

    [Fact]
    public async Task RunSubAgentAsync_NoProgressFinalizerClaimsSuccess_RemainsPartialWithSanitizedDiagnostic()
    {
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(),
            finalText: "Everything succeeded and was validated.");
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.SummaryIsVerified.ShouldBeFalse();
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldNotContain("secret-argument");
        result.ResultSummary.ShouldNotContain("private-error-payload");
        result.ResultSummary.Length.ShouldBeLessThanOrEqualTo(2048);
        run.ActivitySubscriptionDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task RunSubAgentAsync_DeadlineWinsDuringNoProgressFinalization_ReportsTimeoutNotSuccess()
    {
        var time = new ControllableTimeProvider();
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(),
            onFinalization: () => time.Advance(TimeSpan.FromSeconds(300)));
        var (manager, _, _) = CreateManager(run.Handle, time,
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        run.FinalizationCalls.ShouldBe(1);
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.StopReason.ShouldBe(SubAgentStopReason.Timeout);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldNotContain("Everything succeeded");
    }

    [Fact]
    public async Task RunSubAgentAsync_KillWinsBeforeLateActivity_CannotReclassifyKilledRun()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(),
            beforeExploration: async () => { started.TrySetResult(); await release.Task; },
            ignoreCancellation: true, onExplorationDrained: () => drained.TrySetResult());
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));
        var spawned = await manager.SpawnAsync(ActivityRequest());
        try
        {
            await started.Task.WaitAsync(HangGuard);
            (await manager.KillAsync(spawned.SubAgentId, spawned.ParentSessionId)).ShouldBeTrue();
        }
        finally
        {
            release.TrySetResult();
        }
        await drained.Task.WaitAsync(HangGuard);
        var result = await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);

        result.Status.ShouldBe(SubAgentStatus.Killed);
        result.PartialResult.ShouldBeNull();
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public void AgentActivityObservation_ContractContainsOnlyFixedSizeSanitizedData()
    {
        // No result text, arguments, model narration, paths, or unbounded ID collections can
        // cross the observation seam. Producers hash normalized tool/result evidence internally.
        var properties = typeof(AgentActivityObservation).GetProperties();
        properties.Select(property => property.PropertyType).ShouldAllBe(type =>
            type == typeof(AgentActivityKind) || type == typeof(ulong));
        properties.Length.ShouldBe(3);
    }

    [Fact]
    public async Task RunSubAgentAsync_UnchangedSuccessfulEvidenceForFullWindow_FinalizesPartial()
    {
        var run = CreateActivityRun(Enumerable.Repeat(new[] { Tool(AgentActivityKind.ToolSucceeded, 100) }, 10).ToArray());
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        result.Status.ShouldBe(SubAgentStatus.Failed);
        run.ExplorationTurns.ShouldBe(4);
        result.TurnsUsed.ShouldBe(5);
        result.PartialResult.ShouldNotBeNull().UnresolvedWork.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RunSubAgentAsync_NewSuccessLaterInThresholdBatch_DoesNotFinalize()
    {
        var run = CreateActivityRun([FailedTurn(), FailedTurn(),
            [.. FailedTurn(), Tool(AgentActivityKind.ToolSucceeded, 100)]]);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        run.FinalizationCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunSubAgentAsync_ReserveAlreadyOwnsFinalization_LateActivityDoesNotLatchNoProgress()
    {
        var time = new ControllableTimeProvider();
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(),
            beforeExploration: () => { time.Advance(TimeSpan.FromSeconds(271)); return Task.CompletedTask; },
            ignoreCancellation: true);
        var (manager, _, _) = CreateManager(run.Handle, time,
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.PartialResult.ShouldBeNull();
        run.FinalizationCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RunSubAgentAsync_NoProgress_PreservesToolEvidenceActionsAndUnresolvedFailures()
    {
        AgentToolCallInfo[] tools = [new("read-1", "read", false, ResultContent: "verified evidence"),
            new("write-1", "write", true, ResultContent: "private failure")];
        var run = CreateActivityRun(Enumerable.Repeat(FailedTurn(), 10).ToArray(), explorationTools: tools);
        var (manager, _, _) = CreateManager(run.Handle, new ControllableTimeProvider(),
            timeoutSecondsBudget: 300, configureSubAgents: options => EnableNoProgress(options, 3));

        var result = await JoinActivityRunAsync(manager);

        AssertNoProgress(result);
        result.Status.ShouldBe(SubAgentStatus.Failed);
        var partial = result.PartialResult.ShouldNotBeNull();
        partial.VerifiedEvidence.ShouldHaveSingleItem().Result.ShouldBe("verified evidence");
        partial.ActionsTaken.Count.ShouldBe(2);
        partial.ActionsTaken.Single(action => action.ToolCallId == "write-1").Succeeded.ShouldBeFalse();
        partial.UnresolvedWork.ShouldContain("write (write-1) did not complete successfully.");
        partial.TurnsUsed.ShouldBe(4);
        result.ResultSummary.ShouldNotBeNull().ShouldNotContain("private failure");
    }

    private static void EnableNoProgress(SubAgentOptions options, int window)
    {
        options.NoProgressEnabled = true;
        options.NoProgressTurnWindow = window;
        options.NoProgressRepeatedFailureThreshold = window;
    }

    private static void AssertNoProgress(SubAgentInfo result)
    {
        result.Status.ShouldNotBe(SubAgentStatus.Completed);
        result.Status.ShouldNotBe(SubAgentStatus.HandedOff);
        result.Status.ShouldNotBe(SubAgentStatus.Running);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.Completion.ShouldBe(SubAgentCompletion.Partial);
        result.PartialResult.StopReason.ShouldBe(SubAgentStopReason.NoProgress);
        result.PartialResult.SummaryIsVerified.ShouldBeFalse();
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("[partial:no_progress]");
        result.ResultSummary.ShouldContain("supporting turns: ");
    }

    private static AgentActivityObservation Tool(AgentActivityKind kind, ulong digest)
        => new(kind, digest, 0);

    private static AgentActivityObservation[] FailedTurn()
        => [Tool(AgentActivityKind.ToolFailed, 1)];

    private static SubAgentSpawnRequest ActivityRequest() => new()
    {
        ParentAgentId = AgentId.From("parent-agent"),
        ParentSessionId = SessionId.From("parent-session"),
        Task = "Do background work", TimeoutSeconds = 300, MaxTurns = 30,
        Mode = new Embody(SubAgentArchetype.General),
        InheritedConversationId = ConversationId.From("inherited-conversation")
    };

    private static async Task<SubAgentInfo> JoinActivityRunAsync(DefaultSubAgentManager manager)
    {
        var spawned = await manager.SpawnAsync(ActivityRequest());
        return await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);
    }

    private static ActivityRun CreateActivityRun(
        AgentActivityObservation[][] turns,
        string explorationText = "Exploration evidence remains unverified.",
        string finalText = "Everything succeeded and was validated.",
        bool finalizerThrows = false,
        bool exposeActivity = true,
        Action? onFinalization = null,
        Func<Task>? beforeExploration = null,
        bool ignoreCancellation = false,
        Action? onExplorationDrained = null,
        IReadOnlyList<AgentToolCallInfo>? explorationTools = null)
    {
        Action? onTurn = null;
        Action<AgentActivityObservation>? onActivity = null;
        var run = new ActivityRun();
        run.Handle = CreateHandle(async token =>
        {
            run.ExplorationToken = token;
            if (beforeExploration is not null)
                await beforeExploration();
            foreach (var turn in turns)
            {
                if (token.IsCancellationRequested && !ignoreCancellation)
                    break;
                foreach (var observation in turn)
                    onActivity?.Invoke(observation);
                run.ExplorationTurns++;
                onActivity?.Invoke(new AgentActivityObservation(AgentActivityKind.TurnCompleted, 0, 0));
                onTurn?.Invoke();
            }
            onExplorationDrained?.Invoke();
            var response = new AgentResponse { Content = explorationText, TurnCount = run.ExplorationTurns,
                ToolCalls = explorationTools ?? [] };
            if (token.IsCancellationRequested && !ignoreCancellation)
                throw new AgentPromptInterruptedException(response, token);
            return response;
        });
        run.Handle.Setup(handle => handle.ObserveTurns(It.IsAny<Action>()))
            .Callback<Action>(callback => onTurn = callback).Returns(Mock.Of<IDisposable>());
        if (exposeActivity)
        {
            var subscription = new Mock<IDisposable>();
            subscription.Setup(value => value.Dispose()).Callback(() =>
            {
                run.ActivitySubscriptionDisposed = true;
                onActivity = null;
            });
            run.Handle.Setup(handle => handle.ObserveActivity(It.IsAny<Action<AgentActivityObservation>>()))
                .Callback<Action<AgentActivityObservation>>(callback => onActivity = callback)
                .Returns(subscription.Object);
        }
        run.Handle.Setup(handle => handle.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) =>
            {
                run.FinalizationCalls++;
                run.FinalizationToken = token;
                onFinalization?.Invoke();
                if (finalizerThrows)
                    throw new IOException("finalizer unavailable");
                return Task.FromResult(new AgentResponse { Content = finalText });
            });
        return run;
    }

    private sealed class ActivityRun
    {
        public Mock<IAgentHandle> Handle { get; set; } = new();
        public int ExplorationTurns { get; set; }
        public int FinalizationCalls { get; set; }
        public CancellationToken ExplorationToken { get; set; }
        public CancellationToken FinalizationToken { get; set; }
        public bool ActivitySubscriptionDisposed { get; set; }
    }
}
