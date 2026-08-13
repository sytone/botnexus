using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// #3014. <c>AgentLoopRunner.CompactForOverflow</c> truncated the transcript by raw count with no
/// awareness of tool-call/tool-result pairing, so the retained tail could begin with a
/// <see cref="ToolResultAgentMessage"/> whose originating assistant tool call was dropped. Anthropic
/// and the Copilot messages API reject that shape with a hard 400, and overflow recovery is allowed
/// exactly once, so the recoverable overflow became a terminal turn failure. These tests pin the
/// boundary invariant directly: the cut index is aligned forward past any stranded tool results.
/// </summary>
public class CompactForOverflowBoundaryTests
{
    private static AgentMessage User(string text) => new AgentUserMessage(text);

    private static AgentMessage Assistant(string id) => new AssistantAgentMessage(
        Content: string.Empty,
        ToolCalls: [new ToolCallContent(id, "tool", new Dictionary<string, object?>())],
        FinishReason: StopReason.ToolUse);

    private static AgentMessage ToolResult(string id) => new ToolResultAgentMessage(
        id, "tool", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));

    [Fact]
    public void CompactForOverflow_NaiveCutLandingOnToolResult_MovesTheBoundaryForward()
    {
        // 30 messages => keep = max(8, 10) = 10 => naive start index 20. Place a tool result there
        // (and at 21) so the raw cut would strand two orphans; the aligned cut must skip both.
        var messages = new List<AgentMessage>();
        for (var i = 0; i < 30; i++)
        {
            messages.Add(i is 20 or 21 ? ToolResult($"tc-{i}") : User($"m{i}"));
        }

        messages[20].ShouldBeOfType<ToolResultAgentMessage>(
            "precondition: the naive cut index must land on a tool result, else the test is vacuous");

        var result = AgentLoopRunner.CompactForOverflow(messages);

        result[0].ShouldNotBeOfType<ToolResultAgentMessage>();
        result.Count.ShouldBe(8); // two stranded results skipped
        result[0].ShouldBe(messages[22]);
    }

    [Fact]
    public void CompactForOverflow_RetainedTail_NeverStartsWithAToolResult()
    {
        // Sweep every transcript length that takes the truncation branch, with tool results at every
        // position, so no arrangement of the cut can produce a leading orphan.
        for (var count = 13; count <= 60; count++)
        {
            for (var toolResultEvery = 2; toolResultEvery <= 4; toolResultEvery++)
            {
                var messages = new List<AgentMessage>();
                for (var i = 0; i < count; i++)
                {
                    messages.Add(i % toolResultEvery == 0 ? ToolResult($"tc-{i}") : User($"m{i}"));
                }

                var result = AgentLoopRunner.CompactForOverflow(messages);

                if (result.Count > 0)
                {
                    result[0].ShouldNotBeOfType<ToolResultAgentMessage>(
                        $"count={count} toolResultEvery={toolResultEvery} produced a leading orphan");
                }
            }
        }
    }

    [Fact]
    public void CompactForOverflow_NaiveCutAlreadyOnANonToolResult_IsUnchanged()
    {
        // Behaviour parity: when the raw index is already a valid boundary the aligned cut must not
        // move it. Without this, "always skip one" would pass the orphan tests while silently
        // discarding a message per compaction.
        var messages = new List<AgentMessage>();
        for (var i = 0; i < 30; i++)
        {
            messages.Add(User($"m{i}"));
        }

        var result = AgentLoopRunner.CompactForOverflow(messages);

        result.Count.ShouldBe(10);
        result[0].ShouldBe(messages[20]);
    }

    [Fact]
    public void CompactForOverflow_ShortTranscript_IsReturnedWholeEvenWhenItStartsWithAToolResult()
    {
        // The <= 12 short-circuit predates this fix and is deliberately untouched: nothing is dropped
        // there, so no call can be stranded BY compaction. A pre-existing orphan in the caller's list
        // is still handled downstream by the shared MessageTransformer seam.
        var messages = new List<AgentMessage> { ToolResult("tc-0"), User("m1") };

        var result = AgentLoopRunner.CompactForOverflow(messages);

        result.Count.ShouldBe(2);
        result[0].ShouldBeOfType<ToolResultAgentMessage>();
        result.ShouldNotBeSameAs(messages); // still a copy, not an alias
    }

    [Fact]
    public void CompactForOverflow_AllRetainedMessagesAreToolResults_ReturnsEmptyRatherThanAnOrphan()
    {
        // Sad path: every candidate in the retained window is a tool result, so alignment runs off the
        // end. Returning nothing is correct - an empty list is a valid request shape, whereas a
        // leading orphan is a guaranteed 400.
        var messages = new List<AgentMessage>();
        for (var i = 0; i < 13; i++)
        {
            messages.Add(i < 5 ? User($"m{i}") : ToolResult($"tc-{i}"));
        }

        var result = AgentLoopRunner.CompactForOverflow(messages);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CompactForOverflow_PreservesTheOrderAndIdentityOfRetainedMessages()
    {
        // Non-vacuity guard: the alignment must only move the START index. A mutation that reordered
        // or filtered the tail would keep the orphan assertions green.
        var messages = new List<AgentMessage>();
        for (var i = 0; i < 30; i++)
        {
            messages.Add(i == 20 ? ToolResult($"tc-{i}") : Assistant($"tc-{i}"));
        }

        var result = AgentLoopRunner.CompactForOverflow(messages);

        result.ShouldBe(messages.Skip(21).ToList());
    }
}
