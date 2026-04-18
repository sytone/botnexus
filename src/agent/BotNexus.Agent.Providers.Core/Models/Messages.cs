using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.Core.Models;

/// <summary>
/// Base message type using "role" discriminator for polymorphic serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
/// <summary>
/// Represents message.
/// </summary>
public abstract record Message(long Timestamp);

/// <summary>
/// Represents user message.
/// </summary>
public sealed record UserMessage(
    UserMessageContent Content,
    long Timestamp
) : Message(Timestamp);

/// <summary>
/// Represents assistant message.
/// </summary>
public sealed record AssistantMessage(
    IReadOnlyList<ContentBlock> Content,
    string Api,
    string Provider,
    string ModelId,
    Usage Usage,
    StopReason StopReason,
    string? ErrorMessage,
    string? ResponseId,
    long Timestamp
) : Message(Timestamp);

/// <summary>
/// Represents tool result message.
/// </summary>
public sealed record ToolResultMessage(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<ContentBlock> Content,
    bool IsError,
    long Timestamp,
    object? Details = null
) : Message(Timestamp);
