using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>Excludes ordinary inbound admission for one empty canonical queue until disposed.</summary>
/// <remarks>Does not stop an agent or prove owned command termination. One same-key message may be
/// posted through this reservation; processing starts only after disposal. Failed posts remain unsent.</remarks>
public interface IInboundIdleReservation : IDisposable
{
    /// <summary>Enqueues at most one same-key message; returns false after disposal or prior success.</summary>
    bool TryPost(InboundMessage message);

    /// <summary>Enqueues one producer-owned message. Unsupported reservations refuse without sending.</summary>
    bool TryPost(InboundMessage message, InboundExecutionControl executionControl) => false;

    /// <summary>Actual processor completion for the owned post, or null before admission/when unsupported.
    /// A returned task is not evidence that processing started or finished until observed.</summary>
    Task<InboundDispatchResult>? Completion => null;
}
