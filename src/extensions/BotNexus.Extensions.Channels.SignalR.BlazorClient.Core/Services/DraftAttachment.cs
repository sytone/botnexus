namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>Portable draft attachment contract shared by desktop today and future clients later.</summary>
public sealed record DraftAttachment(string FileName, string MimeType, string Base64Data, long Size);

/// <summary>Client-side limits that bound SignalR payload size and draft resource use.</summary>
public static class AttachmentLimits
{
    public const int MaxCount = 8;

    // SignalR's default gateway frame cap is 10 MB. Base64 adds roughly 33%, so the
    // raw draft must remain below 7.5 MB with room left for JSON metadata and text.
    public const long MaxFileBytes = 7 * 1024 * 1024;
    public const long MaxTotalBytes = 7 * 1024 * 1024;
}

/// <summary>Wire content part matching the existing SignalR media contract.</summary>
public sealed record MediaContentPartDto
{
    public required string MimeType { get; init; }
    public string? Base64Data { get; init; }
    public string? Text { get; init; }
    public string? FileName { get; init; }
}
