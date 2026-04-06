using System.Diagnostics;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Channels.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Core;

/// <summary>
/// Base class for channel adapters that provides common lifecycle management,
/// allow-list enforcement, and logging.
/// </summary>
/// <remarks>
/// Subclasses implement <see cref="OnStartAsync"/> and <see cref="OnStopAsync"/>
/// for protocol-specific connection management, and override <see cref="SendAsync"/>
/// and <see cref="SendStreamDeltaAsync"/> for outbound message delivery.
/// </remarks>
public abstract class ChannelAdapterBase : IChannelAdapter
{
    protected readonly ILogger Logger;
    private IChannelDispatcher? _dispatcher;
    private bool _isRunning;

    /// <summary>
    /// Allow-list of sender IDs. If empty, all senders are allowed.
    /// </summary>
    protected IReadOnlyList<string> AllowList { get; init; } = [];

    protected ChannelAdapterBase(ILogger logger) => Logger = logger;

    /// <inheritdoc />
    public abstract string ChannelType { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public virtual bool SupportsStreaming => false;

    /// <inheritdoc />
    public virtual bool SupportsSteering => false;

    /// <inheritdoc />
    public virtual bool SupportsFollowUp => false;

    /// <inheritdoc />
    public virtual bool SupportsThinkingDisplay => false;

    /// <inheritdoc />
    public virtual bool SupportsToolDisplay => false;

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public async Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.start", ActivityKind.Server);
        activity?.SetTag("botnexus.channel.type", ChannelType);
        activity?.SetTag("botnexus.correlation.id", Activity.Current?.TraceId.ToString());

        _dispatcher = dispatcher;
        await OnStartAsync(cancellationToken);
        _isRunning = true;
        Logger.LogInformation("Channel adapter '{ChannelType}' started", ChannelType);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.stop", ActivityKind.Internal);
        activity?.SetTag("botnexus.channel.type", ChannelType);
        activity?.SetTag("botnexus.correlation.id", Activity.Current?.TraceId.ToString());

        await OnStopAsync(cancellationToken);
        _isRunning = false;
        _dispatcher = null;
        Logger.LogInformation("Channel adapter '{ChannelType}' stopped", ChannelType);
    }

    /// <inheritdoc />
    public abstract Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatches an inbound message to the Gateway routing pipeline.
    /// Checks the allow-list before dispatching.
    /// </summary>
    protected async Task DispatchInboundAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (AllowList.Count > 0 && !AllowList.Contains(message.SenderId))
        {
            Logger.LogDebug("Blocked message from '{SenderId}' — not in allow list for '{ChannelType}'", message.SenderId, ChannelType);
            return;
        }

        if (_dispatcher is null)
        {
            Logger.LogWarning("Channel '{ChannelType}' received message but no dispatcher is registered", ChannelType);
            return;
        }

        await _dispatcher.DispatchAsync(message, cancellationToken);
    }

    /// <summary>
    /// Protocol-specific startup logic. Called by <see cref="StartAsync"/>.
    /// </summary>
    protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Protocol-specific shutdown logic. Called by <see cref="StopAsync"/>.
    /// </summary>
    protected abstract Task OnStopAsync(CancellationToken cancellationToken);
}
