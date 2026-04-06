using System.Diagnostics;

namespace BotNexus.Providers.Core.Diagnostics;

/// <summary>
/// Shared ActivitySource for BotNexus provider instrumentation.
/// </summary>
public static class ProviderDiagnostics
{
    public const string SourceName = "BotNexus.Providers";

    public static readonly ActivitySource Source = new(SourceName);
}
