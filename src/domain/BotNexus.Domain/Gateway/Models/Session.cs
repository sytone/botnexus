using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain session state without infrastructure concerns such as locks or replay buffering.
/// </summary>
public sealed record Session
{
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    public SessionId SessionId { get; set; }

    /// <summary>
    /// Gets or sets the agent id.
    /// </summary>
    public AgentId AgentId { get; set; }

    /// <summary>
    /// Gets or sets the channel type.
    /// </summary>
    public ChannelKey? ChannelType { get; set; }

    /// <summary>
    /// Gets or sets the session type.
    /// </summary>
    public SessionType SessionType { get; set; } = SessionType.UserAgent;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public SessionStatus Status { get; set; } = SessionStatus.Active;

    public bool IsInteractive => SessionType.Equals(SessionType.UserAgent);

    /// <summary>
    /// Gets or sets the participants.
    /// </summary>
    public List<SessionParticipant> Participants { get; set; } = [];

    /// <summary>
    /// Gets or sets the created at.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the updated at.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the expires at.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the conversation this session belongs to. Conversation is the
    /// durable parent container; sessions are bounded transcripts inside a conversation.
    /// The unset sentinel is <c>default(ConversationId)</c> — store implementations are
    /// responsible for backfilling unset values to the agent's legacy conversation
    /// before returning a session to callers (Phase 9 / P9-B; issues #615, #627).
    /// </summary>
    public ConversationId ConversationId { get; set; }

    /// <summary>
    /// Gets or sets the metadata.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; set; } = [];

    /// <summary>
    /// Gets or sets the history.
    /// </summary>
    public List<SessionEntry> History { get; set; } = [];

    public int MessageCount => History.Count;
}
