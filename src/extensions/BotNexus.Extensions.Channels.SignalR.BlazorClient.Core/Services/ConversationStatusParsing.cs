using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Parses server-supplied conversation lifecycle values into the canonical
/// <see cref="ConversationStatus"/> declaration.
/// </summary>
/// <remarks>
/// Parsing is deliberately tolerant and fails open to <see cref="ConversationStatus.Active"/>
/// (#3454). A client older than its server must not map a newly introduced status to archived and
/// silently empty the user's conversation list; an unexpectedly visible row is the safer failure.
/// </remarks>
public static class ConversationStatusParsing
{
    /// <summary>
    /// Parses a status case-insensitively. Unknown, empty, and absent values remain active.
    /// </summary>
    public static ConversationStatus Parse(string? value) =>
        Enum.TryParse<ConversationStatus>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : ConversationStatus.Active;

    /// <summary>Returns whether the tolerant status interpretation is active.</summary>
    public static bool IsActive(string? value) => Parse(value) is ConversationStatus.Active;
}
