using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>Excludes ordinary inbound admission for one empty canonical queue until disposed.</summary>
/// <remarks>Does not stop an agent or prove owned command termination. One same-key message may be
/// posted through this reservation; processing starts only after disposal. Failed posts remain unsent.</remarks>
public interface IInboundIdleReservation : IDisposable
{
    /// <summary>Enqueues at most one same-key message; returns false after disposal or prior success.</summary>
    bool TryPost(InboundMessage message);
}
