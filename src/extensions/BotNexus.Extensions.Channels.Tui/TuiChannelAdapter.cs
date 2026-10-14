using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Domain.Gateway.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Tui;

/// <summary>
/// Terminal UI channel adapter for local console I/O.
/// </summary>
public sealed class TuiChannelAdapter
    : ChannelAdapterBase, IStreamEventChannelAdapter, IConversationEventSink
{
    private readonly ILogger<TuiChannelAdapter> _logger;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private CancellationTokenSource? _inputLoopCancellation;
    private Task? _inputLoopTask;

    /// <summary>
    /// Initializes a terminal adapter over the process console.
    /// </summary>
    public TuiChannelAdapter(ILogger<TuiChannelAdapter> logger)
        : this(logger, Console.In, Console.Out)
    {
    }

    internal TuiChannelAdapter(
        ILogger<TuiChannelAdapter> logger,
        TextReader input,
        TextWriter output)
        : base(logger)
    {
        _logger = logger;
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Gets the channel type identifier.
    /// </summary>
    public override ChannelKey ChannelType => ChannelKey.From("tui");

    /// <summary>
    /// Gets the human-readable channel display name.
    /// </summary>
    public override string DisplayName => "Terminal UI";

    /// <summary>
    /// Gets a value indicating whether this channel supports streaming deltas.
    /// </summary>
    public override bool SupportsStreaming => true;

    /// <inheritdoc />
    public override bool SupportsSteering => true;

    /// <inheritdoc />
    public override bool SupportsFollowUp => false;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => true;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => true;

    /// <summary>
    /// The terminal UI renders directly to the operator's console - a user-visible surface - so
    /// the delimited internal runtime-context envelope is redacted before write (#1430).
    /// </summary>
    protected override bool StripsRuntimeContext => true;

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

        return _output.WriteLineAsync($"[{DisplayName}:{message.ChannelAddress.Value}] {ProjectOutboundText(message.Content)}");
    }

    /// <summary>
    /// Sends a streaming delta to the terminal without appending a newline.
    /// </summary>
    /// <param name="target">Typed stream target — TUI uses <c>target.SessionId</c> only as a display label.</param>
    /// <param name="delta">Streaming text delta.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when the delta has been written.</returns>
    public override Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRunning)
        {
            _logger.LogDebug("{DisplayName} stream delta requested while adapter is not running", DisplayName);
        }

        return _output.WriteAsync(delta);
    }

    /// <summary>
    /// Sends a structured stream event to the terminal.
    /// </summary>
    /// <param name="target">Typed stream target — TUI uses <c>target.SessionId</c> only as a display label.</param>
    /// <param name="streamEvent">Structured stream event payload.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when the event has been rendered.</returns>
    public Task SendStreamEventAsync(
        ChannelStreamTarget target,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var label = target.SessionId.Value;
        return streamEvent.Type switch
        {
            AgentStreamEventType.ContentDelta when streamEvent.ContentDelta is not null
                => SendStreamDeltaAsync(target, streamEvent.ContentDelta, cancellationToken),
            AgentStreamEventType.ThinkingDelta when streamEvent.ThinkingContent is not null
                => _output.WriteAsync($"\n💭 {streamEvent.ThinkingContent}"),
            AgentStreamEventType.ToolStart when streamEvent.ToolName is not null
                => _output.WriteLineAsync($"\n{ToolGlyphs.ForTool(streamEvent.ToolName)} [{DisplayName}:{label}] Tool start: {streamEvent.ToolName}"),
            AgentStreamEventType.ToolEnd
                => _output.WriteLineAsync($"\n{(streamEvent.ToolIsError == true ? "\u26A0\uFE0F" : ToolGlyphs.ForTool(streamEvent.ToolName))} [{DisplayName}:{label}] Tool complete: {streamEvent.ToolName ?? streamEvent.ToolCallId ?? "unknown"}"),
            AgentStreamEventType.Error when streamEvent.ErrorMessage is not null
                => _output.WriteLineAsync($"\n❌ [{DisplayName}:{label}] {streamEvent.ErrorMessage}"),
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
                line = await _input.ReadLineAsync(cancellationToken);
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

            if (trimmed.StartsWith("/steer", StringComparison.OrdinalIgnoreCase))
            {
                var steerContent = trimmed.Length > "/steer".Length
                    ? trimmed["/steer".Length..].TrimStart()
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(steerContent))
                {
                    await _output.WriteLineAsync("↪ Usage: /steer <message>");
                    continue;
                }

                await _output.WriteLineAsync($"↪ [{DisplayName}:console] Steering submitted.");
                await DispatchInboundAsync(new InboundMessage
                {
                    ChannelType = ChannelType,
                    SenderId = Environment.UserName,
                    Sender = CitizenId.Of(UserId.From(Environment.UserName)),
                    ChannelAddress = ChannelAddress.From("console"),
                    // The conversation router resolves the (tui, console) binding to the active
                    // conversation and session. TUI declares delivery intent without selecting a
                    // second GatewayHost control path or fabricating a routing identity.
                    Content = steerContent,
                    RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                        targetAgentId: null,
                        sessionId: null,
                        conversationId: null,
                        deliveryMode: InboundDeliveryMode.Steer)
                }, cancellationToken);
                continue;
            }

            await DispatchInboundAsync(new InboundMessage
            {
                ChannelType = ChannelType,
                SenderId = Environment.UserName,
                Sender = CitizenId.Of(UserId.From(Environment.UserName)),
                ChannelAddress = ChannelAddress.From("console"),
                // PR2 of W-5 (#691): no RoutingHints — see comment in the steer branch.
                Content = line
            }, cancellationToken);
        }
    }
    /// <inheritdoc />
    public async Task OnConversationEventAsync(
        ConversationEvent conversationEvent,
        CancellationToken cancellationToken = default)
    {
        if (conversationEvent is not ConversationAgentEvent agentEvent)
            return;

        foreach (var target in ConversationEventStreamRouting.GetTargets(
                     conversationEvent, ChannelType, ((IChannelAdapter)this).AdapterId))
        {
            if (((IStreamEventChannelAdapter)this).CanSendStreamEvent(target))
            {
                await SendStreamEventAsync(target, agentEvent.StreamEvent, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

}
