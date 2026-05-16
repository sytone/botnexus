using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Domain model for a conversation — a named, persistent grouping of one or more sessions
/// across potentially multiple channels.
/// </summary>
public sealed record Conversation
{
    /// <summary>Gets or sets the unique conversation identifier.</summary>
    public ConversationId ConversationId { get; set; }

    /// <summary>Gets or sets the agent that owns this conversation.</summary>
    public AgentId AgentId { get; set; }

    /// <summary>Gets or sets the human-readable title of this conversation.</summary>
    public string Title { get; set; } = "New conversation";

    /// <summary>Gets or sets the persisted description of this conversation's intent.</summary>
    public string? Purpose { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the agent's default conversation.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets the lifecycle status of this conversation.</summary>
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;

    /// <summary>Gets or sets when this conversation was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when this conversation was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the session currently active within this conversation, if any.</summary>
    public SessionId? ActiveSessionId { get; set; }

    /// <summary>Gets or sets the channel bindings that route messages into and out of this conversation.</summary>
    public List<ChannelBinding> ChannelBindings { get; set; } = [];

    /// <summary>Gets or sets arbitrary extension metadata for this conversation.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = [];
}
