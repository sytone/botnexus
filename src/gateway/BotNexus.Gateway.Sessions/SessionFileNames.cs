namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Represents session file names.
/// </summary>
public static class SessionFileNames
{
    /// <summary>
    /// Executes sanitize session id.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The sanitize session id result.</returns>
    public static string SanitizeSessionId(string sessionId) => Uri.EscapeDataString(sessionId);

    /// <summary>
    /// Executes history file name.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The history file name result.</returns>
    public static string HistoryFileName(string sessionId) => $"{SanitizeSessionId(sessionId)}.jsonl";

    /// <summary>
    /// Executes metadata file name.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The metadata file name result.</returns>
    public static string MetadataFileName(string sessionId) => $"{SanitizeSessionId(sessionId)}.meta.json";
}
