using System.Diagnostics;

namespace BotNexus.Channels.Core.Diagnostics;

public static class ChannelDiagnostics
{
    public const string SourceName = "BotNexus.Channels";

    public static readonly ActivitySource Source = new(SourceName);
}
