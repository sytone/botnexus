namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// Backpressure and failure-isolation policy for the conversation event publication seam
/// (issue #2085). The defaults are chosen so a wedged channel extension degrades into dropped
/// projection events rather than a stalled agent loop.
/// </summary>
public sealed class ConversationEventPublisherOptions
{
    /// <summary>
    /// Maximum events buffered per conversation before shedding. Bounded because an unbounded
    /// queue behind a hung extension is a memory leak with extra steps. When the buffer is
    /// full the newest event is refused (publication returns <c>false</c>) rather than evicting
    /// an older one, so the surviving prefix stays contiguous and ordering stays meaningful.
    /// </summary>
    public int PerConversationCapacity { get; init; } = 1024;

    /// <summary>
    /// How long a single sink may take for one event before the publisher cancels its token and
    /// moves on. This is the guarantee that one slow extension cannot hold a conversation's
    /// ordered pump hostage.
    /// </summary>
    public TimeSpan SinkTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
