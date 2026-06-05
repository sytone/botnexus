using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Test fake that records every <see cref="InboundMessage"/> passed to
/// <see cref="AcceptAsync"/> and returns a configurable
/// <see cref="InboundDispatchResult"/>. Lets hub-level tests assert on the
/// shape of the message (sender, channel address, content, routing hints,
/// metadata) without staging a full <see cref="DefaultInboundMessageOrchestrator"/>
/// pipeline or per-test Moq setups.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe so concurrency-style tests (multiple Hub connections sharing a
/// single orchestrator instance) can assert on the cumulative call list.
/// </para>
/// <para>
/// The fake also implements <see cref="IChannelDispatcher"/> by delegating to
/// <see cref="AcceptAsync"/> — matching the production
/// <see cref="DefaultInboundMessageOrchestrator"/> which provides the same
/// adapter for back-compat with hosts that still inject the older interface.
/// Tests can therefore pass this fake wherever either interface is required.
/// </para>
/// </remarks>
public sealed class CapturingInboundMessageOrchestrator : IInboundMessageOrchestrator, IChannelDispatcher
{
    private readonly ConcurrentQueue<InboundMessage> _captured = new();
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    /// <summary>
    /// Snapshot of every message that has flowed through <see cref="AcceptAsync"/>
    /// since this fake was constructed, in invocation order.
    /// </summary>
    public IReadOnlyList<InboundMessage> Captured => _captured.ToArray();

    /// <summary>
    /// The result returned from every <see cref="AcceptAsync"/> call. Defaults to
    /// <see cref="InboundDispatchResult.Accepted"/> with an empty dispatch list.
    /// Set this to inject Busy / NoRoute / Rejected outcomes from a test.
    /// </summary>
    public InboundDispatchResult ResultToReturn { get; set; } =
        InboundDispatchResult.Accepted(EmptyDispatches);

    /// <summary>
    /// Records <paramref name="message"/> and returns <see cref="ResultToReturn"/>.
    /// </summary>
    public Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _captured.Enqueue(message);
        return Task.FromResult(ResultToReturn);
    }

    /// <summary>
    /// Back-compat alias for <see cref="AcceptAsync"/>. Returns
    /// <see cref="Task.CompletedTask"/> once the message has been recorded so
    /// callers that still depend on <see cref="IChannelDispatcher"/> see the
    /// same fire-and-forget semantics they would get from
    /// <see cref="DefaultInboundMessageOrchestrator"/>.
    /// </summary>
    public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        await AcceptAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget enqueue: records the message and returns <see langword="true"/>
    /// (always succeeds in the test fake — no capacity limit).
    /// </summary>
    public bool Post(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _captured.Enqueue(message);
        return true;
    }
}
