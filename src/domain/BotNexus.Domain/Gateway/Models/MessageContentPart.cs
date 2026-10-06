namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Abstract base for content parts in multi-modal messages.
/// </summary>
public abstract record MessageContentPart
{
    /// <summary>
    /// MIME type of the content (e.g., "text/plain", "audio/wav", "image/png").
    /// </summary>
    public required string MimeType { get; init; }
}

/// <summary>
/// Text content part.
/// </summary>
public sealed record TextContentPart : MessageContentPart
{
    /// <summary>
    /// The text value.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Optional filename retained when textual content originated from an attachment.
    /// </summary>
    public string? FileName { get; init; }
}

/// <summary>
/// Binary content part for inline data (≤1MB).
/// </summary>
public sealed record BinaryContentPart : MessageContentPart
{
    /// <summary>
    /// Raw binary data.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Optional filename hint.
    /// </summary>
    public string? FileName { get; init; }
}

/// <summary>
/// Reference content part for large/external media (>1MB or pre-stored).
/// </summary>
public sealed record ReferenceContentPart : MessageContentPart
{
    /// <summary>
    /// URI to the content (file://, https://, blob://, etc.).
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Size in bytes (for display/validation).
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Optional filename hint.
    /// </summary>
    public string? FileName { get; init; }
}
