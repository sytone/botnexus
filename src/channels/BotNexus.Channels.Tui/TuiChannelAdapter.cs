using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Tui;

/// <summary>
/// Terminal UI channel adapter for local console I/O.
/// </summary>
public sealed class TuiChannelAdapter(ILogger<TuiChannelAdapter> logger)
    : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private readonly ILogger<TuiChannelAdapter> _logger = logger;
    private CancellationTokenSource? _inputLoopCancellation;
    private Task? _inputLoopTask;

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
    public override bool SupportsSteering => false;

    /// <inheritdoc />
    public override bool SupportsFollowUp => false;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => true;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => true;

    /// <inheritdoc />
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _inputLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inputLoopTask = Task.Run(() => RunInputLoopAsync(_inputLoopCancellation.Token), CancellationToken.None);
        _logger.LogInformation("{DisplayName} channel adapter started", DisplayName);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        _inputLoopCancellation?.Cancel();
        if (_inputLoopTask is not null)
        {
            try
            {
                await _inputLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _inputLoopCancellation?.Dispose();
        _inputLoopCancellation = null;
        _inputLoopTask = null;
        _logger.LogInformation("{DisplayName} channel adapter stopped", DisplayName);
    }

    /// <summary>
    /// Sends a complete outbound message to the terminal.
    /// </summary>
    /// <param name="message">Outbound message to render.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when the message has been written.</returns>
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
    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRunning)
        {
            _logger.LogDebug("{DisplayName} stream delta requested while adapter is not running", DisplayName);
        }

        return Console.Out.WriteAsync(delta);
    }

    /// <summary>
    /// Sends a structured stream event to the terminal.
    /// </summary>
    /// <param name="conversationId">Target conversation identifier.</param>
    /// <param name="streamEvent">Structured stream event payload.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when the event has been rendered.</returns>
    public Task SendStreamEventAsync(
        string conversationId,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return streamEvent.Type switch
        {
            AgentStreamEventType.ContentDelta when streamEvent.ContentDelta is not null
                => SendStreamDeltaAsync(conversationId, streamEvent.ContentDelta, cancellationToken),
            AgentStreamEventType.ThinkingDelta when streamEvent.ThinkingContent is not null
                => Console.Out.WriteAsync($"\n💭 {streamEvent.ThinkingContent}"),
            AgentStreamEventType.ToolStart when streamEvent.ToolName is not null
                => Console.Out.WriteLineAsync($"\n🔧 [{DisplayName}:{conversationId}] Tool start: {streamEvent.ToolName}"),
            AgentStreamEventType.ToolEnd
                => Console.Out.WriteLineAsync($"\n✅ [{DisplayName}:{conversationId}] Tool complete: {streamEvent.ToolCallId ?? "unknown"}"),
            AgentStreamEventType.Error when streamEvent.ErrorMessage is not null
                => Console.Out.WriteLineAsync($"\n❌ [{DisplayName}:{conversationId}] {streamEvent.ErrorMessage}"),
            _ => Task.CompletedTask
        };
    }

    private async Task RunInputLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
                continue;

            var trimmed = line.Trim();
            if (string.Equals(trimmed, "/quit", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{DisplayName} input loop received /quit", DisplayName);
                _inputLoopCancellation?.Cancel();
                break;
            }

            if (string.Equals(trimmed, "/clear", StringComparison.OrdinalIgnoreCase))
            {
                Console.Clear();
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            await DispatchInboundAsync(new InboundMessage
            {
                ChannelType = ChannelType,
                SenderId = Environment.UserName,
                ConversationId = "console",
                SessionId = "tui-console",
                Content = line
            }, cancellationToken);
        }
    }
}
