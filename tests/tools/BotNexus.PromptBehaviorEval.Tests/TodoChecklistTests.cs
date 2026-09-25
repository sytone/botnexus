using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class TodoChecklistTests
{
    [Fact]
    public void Replace_ValidDiscoverySequence_RecordsSnapshotsAndAddedItem()
    {
        var checklist = new TodoChecklist();
        checklist.Replace([new TodoItem("change", "Apply the requested fixture change", TodoItemStatus.InProgress), new TodoItem("verify", "Verify the fixture change", TodoItemStatus.Pending)]).Accepted.ShouldBeTrue();
        checklist.RecordInspection();
        checklist.Replace([new TodoItem("change", "Apply the requested fixture change", TodoItemStatus.InProgress), new TodoItem("verify", "Verify the fixture change", TodoItemStatus.Pending), new TodoItem(TodoChecklist.DiscoveredItemId, "Check the fixture configuration", TodoItemStatus.Pending)]).Accepted.ShouldBeTrue();
        checklist.RecordVerification();
        checklist.Replace([new TodoItem("change", "Apply the requested fixture change", TodoItemStatus.Done), new TodoItem("verify", "Verify the fixture change", TodoItemStatus.Done), new TodoItem(TodoChecklist.DiscoveredItemId, "Check the fixture configuration", TodoItemStatus.Done)]).Accepted.ShouldBeTrue();

        checklist.Transitions.Count.ShouldBe(3);
        checklist.Transitions.ShouldAllBe(transition => transition.Accepted);
        checklist.Snapshots.Count.ShouldBe(3);
        checklist.AddedDiscoveredItemAfterInspection.ShouldBeTrue();
        checklist.DistinctDoneItemCount.ShouldBe(3);
    }

    [Fact]
    public void Replace_DiscoveredItemBeforeInspection_RejectsAndPreservesState()
    {
        var checklist = new TodoChecklist();
        var rejected = checklist.Replace([new TodoItem("change", "Apply the requested fixture change", TodoItemStatus.Pending), new TodoItem(TodoChecklist.DiscoveredItemId, "Check the fixture configuration", TodoItemStatus.Pending)]);

        rejected.Accepted.ShouldBeFalse();
        (rejected.FailureReason ?? string.Empty).ShouldContain("inspection");
        checklist.Items.ShouldBeEmpty();
        checklist.Transitions.ShouldHaveSingleItem().Accepted.ShouldBeFalse();
    }

    [Fact]
    public void Replace_DoneBeforeVerification_RejectsAndPreservesPreviousSnapshot()
    {
        var checklist = new TodoChecklist();
        checklist.Replace([new TodoItem("change", "Apply the requested fixture change", TodoItemStatus.InProgress), new TodoItem("verify", "Verify the fixture change", TodoItemStatus.Pending)]).Accepted.ShouldBeTrue();
        var rejected = checklist.Replace([new TodoItem("change", "Apply the requested fixture change", TodoItemStatus.Done), new TodoItem("verify", "Verify the fixture change", TodoItemStatus.Done)]);

        rejected.Accepted.ShouldBeFalse();
        (rejected.FailureReason ?? string.Empty).ShouldContain("verification");
        checklist.Items.ShouldAllBe(item => item.Status != TodoItemStatus.Done);
        checklist.Snapshots.Count.ShouldBe(1);
    }
}
