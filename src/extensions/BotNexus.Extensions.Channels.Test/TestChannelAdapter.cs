using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// A lightweight in-memory channel adapter for integration testing.
/// Does not connect to any external service — outbound messages and stream deltas
/// are captured in concurrent queues for test assertions.
/// </summary>
public sealed class TestChannelAdapter : ChannelAdapterBase
{
    /// <summary>
    /// Captured outbound messages sent via <see cref="SendAsync"/>.
    /// </summary>
    public ConcurrentQueue<OutboundMessage> DeliveredMessages { get; } = new();

    /// <summary>
    /// Captured stream deltas sent via <see cref="SendStreamDeltaAsync"/>.
    /// </summary>
    public ConcurrentQueue<(ChannelStreamTarget Target, string Delta)> DeliveredDeltas { get; } = new();

    public TestChannelAdapter(ILogger<TestChannelAdapter> logger) : base(logger)
    {
    }

    /// <inheritdoc />
    public override ChannelKey ChannelType => ChannelKey.From("test");

    /// <inheritdoc />
    public override string DisplayName => "Test Channel";

    /// <inheritdoc />
    public override bool SupportsStreaming => true;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => true;

    /// <summary>
    /// Injects an inbound message into the adapter as if it arrived from an external source.
    /// The adapter must be started before calling this method.
    /// </summary>
    /// <param name="message">The inbound message to inject.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the adapter is not running.</exception>
    public Task InjectMessageAsync(InboundMessage message, CancellationToken ct = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("Cannot inject messages before the adapter is started.");

        return DispatchInboundAsync(message, ct);
    }

    /// <inheritdoc />
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        DeliveredMessages.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        DeliveredDeltas.Enqueue((target, delta));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all captured outbound messages and stream deltas.
    /// </summary>
    public void ClearDelivered()
    {
        while (DeliveredMessages.TryDequeue(out _)) { }
        while (DeliveredDeltas.TryDequeue(out _)) { }
    }

    /// <inheritdoc />
    protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
