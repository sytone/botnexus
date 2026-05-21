using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a user-input checkpoint emitted by an agent tool.
/// The gateway uses this contract to fan out interactive prompts across all
/// bindings on the active conversation while preserving session/agent context.
/// </summary>
public sealed record AskUserRequest
{
    /// <summary>Unique identifier for this ask-user request instance.</summary>
    public required string RequestId { get; init; }

    /// <summary>Conversation scope that can satisfy this request from any bound channel.</summary>
    public required ConversationId ConversationId { get; init; }

    /// <summary>Session currently blocked on this user response.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Agent that initiated the request, for channel decoration and auditing.</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>Prompt text presented to the user.</summary>
    public required string Prompt { get; init; }

    /// <summary>Input shape the channel should render for this request.</summary>
    public AskUserInputType InputType { get; init; } = AskUserInputType.FreeForm;

    /// <summary>Optional set of structured choices for choice-based input types.</summary>
    public IReadOnlyList<AskUserChoice>? Choices { get; init; }

    /// <summary>True when channels should allow selecting more than one choice.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>True when channels should allow entering custom free-form text.</summary>
    public bool AllowFreeForm { get; init; }

    /// <summary>Optional wait limit after which the request should auto-time out.</summary>
    public TimeSpan? Timeout { get; init; }
}
