using System.Diagnostics;

namespace BotNexus.Gateway.Diagnostics;

public static class GatewayDiagnostics
{
    public const string SourceName = "BotNexus.Gateway";

    public static readonly ActivitySource Source = new(SourceName);
}
