namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>Portal-selected conversation export options.</summary>
public sealed record ConversationExportRequest(
    string Format,
    bool IncludeTools = true,
    bool IncludeThinking = false,
    bool IncludeSystemMessages = false,
    bool RedactSecrets = true);

/// <summary>Download payload returned by the gateway export route.</summary>
public sealed record ExportDownload(string FileName, string ContentType, byte[] Content);
