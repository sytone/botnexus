using System.Diagnostics;

namespace BotNexus.AgentCore.Diagnostics;

public static class AgentDiagnostics
{
    public const string SourceName = "BotNexus.Agents";

    public static readonly ActivitySource Source = new(SourceName);
}
