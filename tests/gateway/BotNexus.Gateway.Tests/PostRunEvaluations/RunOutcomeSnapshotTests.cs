using System.Collections.Immutable;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Evaluations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests.PostRunEvaluations;

public sealed class RunOutcomeSnapshotTests
{
    [Fact]
    public void Create_CopiesAndBoundsRunEvidence()
    {
        var openItems = new List<string> { "one" };
        var tools = new List<RunOutcomeTool>
        {
            new("call-1", "read", "arguments", "result", false, false)
        };
        var completion = new RunCompletionSignal(
            "Completed", openItems, null, "detail", "evidence", null, null, 0);

        var snapshot = RunOutcomeSnapshot.Create(
            RunId.From("run-1"),
            SessionId.From("session-1"),
            ConversationId.From("conversation-1"),
            AgentId.From("agent-1"),
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            new string('x', 100),
            completion,
            new RunOutcomeUsage(11, 7, 3, 2, 2),
            tools,
            new RunOutcomeSnapshotLimits(MaxAssistantContentBytes: 12, MaxCompletionDetailBytes: 8, MaxToolCount: 1, MaxToolFieldBytes: 6));

        openItems.Add("two");
        tools.Clear();

        snapshot.SchemaVersion.ShouldBe(1);
        snapshot.Completion?.OpenItemIds.ShouldBe(ImmutableArray.Create("one"));
        snapshot.Tools.ShouldHaveSingleItem();
        Encoding.UTF8.GetByteCount(snapshot.AssistantContent).ShouldBeLessThanOrEqualTo(12);
        Encoding.UTF8.GetByteCount(snapshot.Tools[0].Arguments ?? string.Empty).ShouldBeLessThanOrEqualTo(6);
        snapshot.Usage.ShouldBe(new RunOutcomeUsage(11, 7, 3, 2, 2));
    }

    [Fact]
    public void Create_UnicodeBoundsNeverSplitUtf8Sequence()
    {
        var snapshot = RunOutcomeSnapshot.Create(
            RunId.From("run-unicode"),
            SessionId.From("session-1"),
            ConversationId.From("conversation-1"),
            AgentId.From("agent-1"),
            DateTimeOffset.UtcNow,
            "a😀b",
            completion: null,
            usage: null,
            tools: [],
            new RunOutcomeSnapshotLimits(MaxAssistantContentBytes: 5));

        snapshot.AssistantContent.ShouldBe("a😀");
        Encoding.UTF8.GetByteCount(snapshot.AssistantContent).ShouldBe(5);
    }

    [Fact]
    public void FromAgentResponse_ProjectsCompletionUsageAndTools()
    {
        var response = new AgentResponse
        {
            Content = "answer",
            Completion = new RunCompletionSignal("Parked", ["item"], "UserInput", "why", "proof", "user", "reply", 1),
            RunUsage = new AgentResponseUsage(31, 12, 5, 3),
            TurnCount = 3,
            ToolCalls = [new AgentToolCallInfo("call", "read", false, "{}", "ok")]
        };

        var snapshot = RunOutcomeSnapshot.FromAgentResponse(
            RunId.From("run-response"),
            SessionId.From("session-1"),
            ConversationId.From("conversation-1"),
            AgentId.From("agent-1"),
            DateTimeOffset.UtcNow,
            response);

        snapshot.Completion?.Status.ShouldBe("Parked");
        snapshot.Usage.ShouldBe(new RunOutcomeUsage(31, 12, 5, 3, 3));
        snapshot.Tools.ShouldHaveSingleItem().ToolName.ShouldBe("read");
    }
}
