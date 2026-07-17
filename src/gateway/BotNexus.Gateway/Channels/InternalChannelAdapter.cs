using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Channel adapter for internal messages (sub-agent completions, cross-agent routing).
/// Resolves the target session's original channel and delegates message and stream delivery to that adapter.
/// </summary>
public sealed class InternalChannelAdapter : ChannelAdapterBase, IStreamEventChannelAdapter, IChannelDestinationResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionStore _sessionStore;

    public InternalChannelAdapter(
        IServiceProvider serviceProvider,
        ISessionStore sessionStore,
        ILogger<InternalChannelAdapter> logger) : base(logger)
    {
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
    }

    public override ChannelKey ChannelType => ChannelKey.From("internal");
    public override string DisplayName => "Internal";
    public override bool SupportsStreaming => true;
    public override bool SupportsSteering => false;
    public override bool SupportsFollowUp => false;
    public override bool SupportsThinkingDisplay => false;
    public override bool SupportsToolDisplay => false;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var targetAdapter = await ResolveTargetAdapterAsync(message, cancellationToken);
        if (targetAdapter is null)
        {
            Logger.LogWarning(
                "Internal adapter: no target channel resolved for session '{SessionId}'. Response saved to history but not delivered.",
                message.SessionId);
            return;
        }

        var remapped = message with { ChannelType = targetAdapter.ChannelType };
        await targetAdapter.SendAsync(remapped, cancellationToken);
    }

    public override async Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        var targetAdapter = await ResolveTargetAdapterForSessionAsync(target.SessionId, cancellationToken);
        if (targetAdapter is null)
            return;

        await targetAdapter.SendStreamDeltaAsync(target, delta, cancellationToken);
    }

    /// <summary>
    /// Routes structured stream events for internal wake-ups through the session's original channel adapter so
    /// lifecycle events (start/end, thinking, and tool notifications) are preserved when parent agents are resumed.
    /// If the target channel only supports plain deltas, content events degrade gracefully to delta forwarding.
    /// </summary>
    /// <param name="target">Typed stream target — the parent session and its originating address.</param>
    /// <param name="streamEvent">The structured stream event to deliver to the resolved channel.</param>
    /// <param name="cancellationToken">Cancellation token for the async send operation.</param>
    public async Task SendStreamEventAsync(
        ChannelStreamTarget target,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken = default)
    {
        var targetAdapter = await ResolveTargetAdapterForSessionAsync(target.SessionId, cancellationToken);
        if (targetAdapter is null)
        {
            Logger.LogWarning(
                "Internal adapter: no target channel resolved for session '{SessionId}'. Stream event '{EventType}' was not delivered.",
                target.SessionId,
                streamEvent.Type);
            return;
        }

        if (targetAdapter is IStreamEventChannelAdapter streamTarget)
        {
            await streamTarget.SendStreamEventAsync(target, streamEvent, cancellationToken);
            return;
        }

        if (streamEvent.Type == AgentStreamEventType.ContentDelta
            && streamEvent.ContentDelta is not null)
        {
            await targetAdapter.SendStreamDeltaAsync(target, streamEvent.ContentDelta, cancellationToken);
        }
    }

    private async Task<IChannelAdapter?> ResolveTargetAdapterAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(message.SessionId))
        {
            var session = await _sessionStore.GetAsync(SessionId.From(message.SessionId), cancellationToken);
            if (session?.ChannelType is { } channelType)
            {
                var adapter = GetChannelManager().Get(channelType);
                if (adapter is not null && !adapter.ChannelType.Equals(ChannelType))
                {
                    Logger.LogDebug(
                        "Internal adapter: routing to '{ChannelType}' for session '{SessionId}'",
                        channelType, message.SessionId);
                    return adapter;
                }
            }
        }

        var fallback = GetChannelManager().Get(ChannelKey.From("signalr"));
        if (fallback is not null && !fallback.ChannelType.Equals(ChannelType))
        {
            Logger.LogDebug("Internal adapter: falling back to 'signalr' for delivery");
            return fallback;
        }

        return null;
    }

    /// <summary>
    /// Exposes the session-resolved destination so the gateway can suppress observer fan-out
    /// that would otherwise target the same adapter and SignalR conversation group twice.
    /// </summary>
    public Task<IChannelAdapter?> ResolveStreamDestinationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
        => ResolveTargetAdapterForSessionAsync(sessionId, cancellationToken);

    private async Task<IChannelAdapter?> ResolveTargetAdapterForSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
            if (session?.ChannelType is { } channelType)
            {
                var adapter = GetChannelManager().Get(channelType);
                if (adapter is not null && !adapter.ChannelType.Equals(ChannelType))
                    return adapter;
            }
        }
        catch
        {
            // Best effort — fall through to signalr.
        }

        var fallback = GetChannelManager().Get(ChannelKey.From("signalr"));
        return fallback is not null && !fallback.ChannelType.Equals(ChannelType)
            ? fallback
            : null;
    }

    private IChannelManager GetChannelManager()
        => _serviceProvider.GetRequiredService<IChannelManager>();
}
