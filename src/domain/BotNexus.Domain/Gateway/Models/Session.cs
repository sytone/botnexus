using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain session state without infrastructure concerns such as locks or replay buffering.
/// </summary>
public sealed record Session
{
    public SessionId SessionId { get; set; }

    public AgentId AgentId { get; set; }

    public ChannelKey? ChannelType { get; set; }

    public SessionType SessionType { get; set; } = SessionType.UserAgent;

    public SessionStatus Status { get; set; } = SessionStatus.Active;

    public bool IsInteractive => SessionType.Equals(SessionType.UserAgent);

    public List<SessionParticipant> Participants { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ExpiresAt { get; set; }

    public Dictionary<string, object?> Metadata { get; set; } = [];

    public List<SessionEntry> History { get; set; } = [];

    public int MessageCount => History.Count;
}
