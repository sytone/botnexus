using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The connection indicator must distinguish "reconnecting" from "disconnected" so an outage in
/// progress is legible rather than looking like a frozen page (#2624, AC4).
/// </summary>
/// <remarks>
/// The raw <c>HubConnectionState</c> is <c>Disconnected</c> for the entire terminal-close re-dial
/// window, so it cannot express the difference on its own; the loop's state is passed in as a
/// parameter. These tests pin both renderings against the same underlying hub state.
/// </remarks>
public sealed class ConnectionStatusReconnectingTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_Reconnecting_while_redial_loop_is_active()
    {
        var hub = new GatewayHubConnection();

        var cut = _ctx.Render<ConnectionStatus>(p => p
            .Add(c => c.Hub, hub)
            .Add(c => c.Reconnecting, true));

        // U+2026 HORIZONTAL ELLIPSIS, matching the in-budget reconnecting label.
        cut.Find(".connection-label").TextContent.ShouldBe("Reconnecting\u2026");
        cut.Find(".connection-indicator").ClassList.ShouldContain("status-reconnecting");
        cut.Find(".connection-indicator").GetAttribute("title").ShouldBe("Reconnecting\u2026");
        // U+25CC DOTTED CIRCLE, not the U+25CB hollow "dead" circle.
        cut.Find(".connection-dot").TextContent.ShouldContain("\u25CC");
    }

    /// <summary>
    /// Same hub state, loop inactive: the indicator must still read "Disconnected". This is what
    /// makes the previous test meaningful rather than an unconditional relabel.
    /// </summary>
    [Fact]
    public void Shows_Disconnected_when_redial_loop_is_not_active()
    {
        var hub = new GatewayHubConnection();

        var cut = _ctx.Render<ConnectionStatus>(p => p
            .Add(c => c.Hub, hub)
            .Add(c => c.Reconnecting, false));

        cut.Find(".connection-label").TextContent.ShouldBe("Disconnected");
        cut.Find(".connection-indicator").ClassList.ShouldContain("status-disconnected");
        cut.Find(".connection-dot").TextContent.ShouldContain("\u25CB");
    }

    /// <summary>
    /// The parameter defaults to false, so every pre-existing call-site renders exactly as before.
    /// </summary>
    [Fact]
    public void Defaults_to_Disconnected_when_parameter_is_omitted()
    {
        var hub = new GatewayHubConnection();

        var cut = _ctx.Render<ConnectionStatus>(p => p.Add(c => c.Hub, hub));

        cut.Find(".connection-label").TextContent.ShouldBe("Disconnected");
        cut.Find(".connection-indicator").ClassList.ShouldContain("status-disconnected");
    }
}
