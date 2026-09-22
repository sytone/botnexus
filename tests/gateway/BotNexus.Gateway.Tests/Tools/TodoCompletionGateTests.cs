using System.Text.Json;
using BotNexus.Agent.Core.Loop;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class TodoCompletionGateTests
{
    [Fact]
    public void NoChecklist_CompletesNormally()
    {
        TodoTool.EvaluateRunCompletion(new Conversation()).Status.ShouldBe(RunCompletionStatus.Completed);
    }

    [Fact]
    public void TerminalChecklist_CompletesNormally()
    {
        var conversation = ConversationWithItems(("done", "done"), ("cancelled", "cancelled"));

        TodoTool.EvaluateRunCompletion(conversation).Status.ShouldBe(RunCompletionStatus.Completed);
    }

    [Fact]
    public void OpenChecklist_RequiresContinuation()
    {
        var conversation = ConversationWithItems(("implement", "in_progress"), ("publish", "pending"));

        var result = TodoTool.EvaluateRunCompletion(conversation);

        result.Status.ShouldBe(RunCompletionStatus.Working);
        result.OpenItemIds.ShouldBe(["implement", "publish"]);
        result.StopReason.ShouldBeNull();
    }

    [Fact]
    public void PendingAskUser_ParksOpenChecklistWithStructuredEvidence()
    {
        var conversation = ConversationWithItems(("decision", "in_progress"));
        conversation.PendingAskUserJson = "{\"id\":\"ask-1\"}";

        var result = TodoTool.EvaluateRunCompletion(conversation);

        result.Status.ShouldBe(RunCompletionStatus.Parked);
        result.StopReason.ShouldBe(RunStopReason.UserInput);
        result.Evidence.ShouldNotBeNull();
        result.Evidence.ShouldContain("persisted");
        result.ContinuationOwner.ShouldBe("user");
        result.WakeCondition.ShouldNotBeNullOrWhiteSpace();
    }

    private static Conversation ConversationWithItems(params (string Id, string Status)[] items)
    {
        var todoJson = JsonSerializer.Serialize(new
        {
            items = items.Select(item => new
            {
                id = item.Id,
                text = item.Id,
                status = item.Status,
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow,
            }),
        });
        return new Conversation { TodoJson = todoJson };
    }
}
