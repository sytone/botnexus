using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Base;

/// <summary>Abstract base class for all channels providing common functionality.</summary>
public abstract class BaseChannel : IChannel
{
    protected readonly ILogger Logger;
    protected readonly IMessageBus MessageBus;
    protected readonly IReadOnlyList<string> AllowList;

    private bool _isRunning;

    protected BaseChannel(IMessageBus messageBus, ILogger logger, IReadOnlyList<string>? allowList = null)
    {
        MessageBus = messageBus;
        Logger = logger;
        AllowList = allowList ?? [];
    }

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public bool IsRunning => _isRunning;

    /// <inheritdoc/>
    public virtual bool SupportsStreaming => false;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            Logger.LogWarning("Channel {Name} is already running", Name);
            return;
        }

        Logger.LogInformation("Starting channel {Name}", DisplayName);
        await OnStartAsync(cancellationToken).ConfigureAwait(false);
        _isRunning = true;
        Logger.LogInformation("Channel {Name} started", DisplayName);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning) return;

        Logger.LogInformation("Stopping channel {Name}", DisplayName);
        await OnStopAsync(cancellationToken).ConfigureAwait(false);
        _isRunning = false;
        Logger.LogInformation("Channel {Name} stopped", DisplayName);
    }

    /// <inheritdoc/>
    public abstract Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public bool IsAllowed(string senderId)
    {
        if (AllowList.Count == 0) return true;
        return AllowList.Contains(senderId);
    }

    /// <summary>Override to start channel-specific services.</summary>
    protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    /// <summary>Override to stop channel-specific services.</summary>
    protected abstract Task OnStopAsync(CancellationToken cancellationToken);

    /// <summary>Publishes an inbound message to the message bus after checking allow-list.</summary>
    protected async ValueTask PublishMessageAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(message.SenderId))
        {
            Logger.LogDebug("Message from {SenderId} blocked by allow-list", message.SenderId);
            return;
        }
        await MessageBus.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
