namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// DTO for media content parts sent via SignalR.
/// Binary data is base64-encoded for JSON transport.
/// </summary>
public sealed record MediaContentPartDto
{
    /// <summary>MIME type (e.g., "audio/wav", "image/png").</summary>
    public required string MimeType { get; init; }

    /// <summary>Base64-encoded binary data.</summary>
    public string? Base64Data { get; init; }

    /// <summary>Text content (for text parts).</summary>
    public string? Text { get; init; }

    /// <summary>Optional filename.</summary>
    public string? FileName { get; init; }
}
