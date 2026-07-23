using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Triggers;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// Unit coverage for the shared execution-layer tool-audit projection (#2127). Verifies that the
/// single sink every blocking trigger now uses produces the same lossless tool timeline the
/// interactive streaming path persists, including the interrupted/incomplete synthesis and the
/// tool-activity guard the heartbeat ack-prune relies on.
/// </summary>
public sealed class TriggerToolAuditProjectorTests
{
    [Fact]
    public void ProjectToolEntries_PreservesOrderIdNameArgsResultAndError()
    {
        var response = new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("call-1", "read", false, "{\"path\":\"a.txt\"}", "file body"),
                new AgentToolCallInfo("call-2", "web_fetch", true, "{\"url\":\"x\"}", "boom: timeout")
            ]
        };

        var rows = TriggerToolAuditProjector.ProjectToolEntries(response).ToArray();

        rows.Length.ShouldBe(2);
        rows.ShouldAllBe(r => r.Role == MessageRole.Tool);
        rows[0].ToolCallId.ShouldBe("call-1");
        rows[0].ToolName.ShouldBe("read");
        rows[0].ToolArgs.ShouldBe("{\"path\":\"a.txt\"}");
        rows[0].Content.ShouldBe("file body");
        rows[0].ToolIsError.ShouldBeFalse();
        rows[1].ToolCallId.ShouldBe("call-2");
        rows[1].ToolIsError.ShouldBeTrue();
        rows[1].Content.ShouldBe("boom: timeout");
    }

    [Fact]
    public void ProjectToolEntries_IncompleteCall_SynthesizesInterruptedErrorRow()
    {
        var response = new AgentResponse
        {
            Content = string.Empty,
            ToolCalls = [new AgentToolCallInfo("call-x", "deploy", true, "{}", null, IsIncomplete: true)]
        };

        var row = TriggerToolAuditProjector.ProjectToolEntries(response).ShouldHaveSingleItem();

        row.ToolCallId.ShouldBe("call-x");
        row.ToolIsError.ShouldBeTrue();
        row.Content.ShouldContain("did not complete");
    }

    [Fact]
    public void ProjectToolEntries_ErrorWithoutResult_UsesFailedPlaceholder()
    {
        var response = new AgentResponse
        {
            Content = "x",
            ToolCalls = [new AgentToolCallInfo("call-e", "shell", true)]
        };

        var row = TriggerToolAuditProjector.ProjectToolEntries(response).ShouldHaveSingleItem();

        row.Content.ShouldBe("Tool execution failed.");
    }

    [Fact]
    public void HasToolActivity_TrueOnlyWhenToolsRan()
    {
        TriggerToolAuditProjector.HasToolActivity(new AgentResponse { Content = "x" }).ShouldBeFalse();
        TriggerToolAuditProjector.HasToolActivity(new AgentResponse
        {
            Content = "x",
            ToolCalls = [new AgentToolCallInfo("c", "read", false)]
        }).ShouldBeTrue();
    }
}
