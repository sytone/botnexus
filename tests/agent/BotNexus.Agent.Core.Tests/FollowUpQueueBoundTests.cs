using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Covers the bounded follow-up queue and the reclaim primitive added for #2438.
/// </summary>
/// <remarks>
/// The bound exists so a producer that enqueues faster than turns complete cannot grow the
/// pending set without limit. Overflow must be a loud, typed refusal - never a silent drop -
/// because a silently dropped follow-up is exactly the message loss #2388 describes.
/// The reclaim primitive is what lets the gateway boundary hand a follow-up back to the normal
/// send path when the run settles before the loop drains it, instead of stranding it.
/// </remarks>
public sealed class FollowUpQueueBoundTests
{
    private static UserMessage Msg(string text) => new(text);

    [Fact]
    public void FollowUpQueue_DefaultCapacity_IsBounded()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());

        agent.FollowUpQueueCapacity.ShouldBe(Agent.DefaultFollowUpQueueCapacity);
        Agent.DefaultFollowUpQueueCapacity.ShouldBe(64);
    }

    [Fact]
    public void FollowUp_UpToCapacity_IsAccepted()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        agent.FollowUpQueueCapacity = 3;
        var third = Msg("three");

        agent.FollowUp(Msg("one"));
        agent.FollowUp(Msg("two"));
        agent.FollowUp(third);

        // The message at exactly the capacity boundary really is in the queue.
        agent.TryReclaimFollowUp(third).ShouldBeTrue();
    }

    [Fact]
    public void FollowUp_PastCapacity_ThrowsQueueFullAndDoesNotEnqueue()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        agent.FollowUpQueueCapacity = 2;
        agent.FollowUp(Msg("one"));
        agent.FollowUp(Msg("two"));
        var refused = Msg("three");

        var ex = Should.Throw<PendingMessageQueueFullException>(() => agent.FollowUp(refused));

        ex.Capacity.ShouldBe(2);
        // The refused message must not be sitting in the queue: the caller owns it and is
        // responsible for reporting the refusal.
        agent.TryReclaimFollowUp(refused).ShouldBeFalse();
    }

    [Fact]
    public void FollowUp_AfterOverflowAndReclaim_AcceptsAgain()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        agent.FollowUpQueueCapacity = 1;
        var first = Msg("one");
        agent.FollowUp(first);
        Should.Throw<PendingMessageQueueFullException>(() => agent.FollowUp(Msg("two")));

        agent.TryReclaimFollowUp(first).ShouldBeTrue();
        var third = Msg("three");
        agent.FollowUp(third);

        // Capacity is a live bound on the pending set, not a one-way latch.
        agent.TryReclaimFollowUp(third).ShouldBeTrue();
    }

    [Fact]
    public void TryReclaimFollowUp_WhenStillQueued_RemovesOnlyThatMessage()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        var mine = Msg("mine");
        var otherBefore = Msg("other-before");
        var otherAfter = Msg("other-after");
        agent.FollowUp(otherBefore);
        agent.FollowUp(mine);
        agent.FollowUp(otherAfter);

        agent.TryReclaimFollowUp(mine).ShouldBeTrue();

        // Concurrently queued follow-ups from other producers are untouched.
        agent.TryReclaimFollowUp(otherBefore).ShouldBeTrue();
        agent.TryReclaimFollowUp(otherAfter).ShouldBeTrue();
    }

    [Fact]
    public void TryReclaimFollowUp_WhenNeverQueued_ReturnsFalse()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());

        // False is the signal the gateway boundary reads as "the run loop already took it, do
        // not also send it normally". Getting this wrong duplicates or loses the message.
        agent.TryReclaimFollowUp(Msg("never queued")).ShouldBeFalse();
    }

    [Fact]
    public void TryReclaimFollowUp_AfterClear_ReturnsFalse()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        var mine = Msg("mine");
        agent.FollowUp(mine);

        agent.ClearFollowUpQueue();

        agent.TryReclaimFollowUp(mine).ShouldBeFalse();
    }

    [Fact]
    public void TryReclaimFollowUp_OnlyMatchesReferenceIdentity_NotEqualContent()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        var queued = Msg("same text");
        agent.FollowUp(queued);

        agent.TryReclaimFollowUp(Msg("same text")).ShouldBeFalse();

        // The genuinely queued instance is still there.
        agent.TryReclaimFollowUp(queued).ShouldBeTrue();
    }

    [Fact]
    public void TryReclaimFollowUp_Twice_ReturnsFalseTheSecondTime()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        var mine = Msg("mine");
        agent.FollowUp(mine);

        agent.TryReclaimFollowUp(mine).ShouldBeTrue();
        // Exactly-once ownership transfer: only one caller can win the reclaim.
        agent.TryReclaimFollowUp(mine).ShouldBeFalse();
    }
}
