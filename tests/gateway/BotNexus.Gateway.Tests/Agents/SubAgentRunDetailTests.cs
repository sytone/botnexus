using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentRunDetailTests
{
    [Fact]
    public void FromLive_BoundsTextAndReportsRunningCapacity()
    {
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var detail = SubAgentRunDetail.FromLive(new SubAgentInfo
        {
            SubAgentId = "sub-1",
            ParentSessionId = SessionId.From("parent-session"),
            ChildSessionId = SessionId.From("child-session"),
            ParentConversationId = ConversationId.From("parent-conversation"),
            ChildConversationId = ConversationId.From("child-conversation"),
            ParentAgentId = "parent-agent",
            ChildAgentId = "child-agent",
            Name = new string('n', 300),
            Task = new string('t', 5000),
            Model = new string('m', 300),
            Status = SubAgentStatus.Running,
            StartedAt = started,
            EffectiveMaxTurns = 10,
            EffectiveTimeoutSeconds = 120,
            TurnsUsed = 4
        }, started.AddSeconds(30));

        detail.Status.ShouldBe(SubAgentStatus.Running);
        detail.Name!.Length.ShouldBeLessThanOrEqualTo(SubAgentRunDetail.MaxShortTextLength);
        detail.Task!.Length.ShouldBeLessThanOrEqualTo(SubAgentRunDetail.MaxLongTextLength);
        detail.RemainingTurns.ShouldBe(6);
        detail.RemainingTimeSeconds.ShouldBe(90);
        detail.ElapsedSeconds.ShouldBe(30);
    }

    [Fact]
    public void FromLive_UnknownMeasurementsRemainNull_AndPrivatePathsAreRemoved()
    {
        var detail = SubAgentRunDetail.FromLive(new SubAgentInfo
        {
            SubAgentId = "sub-2",
            ParentSessionId = SessionId.From("parent-session"),
            ChildSessionId = SessionId.From("child-session"),
            Task = "task",
            Status = SubAgentStatus.TimedOut,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            WorktreeSnapshot = new SubAgentWorktreeSnapshot(
                SubAgentWorktreeSnapshotOutcome.Captured, "C:/private/user/worktree", "refs/botnexus/snapshot", 1, ["C:/private/file.cs"], false)
        });

        detail.EffectiveMaxTurns.ShouldBeNull();
        detail.EffectiveTimeoutSeconds.ShouldBeNull();
        detail.RemainingTurns.ShouldBeNull();
        detail.RemainingTimeSeconds.ShouldBeNull();
        detail.WorktreeSnapshot.ShouldNotBeNull();
        detail.WorktreeSnapshot!.WorktreePath.ShouldBeNull();
        detail.WorktreeSnapshot.ChangedFiles.ShouldAllBe(path => !Path.IsPathRooted(path));
    }
}
