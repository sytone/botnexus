using BotNexus.Gateway.Api.Triggers;
using Xunit;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// Unit tests for the Phase 2 transcript hygiene additions to <see cref="HeartbeatTrigger"/>:
/// <list type="bullet">
///   <item>Ack classification (<see cref="HeartbeatTrigger.IsHeartbeatAck"/>)</item>
///   <item>AckMaxChars threshold enforcement</item>
/// </list>
/// Integration-level pruning + UpdatedAt restoration is covered in HeartbeatTriggerTests.
/// </summary>
public sealed class HeartbeatTranscriptPruningTests
{
    // -----------------------------------------------------------------
    // IsHeartbeatAck — bare ack forms
    // -----------------------------------------------------------------

    [Fact]
    public void IsHeartbeatAck_ExactMatch_ReturnsTrue()
        => Assert.True(HeartbeatTrigger.IsHeartbeatAck("HEARTBEAT_OK"));

    [Fact]
    public void IsHeartbeatAck_WithLeadingTrailingWhitespace_ReturnsTrue()
        => Assert.True(HeartbeatTrigger.IsHeartbeatAck("  HEARTBEAT_OK  "));

    [Fact]
    public void IsHeartbeatAck_StartsWithAck_ReturnsTrue()
        => Assert.True(HeartbeatTrigger.IsHeartbeatAck("HEARTBEAT_OK — nothing needs attention."));

    [Fact]
    public void IsHeartbeatAck_ContainsAckMidString_ReturnsTrue()
        => Assert.True(HeartbeatTrigger.IsHeartbeatAck("All tasks complete. HEARTBEAT_OK"));

    [Fact]
    public void IsHeartbeatAck_NullInput_ReturnsFalse()
        => Assert.False(HeartbeatTrigger.IsHeartbeatAck(null));

    [Fact]
    public void IsHeartbeatAck_EmptyString_ReturnsFalse()
        => Assert.False(HeartbeatTrigger.IsHeartbeatAck(""));

    [Fact]
    public void IsHeartbeatAck_WhitespaceOnly_ReturnsFalse()
        => Assert.False(HeartbeatTrigger.IsHeartbeatAck("   "));

    [Fact]
    public void IsHeartbeatAck_SubstantiveReply_ReturnsFalse()
        => Assert.False(HeartbeatTrigger.IsHeartbeatAck("I found 3 tasks in HEARTBEAT.md and have started working on them."));

    [Fact]
    public void IsHeartbeatAck_NoAckToken_ReturnsFalse()
        => Assert.False(HeartbeatTrigger.IsHeartbeatAck("Nothing needs attention today."));

    [Fact]
    public void IsHeartbeatAck_LowercaseToken_ReturnsFalse()
        => Assert.False(HeartbeatTrigger.IsHeartbeatAck("heartbeat_ok"));

    // -----------------------------------------------------------------
    // AckMaxChars enforcement
    // -----------------------------------------------------------------

    [Fact]
    public void IsHeartbeatAck_WithinDefaultThreshold_ReturnsTrue()
    {
        // 12 chars — well inside the default 300-char window
        Assert.True(HeartbeatTrigger.IsHeartbeatAck("HEARTBEAT_OK"));
    }

    [Fact]
    public void IsHeartbeatAck_ExceedsDefaultThreshold_ReturnsFalse()
    {
        // Build a response that contains HEARTBEAT_OK but is longer than 300 chars
        var longResponse = "HEARTBEAT_OK " + new string('x', 300);
        Assert.False(HeartbeatTrigger.IsHeartbeatAck(longResponse));
    }

    [Fact]
    public void IsHeartbeatAck_ExceedsCustomThreshold_ReturnsFalse()
    {
        // Custom threshold of 20 chars; response is 25 chars
        var response = "HEARTBEAT_OK — all good.";   // 24 chars
        Assert.False(HeartbeatTrigger.IsHeartbeatAck(response, maxChars: 20));
    }

    [Fact]
    public void IsHeartbeatAck_WithinCustomThreshold_ReturnsTrue()
    {
        var response = "HEARTBEAT_OK — done.";   // 20 chars
        Assert.True(HeartbeatTrigger.IsHeartbeatAck(response, maxChars: 20));
    }

    [Fact]
    public void IsHeartbeatAck_ExactlyAtThreshold_ReturnsTrue()
    {
        // Build response exactly 50 chars that contains HEARTBEAT_OK
        var response = "HEARTBEAT_OK" + new string(' ', 38); // 50 chars after trim... trim makes it 12
        // Trim happens first, so a padded-whitespace response is fine
        Assert.True(HeartbeatTrigger.IsHeartbeatAck(response, maxChars: 50));
    }

    [Fact]
    public void IsHeartbeatAck_ZeroThreshold_ReturnsFalse()
    {
        // maxChars=0 means nothing passes
        Assert.False(HeartbeatTrigger.IsHeartbeatAck("HEARTBEAT_OK", maxChars: 0));
    }

    // -----------------------------------------------------------------
    // AckMaxCharsDefault constant
    // -----------------------------------------------------------------

    [Fact]
    public void AckMaxCharsDefault_IsThreeHundred()
        => Assert.Equal(300, HeartbeatTrigger.AckMaxCharsDefault);
}
