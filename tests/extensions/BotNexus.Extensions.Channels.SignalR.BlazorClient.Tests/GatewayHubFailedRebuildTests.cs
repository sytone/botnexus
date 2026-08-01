using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the hub-wrapper half of #2625: a failed rebuild must not leave the client permanently
/// unable to report a live connection, and connection-state handlers must not accumulate across
/// rebuilds.
/// </summary>
/// <remarks>
/// These use a real <see cref="GatewayHubConnection"/> pointed at an unroutable loopback port, so a
/// connect genuinely fails the way it does against a gateway that is mid-restart. No network is
/// reachable and no wall-clock duration is asserted.
/// </remarks>
public sealed class GatewayHubFailedRebuildTests
{
    // Port 1 on loopback: reliably refuses, no external dependency, no DNS.
    private const string UnreachableHubUrl = "http://127.0.0.1:1/hub/gateway";

    /// <summary>
    /// AC2. Every failed rebuild must leave the wrapper in the clean no-connection state, so a later
    /// attempt against a healthy gateway can still establish a connection. The pre-fix code retained
    /// the never-started <c>HubConnection</c>, which SignalR never runs automatic reconnect on --
    /// leaving an object that reports Disconnected forever.
    /// </summary>
    [Fact]
    public async Task FailedConnect_LeavesWrapperAbleToConnectAgain()
    {
        await using var hub = new GatewayHubConnection();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Should.ThrowAsync<Exception>(() => hub.ConnectAsync(UnreachableHubUrl, "mobile"));

            // Never wedged into a state that claims a connection it does not have.
            hub.IsConnected.ShouldBeFalse();
            hub.State.ShouldBe(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected);

            // The probe reports "not alive" so the resume path takes the rebuild branch -- which is
            // only useful because the next ConnectAsync builds a fresh connection rather than
            // re-dialling a dead handle.
            (await hub.ProbeAsync(CancellationToken.None)).ShouldBeFalse();
        }
    }

    /// <summary>
    /// The failure must be observable to the caller rather than silently swallowed, so the retry loop
    /// knows to back off instead of believing a connection was established.
    /// </summary>
    [Fact]
    public async Task FailedConnect_RethrowsSoCallersTreatItAsAFailedAttempt()
    {
        await using var hub = new GatewayHubConnection();

        await Should.ThrowAsync<Exception>(() => hub.ConnectAsync(UnreachableHubUrl, "mobile"));
    }

    /// <summary>
    /// StopAndDispose after a failed connect must be safe and idempotent -- the resume path calls it
    /// unconditionally at the head of every rebuild.
    /// </summary>
    [Fact]
    public async Task StopAndDispose_AfterFailedConnect_IsSafe()
    {
        await using var hub = new GatewayHubConnection();

        await Should.ThrowAsync<Exception>(() => hub.ConnectAsync(UnreachableHubUrl, "mobile"));

        await hub.StopAndDisposeAsync();
        await hub.StopAndDisposeAsync();

        hub.IsConnected.ShouldBeFalse();
    }

    /// <summary>
    /// AC4, at the level this can be observed without a live gateway. The connection-state events
    /// live on the <em>wrapper</em>, which a rebuild does not replace, so the pre-fix code's fresh
    /// per-rebuild lambdas could never be unsubscribed and accumulated one registration per rebuild.
    /// The fix registers a named method and unsubscribes before subscribing. This pins the exact
    /// property that makes that work: repeating unsubscribe-then-subscribe on the wrapper's real
    /// event always collapses to a single registration, so one transition raises exactly once.
    /// </summary>
    /// <remarks>
    /// The counting handler here stands in for <c>PortalLoadService.RaiseConnectionStateChanged</c>;
    /// asserting the same across N <em>successful</em> rebuilds inside PortalLoadService would need a
    /// reachable gateway to negotiate against, which a unit test cannot provide.
    /// </remarks>
    [Fact]
    public void NamedHandler_SubscribedRepeatedly_CollapsesToASingleRegistration()
    {
        var hub = new GatewayHubConnection();
        var calls = 0;
        void Handler() => calls++;

        // Five rebuilds' worth of the fixed wiring sequence.
        for (var i = 0; i < 5; i++)
        {
            hub.OnDisconnected -= Handler;
            hub.OnDisconnected += Handler;
        }

        hub.RaiseOnDisconnectedForTest();

        calls.ShouldBe(1, "after 5 rebuilds a single transition must raise exactly once, not 5 times");
    }

    /// <summary>
    /// The negative control that makes the test above meaningful: the pre-fix shape -- a distinct
    /// lambda per rebuild, never unsubscribed -- accumulates, so one transition raises N times.
    /// </summary>
    [Fact]
    public void FreshLambdaPerRebuild_Accumulates_WhichIsTheDefect()
    {
        var hub = new GatewayHubConnection();
        var calls = 0;

        for (var i = 0; i < 5; i++)
            hub.OnDisconnected += () => calls++;

        hub.RaiseOnDisconnectedForTest();

        calls.ShouldBe(5);
    }
}
