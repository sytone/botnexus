using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// #3564: <see cref="AnthropicProvider.MapStopReason"/> must be TOTAL. It previously ended in
/// <c>_ =&gt; throw new InvalidOperationException</c>, so any stop reason Anthropic added outside the
/// nine-entry literal list destroyed a turn that had otherwise fully succeeded.
/// </summary>
public class AnthropicUnknownStopReasonTests
{
    // AC1/AC3. The unknown value is NAMED in the assertion so the case is grepable: if Anthropic
    // ships "compaction_boundary" and someone maps it, this test states exactly what was exercised.
    [Fact]
    public void MapStopReason_UnknownReason_DoesNotThrow()
    {
        var act = () => AnthropicProvider.MapStopReason("compaction_boundary");

        act.ShouldNotThrow();
    }

    // AC1/AC4. Unknown maps to Error rather than being silently normalised to Stop, so the mapping
    // gap stays visible in persisted history instead of masquerading as a normal completion.
    [Theory]
    [InlineData("compaction_boundary")]
    [InlineData("model_context_window_exceeded")]
    [InlineData("totally_made_up_reason_3564")]
    public void MapStopReason_UnknownReason_IsErrorNotThrown(string unknownReason)
    {
        AnthropicProvider.MapStopReason(unknownReason).ShouldBe(StopReason.Error);
    }

    // AC1. A stream that ends without an explicit stop reason is an ordinary stop, not a failure.
    [Fact]
    public void MapStopReason_Null_IsStop()
    {
        AnthropicProvider.MapStopReason(null).ShouldBe(StopReason.Stop);
    }

    // AC5 / non-vacuity. Every previously recognised reason keeps its exact mapping - the fix must
    // only have replaced the throwing default arm, so "everything is Error" cannot pass above.
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
        AnthropicProvider.MapStopReason(reason).ShouldBe(expected);
    }
}
