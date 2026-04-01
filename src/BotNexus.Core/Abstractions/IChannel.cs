using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for messaging channels (Telegram, Discord, Slack, etc.).</summary>
public interface IChannel
{
    /// <summary>Unique name identifier for the channel.</summary>
    string Name { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Whether the channel is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Whether the channel supports streaming delta updates.</summary>
    bool SupportsStreaming { get; }

    /// <summary>Starts the channel and begins listening for messages.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the channel.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a complete message to a chat.</summary>
    Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>Sends a streaming delta to a chat.</summary>
    Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the sender is allowed to use this channel.</summary>
    bool IsAllowed(string senderId);
}
