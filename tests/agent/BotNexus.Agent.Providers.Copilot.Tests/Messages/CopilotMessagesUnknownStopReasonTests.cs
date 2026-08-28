using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// #3564: <see cref="CopilotMessagesProvider.MapStopReason"/> must be TOTAL. It carried a verbatim
/// copy of the Anthropic switch, including the <c>_ =&gt; throw new InvalidOperationException</c>
/// default arm, so an unrecognised stop reason lost an otherwise-successful turn.
/// </summary>
public class CopilotMessagesUnknownStopReasonTests
{
    // AC2/AC3. Unknown value named in the assertion so the case is grepable.
    [Fact]
    public void MapStopReason_UnknownReason_DoesNotThrow()
    {
        var act = () => CopilotMessagesProvider.MapStopReason("compaction_boundary");

        act.ShouldNotThrow();
    }

    // AC2/AC4. Unknown classifies as Error, keeping the gap visible rather than faking a clean stop.
    [Theory]
    [InlineData("compaction_boundary")]
    [InlineData("model_context_window_exceeded")]
    [InlineData("totally_made_up_reason_3564")]
    public void MapStopReason_UnknownReason_IsErrorNotThrown(string unknownReason)
    {
        CopilotMessagesProvider.MapStopReason(unknownReason).ShouldBe(StopReason.Error);
    }

    // AC2. An absent stop reason is an ordinary stop.
    [Fact]
    public void MapStopReason_Null_IsStop()
    {
        CopilotMessagesProvider.MapStopReason(null).ShouldBe(StopReason.Stop);
    }

    // AC5 / non-vacuity: recognised reasons are unchanged.
    [Theory]
    [InlineData("end_turn", StopReason.Stop)]
    [InlineData("max_tokens", StopReason.Length)]
    [InlineData("tool_use", StopReason.ToolUse)]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("pause_turn", StopReason.Stop)]
    [InlineData("stop_sequence", StopReason.Stop)]
    [InlineData("content_policy", StopReason.Sensitive)]
    [InlineData("safety", StopReason.Sensitive)]
    [InlineData("sensitive", StopReason.Sensitive)]
    public void MapStopReason_RecognisedReasons_KeepTheirMapping(string reason, StopReason expected)
    {
        CopilotMessagesProvider.MapStopReason(reason).ShouldBe(expected);
    }

    // AC2 parity: the two providers now share ONE mapper, so they cannot drift again. Comparing
    // them to each other is what would fail if a future change re-privatised either switch.
    [Theory]
    [InlineData("end_turn")]
    [InlineData("tool_use")]
    [InlineData("sensitive")]
    [InlineData("compaction_boundary")]
    [InlineData(null)]
    public void MapStopReason_AgreesWithAnthropic(string? reason)
    {
        CopilotMessagesProvider.MapStopReason(reason)
            .ShouldBe(BotNexus.Agent.Providers.Core.Streaming.MessagesStopReasonMap.MapStopReason(reason, "Anthropic"));
    }
}
