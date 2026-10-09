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
    /// <summary>
    /// The run's deadline is scheduled on a virtual clock and fires ONLY because the test advances
    /// it from <c>onPoll</c>. The 300-second budget cannot elapse in real time inside the harness's
    /// 5-second hang guard, so the terminal transition is caused by a signal the test controls and
    /// never by elapsed wall-clock time on a loaded runner (#3216). #3215 converted the sibling
    /// race case for the same reason and left this one on the ambient clock.
    /// </summary>
    [Fact]
    public async Task RunSubAgentAsync_PromptThrowsAfterTimeout_ReportsTimedOut()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        AssertTimedOut(result, dispatcher, timeoutSeconds: 300);
    }

    /// <summary>
    /// Same construction as the throwing case: a 300-second deadline on a virtual clock, reached
    /// only by the test's explicit advance (#3216). The classification under test is unchanged -
    /// an empty response returned AFTER cancellation must still resolve to TimedOut rather than
    /// the Failed-on-empty path.
    /// </summary>
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
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        AssertTimedOut(result, dispatcher, timeoutSeconds: 300);
    }

    /// <summary>#4286: timeout preserves the typed response, including incomplete tool state and usage.</summary>
    [Fact]
    public async Task RunSubAgentAsync_InterruptedAtTimeout_PreservesStructuredPartialEvidence()
    {
        var partial = new AgentResponse
        {
            Content = "Read the configuration and started the final write.",
            RunUsage = new AgentResponseUsage(InputTokens: 80, OutputTokens: 12),
            TurnCount = 3,
            ToolCalls =
            [
                new AgentToolCallInfo("read-1", "read", false, ResultContent: "configuration evidence"),
                new AgentToolCallInfo("write-1", "write", false, IsIncomplete: true)
            ]
        };
        var handle = CreateHandle(async token =>
        {
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancellationObserved.SetResult());
            await cancellationObserved.Task;
            throw new AgentPromptInterruptedException(partial, token);
        });
        var time = new ControllableTimeProvider();
        var (manager, _, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(
            manager,
            dispatched,
            time,
            advanceBy: TimeSpan.FromSeconds(300),
            timeoutSeconds: 300);

        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.StopReason.ShouldBe(SubAgentStopReason.Timeout);
        result.PartialResult.TurnsUsed.ShouldBe(3);
        result.PartialResult.Usage.ShouldBe(partial.RunUsage);
        result.PartialResult.VerifiedEvidence.ShouldHaveSingleItem().ToolCallId.ShouldBe("read-1");
        result.PartialResult.ActionsTaken.Single(action => action.ToolCallId == "write-1").Completed.ShouldBeFalse();
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("[partial:timeout]");
        result.ResultSummary.ShouldContain(partial.Content);
    }

    /// <summary>#4297: the last turn is reserved for one text-only synthesis attempt.</summary>
    [Fact]
    public async Task RunSubAgentAsync_TurnReservation_ForcesOneToolFreeFinalizationWithinBudget()
    {
        Action? onTurnCompleted = null;
        var explorationToken = CancellationToken.None;
        var finalizationToken = CancellationToken.None;
        var partial = new AgentResponse
        {
            Content = "Inspected the implementation.",
            TurnCount = 2,
            ToolCalls = [new AgentToolCallInfo("read-1", "read", false, ResultContent: "evidence")]
        };
        var handle = CreateHandle(token =>
        {
            explorationToken = token;
            onTurnCompleted.ShouldNotBeNull();
            onTurnCompleted();
            onTurnCompleted();
            throw new AgentPromptInterruptedException(partial, token);
        });
        handle.Setup(h => h.ObserveTurns(It.IsAny<Action>()))
            .Callback<Action>(callback => onTurnCompleted = callback)
            .Returns(Mock.Of<IDisposable>());
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) =>
            {
                finalizationToken = token;
                return Task.FromResult(new AgentResponse { Content = "Implemented and validated the bounded fix.", TurnCount = 3 });
            });
        var (manager, _, dispatched) = CreateManager(handle, new ControllableTimeProvider(), maxTurnsBudget: 3);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, maxTurns: 3);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldBe("Implemented and validated the bounded fix.");
        result.TurnsUsed.ShouldBe(3);
        explorationToken.IsCancellationRequested.ShouldBeTrue();
        finalizationToken.IsCancellationRequested.ShouldBeFalse();
        handle.Verify(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunSubAgentAsync_MaxTurnsOne_UsesOnlyReservedFinalizationTurn()
    {
        var handle = CreateHandle(_ => throw new InvalidOperationException("exploration must not run"));
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "One-turn final answer." });
        var (manager, _, dispatched) = CreateManager(
            handle, new ControllableTimeProvider(), maxTurnsBudget: 1);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, maxTurns: 1);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.TurnsUsed.ShouldBe(1);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunSubAgentAsync_OrdinaryEarlyCompletion_DoesNotFinalize()
    {
        Action? onTurnCompleted = null;
        var handle = CreateHandle(_ =>
        {
            onTurnCompleted.ShouldNotBeNull();
            onTurnCompleted();
            return Task.FromResult(new AgentResponse { Content = "Completed early." });
        });
        handle.Setup(h => h.ObserveTurns(It.IsAny<Action>()))
            .Callback<Action>(callback => onTurnCompleted = callback)
            .Returns(Mock.Of<IDisposable>());
        var (manager, _, dispatched) = CreateManager(
            handle, new ControllableTimeProvider(), maxTurnsBudget: 3);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, maxTurns: 3);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldBe("Completed early.");
        result.TurnsUsed.ShouldBe(1);
        handle.Verify(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunSubAgentAsync_PreDeadlineReserve_CancelsOnlyExplorationAndRetainsAbsoluteDeadline()
    {
        var explorationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var explorationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizationStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = CreateHandle(async token =>
        {
            explorationStarted.TrySetResult();
            using var registration = token.Register(() => explorationCancelled.TrySetResult());
            await explorationCancelled.Task;
            throw new AgentPromptInterruptedException(
                new AgentResponse { Content = "Evidence before reserve." }, token);
        });
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, token) =>
            {
                finalizationStarted.TrySetResult(token);
                return Task.FromResult(new AgentResponse { Content = "Reserved synthesis." });
            });
        var time = new ControllableTimeProvider();
        var (manager, _, dispatched) = CreateManager(
            handle, time, timeoutSecondsBudget: 300, maxTurnsBudget: 3);
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 300,
            MaxTurns = 3,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        await explorationStarted.Task.WaitAsync(HangGuard);
        time.Advance(TimeSpan.FromSeconds(270));
        var finalToken = await finalizationStarted.Task.WaitAsync(HangGuard);
        await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);
        var result = await manager.GetAsync(spawned.SubAgentId);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldBe("Reserved synthesis.");
        finalToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task RunSubAgentAsync_ReserveFinalizationFailure_WaitsForDeadlineAndClassifiesTimeout()
    {
        var explorationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = CreateHandle(async token =>
        {
            explorationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "never" };
        });
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("finalization unavailable"));
        var time = new ControllableTimeProvider();
        var (manager, _, dispatched) = CreateManager(
            handle, time, timeoutSecondsBudget: 300, maxTurnsBudget: 3);
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 300,
            MaxTurns = 3,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        await explorationStarted.Task.WaitAsync(HangGuard);
        time.Advance(TimeSpan.FromSeconds(270));
        (await manager.GetAsync(spawned.SubAgentId)).ShouldNotBeNull().Status.ShouldBe(SubAgentStatus.Running);

        time.Advance(TimeSpan.FromSeconds(30));
        await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);
        var result = await manager.GetAsync(spawned.SubAgentId);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.ResultSummary.ShouldNotBeNull().ShouldContain("timed out");
    }

    [Fact]
    public async Task RunSubAgentAsync_FinalizationReturnsLateText_ClassifiesTimeoutAndRejectsText()
    {
        Action? onTurnCompleted = null;
        var finalizationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = CreateHandle(token =>
        {
            onTurnCompleted.ShouldNotBeNull();
            onTurnCompleted();
            onTurnCompleted();
            throw new AgentPromptInterruptedException(
                new AgentResponse
                {
                    Content = "Exploration evidence.",
                    ToolCalls = [new AgentToolCallInfo("read-1", "read", false, ResultContent: "evidence")]
                },
                token);
        });
        handle.Setup(h => h.ObserveTurns(It.IsAny<Action>()))
            .Callback<Action>(callback => onTurnCompleted = callback)
            .Returns(Mock.Of<IDisposable>());
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, token) =>
            {
                finalizationStarted.TrySetResult();
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => cancelled.TrySetResult());
                await cancelled.Task;
                return new AgentResponse { Content = "LATE TEXT MUST NOT WIN" };
            });
        var time = new ControllableTimeProvider();
        var (manager, _, dispatched) = CreateManager(
            handle, time, timeoutSecondsBudget: 300, maxTurnsBudget: 3);
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 300,
            MaxTurns = 3,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });
        await finalizationStarted.Task.WaitAsync(HangGuard);

        time.Advance(TimeSpan.FromSeconds(300));
        await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);
        var result = await manager.GetAsync(spawned.SubAgentId);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.StopReason.ShouldBe(SubAgentStopReason.Timeout);
        result.PartialResult.VerifiedEvidence.ShouldHaveSingleItem().ToolCallId.ShouldBe("read-1");
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldNotContain("LATE TEXT MUST NOT WIN");
    }

    [Fact]
    public async Task RunSubAgentAsync_SuccessfulFinalization_PreservesExplorationFailureOutcome()
    {
        Action? onTurnCompleted = null;
        var exploration = new AgentResponse
        {
            Content = "Attempted the write.",
            ToolCalls = [new AgentToolCallInfo("write-1", "write", false, IsIncomplete: true)]
        };
        var handle = CreateHandle(token =>
        {
            onTurnCompleted.ShouldNotBeNull();
            onTurnCompleted();
            onTurnCompleted();
            throw new AgentPromptInterruptedException(exploration, token);
        });
        handle.Setup(h => h.ObserveTurns(It.IsAny<Action>()))
            .Callback<Action>(callback => onTurnCompleted = callback)
            .Returns(Mock.Of<IDisposable>());
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Everything is complete." });
        var (manager, _, dispatched) = CreateManager(
            handle, new ControllableTimeProvider(), maxTurnsBudget: 3);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, maxTurns: 3);

        result.Status.ShouldBe(SubAgentStatus.Failed);
        result.Status.ShouldNotBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("did not complete");
        result.ResultSummary.ShouldContain("Everything is complete.");
    }

    /// <summary>#4297: failed synthesis cannot replace #4286 evidence or its original stop reason.</summary>
    [Fact]
    public async Task RunSubAgentAsync_FinalizationFailure_PreservesOriginalTurnLimitPartialEvidence()
    {
        Action? onTurnCompleted = null;
        var partial = new AgentResponse
        {
            Content = "Read the configuration before the budget stop.",
            TurnCount = 2,
            ToolCalls = [new AgentToolCallInfo("read-1", "read", false, ResultContent: "configuration evidence")]
        };
        var handle = CreateHandle(token =>
        {
            onTurnCompleted.ShouldNotBeNull();
            onTurnCompleted();
            onTurnCompleted();
            throw new AgentPromptInterruptedException(partial, token);
        });
        handle.Setup(h => h.ObserveTurns(It.IsAny<Action>()))
            .Callback<Action>(callback => onTurnCompleted = callback)
            .Returns(Mock.Of<IDisposable>());
        handle.Setup(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));
        var (manager, _, dispatched) = CreateManager(handle, new ControllableTimeProvider(), maxTurnsBudget: 3);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, maxTurns: 3);

        result.Status.ShouldBe(SubAgentStatus.BudgetExhausted);
        result.PartialResult.ShouldNotBeNull();
        result.PartialResult.StopReason.ShouldBe(SubAgentStopReason.TurnLimit);
        result.PartialResult.VerifiedEvidence.ShouldHaveSingleItem().ToolCallId.ShouldBe("read-1");
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("[partial:turn_limit]");
        result.ResultSummary.ShouldContain(partial.Content);
        handle.Verify(h => h.PromptWithoutToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunSubAgentAsync_SnapshotFailure_PreservesTimedOutStatusAndReportsFailure()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var snapshotService = new Mock<ISubAgentWorktreeSnapshotService>();
        snapshotService
            .Setup(service => service.CaptureAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("snapshot unavailable"));
        var time = new ControllableTimeProvider();
        var (manager, _, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300, snapshotService: snapshotService.Object);

        var result = await SpawnAndAwaitTerminalAsync(
            manager, dispatched, time, TimeSpan.FromSeconds(300), timeoutSeconds: 300, grantedWritePaths: ["granted"]);

        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.WorktreeSnapshot.ShouldNotBeNull();
        result.WorktreeSnapshot.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.ProcessFailed);
        snapshotService.Verify(service => service.CaptureAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(paths => paths.SequenceEqual(new[] { "granted" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }


    [Fact]
    public async Task RunSubAgentAsync_CompletionWinsDuringCapture_DeletesOrphanedArtifact()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource<SubAgentWorktreeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var artifactDeleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var artifactPath = Path.Combine(Path.GetTempPath(), "orphaned.patch");
        var snapshotService = new Mock<ISubAgentWorktreeSnapshotService>(MockBehavior.Strict);
        snapshotService
            .Setup(service => service.CaptureAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                captureStarted.SetResult();
                return await releaseCapture.Task;
            });
        snapshotService
            .Setup(service => service.DeleteArtifactAsync(artifactPath, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                artifactDeleted.SetResult();
                return Task.CompletedTask;
            });
        var time = new ControllableTimeProvider();
        var (manager, _, _) = CreateManager(handle, time, timeoutSecondsBudget: 300, snapshotService: snapshotService.Object);
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 300,
            GrantedWritePaths = ["granted"],
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        time.Advance(TimeSpan.FromSeconds(300));
        await captureStarted.Task.WaitAsync(HangGuard);
        await manager.OnCompletedAsync(spawned.SubAgentId, "External completion wins.");
        releaseCapture.SetResult(new SubAgentWorktreeSnapshot(
            SubAgentWorktreeSnapshotOutcome.Captured, "worktree", artifactPath, 12, [], false));
        await artifactDeleted.Task.WaitAsync(HangGuard);
        var result = await manager.GetAsync(spawned.SubAgentId);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.WorktreeSnapshot.ShouldBeNull();
        snapshotService.VerifyAll();
        snapshotService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunSubAgentAsync_TimeoutCaptureStarted_KillCannotWin()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource<SubAgentWorktreeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotService = new Mock<ISubAgentWorktreeSnapshotService>(MockBehavior.Strict);
        snapshotService
            .Setup(service => service.CaptureAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                captureStarted.SetResult();
                return await releaseCapture.Task;
            });
        var time = new ControllableTimeProvider();
        var (manager, _, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300, snapshotService: snapshotService.Object);
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 300,
            GrantedWritePaths = ["granted"],
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        time.Advance(TimeSpan.FromSeconds(300));
        await captureStarted.Task.WaitAsync(HangGuard);
        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("parent-session"));
        killed.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(300));
        releaseCapture.SetResult(new SubAgentWorktreeSnapshot(
            SubAgentWorktreeSnapshotOutcome.Captured, "worktree", Path.Combine(Path.GetTempPath(), "timeout-loser.patch"), 12, [], false));
        await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);
        var result = await manager.GetAsync(spawned.SubAgentId);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.WorktreeSnapshot.ShouldNotBeNull();
        snapshotService.VerifyAll();
        snapshotService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task KillAsync_ExplicitCallerKill_DoesNotCaptureSnapshot()
    {
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = CreateHandle(async token =>
        {
            promptStarted.TrySetResult();
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var subscription = new Mock<IDisposable>();
        subscription.Setup(item => item.Dispose()).Callback(() => runExited.TrySetResult());
        handle.Setup(item => item.ObserveTurns(It.IsAny<Action>())).Returns(subscription.Object);
        var snapshotService = new Mock<ISubAgentWorktreeSnapshotService>(MockBehavior.Strict);
        var time = new ControllableTimeProvider();
        var (manager, _, _) = CreateManager(handle, time, snapshotService: snapshotService.Object);
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = 300,
            GrantedWritePaths = ["granted"],
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        await promptStarted.Task.WaitAsync(HangGuard);
        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("parent-session"));
        await cancellationObserved.Task.WaitAsync(HangGuard);
        time.Advance(TimeSpan.FromSeconds(300));
        await runExited.Task.WaitAsync(HangGuard);
        var result = await manager.GetAsync(spawned.SubAgentId);

        killed.ShouldBeTrue();
        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Killed);
        result.WorktreeSnapshot.ShouldBeNull();
        snapshotService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunSubAgentAsync_EmptyResponseBeforeTimeout_ReportsFailed()
    {
        // The deadline is scheduled on a virtual clock that is never advanced, so it CANNOT fire.
        // The classification under test is therefore decided by the response content alone and no
        // longer by whether a synchronous delegate beats a real 1s timer on a loaded runner (#2979).
        var handle = CreateHandle(_ => Task.FromResult(new AgentResponse { Content = "  " }));
        var (manager, dispatcher, dispatched) = CreateManager(handle, new ControllableTimeProvider());

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched);

        result.Status.ShouldBe(SubAgentStatus.Failed);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("empty final response");
        VerifyDiagnostic(result, dispatcher, SubAgentStatus.Failed, "empty final response");
    }

    [Fact]
    public async Task RunSubAgentAsync_NonEmptyResponseBeforeTimeout_ReportsCompleted()
    {
        // Same deterministic construction as the Failed case: an unadvanced virtual deadline.
        var handle = CreateHandle(_ => Task.FromResult(new AgentResponse { Content = "Implemented the fix." }));
        var (manager, dispatcher, dispatched) = CreateManager(handle, new ControllableTimeProvider());

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched);

        result.Status.ShouldBe(SubAgentStatus.Completed);
        result.ResultSummary.ShouldBe("Implemented the fix.");
        VerifyDiagnostic(result, dispatcher, SubAgentStatus.Completed, "Implemented the fix.");
    }

    /// <summary>
    /// The race under test is preserved verbatim: the handle still returns an EMPTY response, and
    /// only after observing cancellation plus a yield, so the classification must still resolve to
    /// TimedOut rather than the Failed-on-empty path. What changed (#3215) is the clock. The
    /// deadline is scheduled on an injected <see cref="TimeProvider"/> with a <b>300-second</b>
    /// budget, so no real timer can fire inside the harness's 5-second poll window; the run reaches
    /// a terminal state only because <c>onPoll</c> advances the virtual clock. Previously this case
    /// passed <c>timeProvider: null</c> with a 1-second budget, so reaching terminal depended on a
    /// real timer plus continuation scheduling landing inside 5 wall-clock seconds on a shared
    /// 4-CPU runner - the flake reported in #3215.
    /// </summary>
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
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        AssertTimedOut(result, dispatcher, timeoutSeconds: 300);
        result.Status.ShouldNotBe(SubAgentStatus.Completed);
    }

    /// <summary>
    /// Pins the seam the fix depends on: the run's deadline must be scheduled on the INJECTED
    /// <see cref="TimeProvider"/>, not on the ambient <c>CancelAfter</c> timer.
    /// <para>
    /// The budget is <b>300 seconds</b> deliberately. A real timer with that budget cannot fire
    /// inside the harness's 5-second poll window, so the ONLY way this run can reach a terminal
    /// state is a virtual advance firing a timer the provider itself owns. Reverting the production
    /// code to <c>new CancellationTokenSource()</c> + <c>CancelAfter</c> makes this test fail with
    /// "Sub-agent did not reach a terminal state" - mutation-verified. An earlier version of this
    /// test used a 1-second budget and SURVIVED that mutation, because a real 1s timer fires inside
    /// the poll window regardless of which clock scheduled it: it proved nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunSubAgentAsync_TimeoutIsScheduledOnInjectedTimeProvider_VirtualAdvanceTimesOut()
    {
        var handle = CreateHandle(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentResponse { Content = "unreachable" };
        });
        var time = new ControllableTimeProvider();
        var (manager, dispatcher, dispatched) = CreateManager(handle, time, timeoutSecondsBudget: 300);

        var result = await SpawnAndAwaitTerminalAsync(manager, dispatched, time, advanceBy: TimeSpan.FromSeconds(300), timeoutSeconds: 300);

        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain("timed out after 300 seconds");
        VerifyDiagnostic(result, dispatcher, SubAgentStatus.TimedOut, "timed out after 300 seconds");
    }

    /// <summary>
    /// Spawns a run and returns its terminal snapshot, waiting on the SIGNAL that marks the run
    /// settled - the completion diagnostic the manager dispatches from <c>OnCompletedAsync</c>,
    /// raised only after the record's status has already been flipped out of
    /// <see cref="SubAgentStatus.Running"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This helper used to poll <c>GetAsync</c> every 20 ms inside a 5-second wall-clock budget and
    /// throw a <c>TimeoutException</c> when the budget elapsed. That budget was a race, not an
    /// assertion: on a loaded 4-CPU container the run could simply fail to be scheduled within 5 s
    /// and the test reddened without any production behaviour being wrong - observed on remote gate
    /// run 20260903181859-98cba615 against a diff that touched no sub-agent code at all (#3820).
    /// </para>
    /// <para>
    /// Waiting on the dispatch signal removes the budget from the success path entirely: the wait
    /// completes when the run settles, however long the runner took to get there. The virtual clock
    /// advance that CAUSES a timeout case to settle is applied once, immediately after
    /// <c>SpawnAsync</c> returns - safe because the deadline's <c>CancellationTokenSource</c> is
    /// constructed synchronously inside <c>SpawnAsync</c>, before the run task is queued, so the
    /// virtual timer provably exists by the time it is advanced.
    /// </para>
    /// <para>
    /// The <see cref="HangGuard"/> bound that remains is a HANG guard and nothing else. It is an
    /// order of magnitude larger than any scheduling delay a runner can plausibly impose, so it can
    /// only be reached by a run that never settles at all - and its failure is then a genuine
    /// defect, not a lost race.
    /// </para>
    /// </remarks>
    private static async Task<SubAgentInfo> SpawnAndAwaitTerminalAsync(
        DefaultSubAgentManager manager,
        TaskCompletionSource dispatched,
        ControllableTimeProvider? time = null,
        TimeSpan? advanceBy = null,
        int timeoutSeconds = 1,
        IReadOnlyList<string>? grantedWritePaths = null,
        int maxTurns = 30)
    {
        var spawned = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            TimeoutSeconds = timeoutSeconds,
            MaxTurns = maxTurns,
            GrantedWritePaths = grantedWritePaths,
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conversation")
        });

        if (time is not null && advanceBy is { } delta)
            time.Advance(delta);

        await manager.WaitAsync(spawned.SubAgentId, spawned.ParentSessionId).WaitAsync(HangGuard);

        var current = await manager.GetAsync(spawned.SubAgentId);
        current.ShouldNotBeNull();
        current.Status.ShouldNotBe(SubAgentStatus.Running);
        return current;
    }

    /// <summary>
    /// Upper bound on a run that has genuinely deadlocked. Never reached on the success path, which
    /// is signal-driven; see <see cref="SpawnAndAwaitTerminalAsync"/>.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asserts the timed-out terminal state and its dispatched diagnostic. The budget is a
    /// parameter only so a case running on a virtual clock can use a budget large enough that no
    /// real timer could fire (#3215); the assertions themselves are identical for every caller and
    /// the expected diagnostic text is still derived from the budget, so it cannot be satisfied by
    /// a timeout of the wrong length.
    /// </summary>
    private static void AssertTimedOut(
        SubAgentInfo result,
        Mock<IChannelDispatcher> dispatcher,
        int timeoutSeconds = 1)
    {
        var expected = $"timed out after {timeoutSeconds} {(timeoutSeconds == 1 ? "second" : "seconds")}";
        result.Status.ShouldBe(SubAgentStatus.TimedOut);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain(expected);
        VerifyDiagnostic(result, dispatcher, SubAgentStatus.TimedOut, expected);
    }

    private static void VerifyDiagnostic(SubAgentInfo result, Mock<IChannelDispatcher> dispatcher, SubAgentStatus status, string diagnostic)
    {
        result.Status.ShouldBe(status);
        result.ResultSummary.ShouldNotBeNull();
        result.ResultSummary.ShouldContain(diagnostic);
        dispatcher.Verify(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
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
    /// A <see cref="TimeProvider"/> whose clock only moves when a test moves it. Timers created
    /// through it fire solely from <see cref="Advance"/>, so a deadline scheduled on this provider
    /// is unreachable until the test chooses to reach it. That is what makes the non-timeout
    /// classification tests independent of runner load (#2979).
    /// </summary>
    private sealed class ControllableTimeProvider : TimeProvider
    {
        private readonly List<VirtualTimer> _timers = [];
        private readonly Lock _gate = new();
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state, dueTime);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        /// <summary>Moves the virtual clock forward and fires every timer now due.</summary>
        public void Advance(TimeSpan delta)
        {
            VirtualTimer[] due;
            lock (_gate)
            {
                _now = _now.Add(delta);
                due = [.. _timers.Where(t => t.IsDueAt(_now))];
            }

            foreach (var timer in due)
                timer.Fire();
        }

        internal void Remove(VirtualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        internal sealed class VirtualTimer(
            ControllableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow().Add(dueTime);
            private int _fired;

            public bool IsDueAt(DateTimeOffset now) => _dueAt is { } due && now >= due;

            public void Fire()
            {
                if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
                    return;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow().Add(dueTime);
                return true;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Builds the manager under test plus the completion SIGNAL the tests wait on. The signal is
    /// completed from the dispatcher stub, which the manager invokes only after the record's status
    /// has already left <see cref="SubAgentStatus.Running"/> - so observing it is equivalent to
    /// observing a terminal transition, without any wall-clock poll (#3820).
    /// </summary>
    private static (DefaultSubAgentManager Manager, Mock<IChannelDispatcher> Dispatcher, TaskCompletionSource Dispatched) CreateManager(
        Mock<IAgentHandle> handle,
        TimeProvider? timeProvider = null,
        int timeoutSecondsBudget = 1,
        ISubAgentWorktreeSnapshotService? snapshotService = null,
        int maxTurnsBudget = 30)
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

        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => dispatched.TrySetResult())
            .Returns(Task.CompletedTask);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Callback<GatewayActivity, CancellationToken>((eventData, _) =>
            {
                if (eventData.Type is GatewayActivityType.SubAgentCompleted or GatewayActivityType.SubAgentFailed)
                    dispatched.TrySetResult();
            }).Returns(ValueTask.CompletedTask);
        var options = new GatewayOptions();
        options.SubAgents.MaxTimeoutSeconds = timeoutSecondsBudget;
        options.SubAgents.DefaultTimeoutSeconds = timeoutSecondsBudget;
        options.SubAgents.MaxTurnsCeiling = maxTurnsBudget;
        options.SubAgents.DefaultMaxTurns = maxTurnsBudget;

        return (new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activity.Object,
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<DefaultSubAgentManager>.Instance,
            timeProvider: timeProvider,
            worktreeSnapshotService: snapshotService), dispatcher, dispatched);
    }
}
