using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Behavioural pin for <see cref="DefaultInboundMessageOrchestrator"/>'s queue-key
/// derivation. Originally lived on <c>GatewayHost.GetQueueKey</c>; migrated to the
/// orchestrator as part of PR3 of W-5 (#696) when the per-session queue mechanics
/// moved out of <c>GatewayHost</c>.
/// </summary>
/// <remarks>
/// <para>
/// Contract: the orchestrator must route by the typed <c>RequestedSessionId</c> when one
/// is present and fall back to <c>&lt;channelType&gt;:&lt;channelAddress&gt;</c> otherwise.
/// Whitespace SessionIds collapse to "no hint" through <see cref="InboundMessageRoutingHints"/>
/// and therefore route to the channel fallback (not a whitespace-only queue key).
/// </para>
/// <para>
/// <c>GetQueueKey</c> is <c>private static</c> on the orchestrator; we invoke it via
/// reflection to avoid expanding the public surface for a single migration pin. A rename
/// or signature change will surface here long before downstream queueing breaks in
/// production.
/// </para>
/// </remarks>
public sealed class OrchestratorQueueRoutingPinTests
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
        var message = CreateMessage(sessionId: blank);

        var key = InvokeGetQueueKey(message);

        key.ShouldBe("web:conv-1");
    }

    private static string InvokeGetQueueKey(InboundMessage message)
    {
        var method = typeof(DefaultInboundMessageOrchestrator).GetMethod(
            "GetQueueKey",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(InboundMessage)],
            modifiers: null);
        method.ShouldNotBeNull("DefaultInboundMessageOrchestrator.GetQueueKey(InboundMessage) must exist as a private static method — if you renamed or moved it, update this pin.");
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
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null)
        };
}