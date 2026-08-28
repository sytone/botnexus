using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// #3564: the shared Messages-API stop-reason mapper is total and carries the diagnostic string.
/// </summary>
public class MessagesStopReasonMapTests
{
    // AC4. The unrecognised value is observable in the returned message, not only in a log line -
    // so it survives onto the persisted assistant message and is diagnosable without a debugger.
    [Fact]
    public void Map_UnknownReason_RetainsTheReasonInTheMessage()
    {
        var (stopReason, message) = MessagesStopReasonMap.Map("compaction_boundary", "Anthropic");

        stopReason.ShouldBe(StopReason.Error);
        message.ShouldBe("Provider stop_reason: compaction_boundary");
    }

    // AC1/AC2. Total by construction: no input throws, including null and the empty string.
    [Theory]
    [InlineData("compaction_boundary")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Map_NeverThrows(string? reason)
    {
        var act = () => MessagesStopReasonMap.Map(reason, "Anthropic");

        act.ShouldNotThrow();
    }

    // AC1. Absent reason is a clean stop with no error message attached.
    [Fact]
    public void Map_Null_IsStopWithNoMessage()
    {
        var (stopReason, message) = MessagesStopReasonMap.Map(null, "Anthropic");

        stopReason.ShouldBe(StopReason.Stop);
        message.ShouldBeNull();
    }

    // Non-vacuity: recognised reasons carry no error message, so the AC4 assertion above cannot
    // pass by the mapper stamping a message onto everything.
    [Fact]
    public void Map_RecognisedReason_HasNoErrorMessage()
    {
        var (stopReason, message) = MessagesStopReasonMap.Map("end_turn", "Anthropic");

        stopReason.ShouldBe(StopReason.Stop);
        message.ShouldBeNull();
    }

    // The empty string is not "absent" - it is an unrecognised value and must classify as Error,
    // otherwise a provider emitting "" would silently look like a normal completion.
    [Fact]
    public void Map_EmptyString_IsError()
    {
        MessagesStopReasonMap.Map("", "Anthropic").StopReason.ShouldBe(StopReason.Error);
    }
}
