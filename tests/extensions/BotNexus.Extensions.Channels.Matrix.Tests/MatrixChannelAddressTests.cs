using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Tests for <see cref="MatrixChannelAddress"/>, the room/thread encoding folded into the opaque
/// <see cref="ChannelAddress"/>.
/// </summary>
public sealed class MatrixChannelAddressTests
{
    [Fact]
    public void Encode_RoomOnly_ProducesBareRoomId()
    {
        var address = MatrixChannelAddress.Encode("!abc123:example.com");

        address.Value.ShouldBe("!abc123:example.com");
    }

    [Fact]
    public void Encode_WithThread_AppendsThreadSegment()
    {
        var address = MatrixChannelAddress.Encode("!abc123:example.com", "$evt456");

        address.Value.ShouldBe("!abc123:example.com/thread:$evt456");
    }

    [Fact]
    public void Encode_BlankThread_IsTreatedAsNoThread()
    {
        // A whitespace thread id is a caller bug, not a distinct thread. Encoding it verbatim would
        // mint an address that decodes back to a thread id no event ever had.
        var address = MatrixChannelAddress.Encode("!abc123:example.com", "   ");

        address.Value.ShouldBe("!abc123:example.com");
    }

    [Fact]
    public void Encode_BlankRoomId_Throws() =>
        Should.Throw<ArgumentException>(() => MatrixChannelAddress.Encode("   "));

    [Fact]
    public void TryDecode_RoundTripsRoomAndThread()
    {
        var address = MatrixChannelAddress.Encode("!abc123:example.com", "$evt456");

        MatrixChannelAddress.TryDecode(address, out var roomId, out var threadId).ShouldBeTrue();

        roomId.ShouldBe("!abc123:example.com");
        threadId.ShouldBe("$evt456");
    }

    [Fact]
    public void TryDecode_BareRoom_ReturnsNullThread()
    {
        var address = ChannelAddress.From("!abc123:example.com");

        MatrixChannelAddress.TryDecode(address, out var roomId, out var threadId).ShouldBeTrue();

        roomId.ShouldBe("!abc123:example.com");
        threadId.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_TrailingSeparator_RecoversRoomAndNoThread()
    {
        // A hand-authored or legacy binding with an empty thread segment must still route to the
        // room root rather than being dropped entirely.
        var address = ChannelAddress.From("!abc123:example.com/thread:");

        MatrixChannelAddress.TryDecode(address, out var roomId, out var threadId).ShouldBeTrue();

        roomId.ShouldBe("!abc123:example.com");
        threadId.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_EmptyAddress_Fails()
    {
        MatrixChannelAddress.TryDecode(ChannelAddress.From(string.Empty), out var roomId, out var threadId)
            .ShouldBeFalse();

        roomId.ShouldBe(string.Empty);
        threadId.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_ThreadWithNoRoom_Fails()
    {
        // Without a room there is nowhere to deliver, so this must fail rather than yield a blank
        // room id that would later be sent to the homeserver.
        MatrixChannelAddress.TryDecode(ChannelAddress.From("/thread:$evt456"), out var roomId, out _)
            .ShouldBeFalse();

        roomId.ShouldBe(string.Empty);
    }
}
