using System.Diagnostics;

namespace BotNexus.Gateway.Channels.Diagnostics;

/// <summary>
/// Represents channel diagnostics.
/// </summary>
public static class ChannelDiagnostics
{
    public const string SourceName = "BotNexus.Channels";

    public static readonly ActivitySource Source = new(SourceName);
}
