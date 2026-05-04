namespace BotNexus.Gateway.Abstractions.Channels;

/// <summary>
/// Thrown by a channel adapter's <c>SendAsync</c> when the target connection no longer
/// exists (e.g. a SignalR client that has disconnected). GatewayHost's fan-out loop
/// catches this to demote the binding to
/// <see cref="BotNexus.Gateway.Abstractions.Models.BindingMode.Muted"/>, preventing
/// silent delivery to dead connections on future fan-outs.
/// </summary>
public class StaleChannelConnectionException : Exception
{
    /// <summary>Gets the binding ID whose underlying connection is gone.</summary>
    public string BindingId { get; }

    /// <summary>Gets the conversation ID the binding belongs to.</summary>
    public string ConversationId { get; }

    /// <summary>
    /// Initialises a new instance describing a stale connection for a specific binding.
    /// </summary>
    public StaleChannelConnectionException(string bindingId, string conversationId, Exception? inner = null)
        : base($"Channel send failed — connection for binding {bindingId} in conversation {conversationId} is gone.", inner)
    {
        BindingId = bindingId;
        ConversationId = conversationId;
    }
}
