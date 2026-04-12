using System.Text.Json.Serialization;

namespace BotNexus.Providers.Core.Models;

/// <summary>
/// Base content block for message content arrays.
/// Uses "type" discriminator for polymorphic JSON serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ToolCallContent), "toolCall")]
/// <summary>
/// Represents content block.
/// </summary>
public abstract record ContentBlock;

/// <summary>
/// Represents text content.
/// </summary>
public sealed record TextContent(
    string Text,
    string? TextSignature = null
) : ContentBlock;

/// <summary>
/// Represents thinking content.
/// </summary>
public sealed record ThinkingContent(
    string Thinking,
    string? ThinkingSignature = null,
    bool? Redacted = null
) : ContentBlock;

/// <summary>
/// Represents image content.
/// </summary>
public sealed record ImageContent(
    string Data,
    string MimeType
) : ContentBlock;

/// <summary>
/// Represents tool call content.
/// </summary>
public sealed record ToolCallContent(
    string Id,
    string Name,
    Dictionary<string, object?> Arguments,
    string? ThoughtSignature = null
) : ContentBlock;
