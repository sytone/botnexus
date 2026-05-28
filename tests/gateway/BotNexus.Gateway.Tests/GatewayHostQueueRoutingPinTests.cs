using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// AC #5 pin for sub-PR 6.2 (#582): <see cref="GatewayHost"/>.<c>GetQueueKey</c> must route
/// by the typed <c>RequestedSessionId</c> when one is present, and fall back to the
/// <c>&lt;channelType&gt;:&lt;channelAddress&gt;</c> composite when not. This is the
/// behavioural safety net for sub-PR 6.3, which deletes the legacy
/// <c>InboundMessage.SessionId</c> string field: after deletion the only path that can
/// supply a session-id hint is the typed Vogen <see cref="SessionId"/>; if any future
/// change reverts <c>GetQueueKey</c> to read a string field that no longer exists, the
/// build will break — but if some intermediate change silently routes everything to the
/// channel-key fallback (e.g. by reading the wrong field), that's a silent regression
/// these tests catch.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetQueueKey</c> is <c>private static</c> on <see cref="GatewayHost"/> so we invoke it
/// via reflection. That is a deliberate trade-off: the alternative (making the method
/// internal + <c>InternalsVisibleTo</c>) would expand the public surface of a security-
/// sensitive type for the sake of a one-off migration pin. The migration is small (sub-PR
/// 6.3 will likely fold this method into the hint helper anyway) and reflection failure
/// here surfaces a contract change long before downstream queueing breaks in production.
/// </para>
/// </remarks>
public sealed class GatewayHostQueueRoutingPinTests
{
    [Fact]
    public void GetQueueKey_WithTypedSessionIdHint_UsesSessionIdValue()
    {
        var message = CreateMessage(sessionId: "sess-typed-1");

        var key = InvokeGetQueueKey(message);

        key.ShouldBe("sess-typed-1");
    }

    [Fact]
    public void GetQueueKey_WithNoSessionIdHint_FallsBackToChannelComposite()
    {
        var message = CreateMessage(sessionId: null);

        var key = InvokeGetQueueKey(message);

        key.ShouldBe("web:conv-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void GetQueueKey_WithEmptyOrWhitespaceSessionId_FallsBackToChannelComposite(string blank)
    {
        // Whitespace SessionId values collapse to "no hint" through the typed routing-hint
        // projection (InboundMessageRoutingHints normalises empty/whitespace to null) so
        // they route to the channel-key fallback rather than producing a whitespace-only
        // queue key (which would have collided unhelpfully with any other whitespace input).
        var message = CreateMessage(sessionId: blank);

        var key = InvokeGetQueueKey(message);

        key.ShouldBe("web:conv-1");
    }

    private static string InvokeGetQueueKey(InboundMessage message)
    {
        var method = typeof(GatewayHost).GetMethod(
            "GetQueueKey",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(InboundMessage)],
            modifiers: null);
        method.ShouldNotBeNull("GatewayHost.GetQueueKey(InboundMessage) must exist as a private static method — if you renamed or moved it, update this pin.");
        var result = method.Invoke(null, [message]);
        return result.ShouldBeOfType<string>();
    }

    private static InboundMessage CreateMessage(string? sessionId)
        => new()
        {
            ChannelType = ChannelKey.From("web"),
            ChannelAddress = ChannelAddress.From("conv-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hi",
            SessionId = sessionId
        };
}
