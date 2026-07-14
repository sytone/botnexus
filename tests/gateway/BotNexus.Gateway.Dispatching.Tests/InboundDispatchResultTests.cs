using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Dispatching.Tests;

/// <summary>
/// Unit coverage for <see cref="InboundDispatchResult"/> - the transport-facing aggregate
/// outcome of inbound orchestration. Verifies each convenience factory pins the correct
/// <see cref="InboundDispatchStatus"/> and dispatch-list contract, so transports (SignalR
/// error frame, REST 503, ...) can branch on status without re-reading the per-agent list.
/// </summary>
public sealed class InboundDispatchResultTests
{
    [Fact]
    public void Accepted_CarriesSuppliedDispatches_AndAcceptedStatus()
    {
        var dispatches = new List<DispatchResult>();

        var result = InboundDispatchResult.Accepted(dispatches);

        Assert.Equal(InboundDispatchStatus.Accepted, result.Status);
        Assert.Same(dispatches, result.Dispatches);
    }

    [Fact]
    public void NoRoute_HasNoRouteStatus_AndEmptyDispatches()
    {
        var result = InboundDispatchResult.NoRoute();

        Assert.Equal(InboundDispatchStatus.NoRoute, result.Status);
        Assert.Empty(result.Dispatches);
    }

    [Fact]
    public void Busy_HasBusyStatus_AndEmptyDispatches()
    {
        var result = InboundDispatchResult.Busy();

        Assert.Equal(InboundDispatchStatus.Busy, result.Status);
        Assert.Empty(result.Dispatches);
    }

    [Fact]
    public void Rejected_HasRejectedStatus_AndEmptyDispatches()
    {
        var result = InboundDispatchResult.Rejected();

        Assert.Equal(InboundDispatchStatus.Rejected, result.Status);
        Assert.Empty(result.Dispatches);
    }

    [Fact]
    public void EmptyFactories_ShareTheSameEmptyDispatchListInstance()
    {
        // Sanity: the empty-outcome factories reuse a single cached empty list rather than
        // allocating a fresh one each call.
        Assert.Same(InboundDispatchResult.NoRoute().Dispatches, InboundDispatchResult.Busy().Dispatches);
    }

    [Fact]
    public void Records_WithSameValues_AreEqual()
    {
        var a = InboundDispatchResult.NoRoute();
        var b = InboundDispatchResult.NoRoute();
        Assert.Equal(a, b);
    }
}
