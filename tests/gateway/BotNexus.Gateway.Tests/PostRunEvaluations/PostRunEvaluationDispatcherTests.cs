using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Evaluations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Evaluations;

namespace BotNexus.Gateway.Tests.PostRunEvaluations;

public sealed class PostRunEvaluationDispatcherTests
{
    [Fact]
    public void DispatchAfterFinalizer_Persisted_EnqueuesSnapshot()
    {
        var coordinator = new CapturingCoordinator();
        var dispatcher = new PostRunEvaluationDispatcher(coordinator);
        var snapshot = Snapshot();

        var admission = dispatcher.DispatchAfterFinalizer(SessionSaveOutcome.Persisted, snapshot);

        admission.ShouldBe(PostRunEvaluationAdmission.Accepted);
        coordinator.Snapshots.ShouldBe([snapshot]);
    }

    [Fact]
    public void DispatchAfterFinalizer_Rebound_DoesNotEnqueue()
    {
        var coordinator = new CapturingCoordinator();
        var dispatcher = new PostRunEvaluationDispatcher(coordinator);

        var admission = dispatcher.DispatchAfterFinalizer(SessionSaveOutcome.Rebound, Snapshot());

        admission.ShouldBe(PostRunEvaluationAdmission.Disabled);
        coordinator.Snapshots.ShouldBeEmpty();
    }

    private static RunOutcomeSnapshot Snapshot() => RunOutcomeSnapshot.Create(
        RunId.From("run-dispatch"),
        SessionId.From("session-1"),
        ConversationId.From("conversation-1"),
        AgentId.From("agent-1"),
        DateTimeOffset.UtcNow,
        "done",
        null,
        null,
        []);

    private sealed class CapturingCoordinator : IPostRunEvaluationCoordinator
    {
        public List<RunOutcomeSnapshot> Snapshots { get; } = [];

        public PostRunEvaluationAdmission Enqueue(RunOutcomeSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
            return PostRunEvaluationAdmission.Accepted;
        }
    }
}
