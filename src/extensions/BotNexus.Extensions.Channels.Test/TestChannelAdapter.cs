using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// In-memory test channel adapter for integration testing of multi-channel scenarios.
/// Stores inbound/outbound messages and captured log entries in memory.
/// Exposes HTTP endpoints via <see cref="TestChannelEndpoints"/> for test orchestration.
/// </summary>
/// <remarks>
/// This adapter is opt-in only. It must be explicitly registered and should never
/// be loaded in production configurations. The <c>botnexus-extension.json</c> manifest
/// has <c>"enabled": false</c> to prevent accidental auto-loading.
/// </remarks>
public sealed class TestChannelAdapter : ChannelAdapterBase
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<OutboundMessage>> _outbound =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, System.Text.StringBuilder> _streamBuffers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<TestLogEntry> _logs = new();

    public TestChannelAdapter(ILogger<TestChannelAdapter> logger) : base(logger)
    {
    }

    /// <inheritdoc />
    public override ChannelKey ChannelType => ChannelKey.From("test");

    /// <inheritdoc />
    public override string DisplayName => "Test Channel";

    /// <inheritdoc />
    public override bool SupportsStreaming => true;

    /// <summary>
    /// Enqueues an outbound message to the per-channel queue.
    /// </summary>
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var queue = _outbound.GetOrAdd(message.ChannelAddress.Value, _ => new ConcurrentQueue<OutboundMessage>());
        queue.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Accumulates streaming deltas per conversation. Deltas are buffered and
    /// exposed via <see cref="GetOutbound"/> as a single synthetic message on flush.
    /// </summary>
    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
    {
        var buf = _streamBuffers.GetOrAdd(conversationId, _ => new System.Text.StringBuilder());
        lock (buf)
            buf.Append(delta);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Flushes any accumulated streaming buffer for <paramref name="conversationId"/>
    /// into the outbound queue and clears the buffer.
    /// </summary>
    public void FlushStreamBuffer(string conversationId)
    {
        if (!_streamBuffers.TryGetValue(conversationId, out var buf))
            return;

        string content;
        lock (buf)
        {
            content = buf.ToString();
            buf.Clear();
        }

        if (string.IsNullOrEmpty(content))
            return;

        var queue = _outbound.GetOrAdd(conversationId, _ => new ConcurrentQueue<OutboundMessage>());
        queue.Enqueue(new OutboundMessage
        {
            ChannelType = ChannelType,
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = content,
            Metadata = new Dictionary<string, object?> { ["source"] = "stream-flush" }
        });
    }

    /// <summary>
    /// Returns and dequeues all outbound messages for the given channel address.
    /// </summary>
    public IReadOnlyList<OutboundMessage> GetOutbound(string channelId)
    {
        if (!_outbound.TryGetValue(channelId, out var queue))
            return [];

        var result = new List<OutboundMessage>();
        while (queue.TryDequeue(out var msg))
            result.Add(msg);
        return result;
    }

    /// <summary>
    /// Clears all outbound messages for the given channel address.
    /// </summary>
    public void ClearOutbound(string channelId)
    {
        if (_outbound.TryGetValue(channelId, out var queue))
            while (queue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Returns all captured log entries (does not clear).
    /// </summary>
    public IReadOnlyList<TestLogEntry> GetLogs()
    {
        return _logs.ToArray();
    }

    /// <summary>
    /// Clears the log buffer.
    /// </summary>
    public void ClearLogs()
    {
        while (_logs.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Injects an inbound message into the gateway pipeline as if it arrived on this channel.
    /// </summary>
    public Task InjectInboundAsync(
        string channelId,
        string content,
        string senderId,
        string? targetAgentId = null,
        CancellationToken cancellationToken = default)
    {
        var message = new InboundMessage
        {
            ChannelType = ChannelType,
            ChannelAddress = ChannelAddress.From(channelId),
            SenderId = senderId,
            Content = content,
            TargetAgentId = targetAgentId
        };

        return DispatchInboundAsync(message, cancellationToken);
    }

    /// <summary>
    /// Captures a log entry into the internal log buffer.
    /// Called by <see cref="TestChannelLoggerProvider"/>.
    /// </summary>
    public void CaptureLog(TestLogEntry entry) => _logs.Enqueue(entry);

    /// <inheritdoc />
    protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        _outbound.Clear();
        _streamBuffers.Clear();
        while (_logs.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }
}
