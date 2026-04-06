using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Tui;

/// <summary>
/// Terminal UI channel adapter for local console I/O.
/// </summary>
/// <remarks>
/// Phase 2 stub: this implementation only tracks lifecycle state and writes outbound
/// content to <see cref="Console.Out"/>. A full implementation would run a background
/// input loop, translate console input into <see cref="InboundMessage"/> instances,
/// and dispatch them through the registered <see cref="IChannelDispatcher"/>.
/// </remarks>
public sealed class TuiChannelAdapter(ILogger<TuiChannelAdapter> logger) : ChannelAdapterBase(logger)
{
    private readonly ILogger<TuiChannelAdapter> _logger = logger;

    /// <summary>
    /// Gets the channel type identifier.
    /// </summary>
    public override string ChannelType => "tui";

    /// <summary>
    /// Gets the human-readable channel display name.
    /// </summary>
    public override string DisplayName => "Terminal UI";

    /// <summary>
    /// Gets a value indicating whether this channel supports streaming deltas.
    /// </summary>
    public override bool SupportsStreaming => true;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => true;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => true;

    /// <inheritdoc />
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("{DisplayName} channel adapter stub started", DisplayName);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("{DisplayName} channel adapter stub stopped", DisplayName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a complete outbound message to the terminal.
    /// </summary>
    /// <param name="message">Outbound message to render.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    /// <remarks>
    /// Phase 2 stub: writes directly to stdout. A full implementation would route output
    /// through structured terminal rendering components.
    /// </remarks>
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRunning)
        {
            _logger.LogDebug("{DisplayName} send requested while adapter is not running", DisplayName);
        }

        return Console.Out.WriteLineAsync($"[{DisplayName}:{message.ConversationId}] {message.Content}");
    }

    /// <summary>
    /// Sends a streaming delta to the terminal without appending a newline.
    /// </summary>
    /// <param name="conversationId">Target conversation identifier.</param>
    /// <param name="delta">Streaming text delta.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when the delta has been written.</returns>
    /// <remarks>
    /// Phase 2 stub: writes deltas directly to stdout for quick validation of streaming paths.
    /// </remarks>
    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRunning)
        {
            _logger.LogDebug("{DisplayName} stream delta requested while adapter is not running", DisplayName);
        }

        return Console.Out.WriteAsync(delta);
    }
}
