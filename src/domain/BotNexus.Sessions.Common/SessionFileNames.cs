namespace BotNexus.Sessions.Common;

public static class SessionFileNames
{
    public static string SanitizeSessionId(string sessionId) => Uri.EscapeDataString(sessionId);

    public static string HistoryFileName(string sessionId) => $"{SanitizeSessionId(sessionId)}.jsonl";

    public static string MetadataFileName(string sessionId) => $"{SanitizeSessionId(sessionId)}.meta.json";
}
