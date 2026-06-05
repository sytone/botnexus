namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Reports channel-side errors to the gateway for logging and diagnostics.
/// Any channel extension can implement this to report unhandled errors back
/// to the gateway — not specific to the Blazor portal.
/// </summary>
public interface IChannelErrorReporter
{
    /// <summary>
    /// Reports an error to the gateway diagnostics endpoint.
    /// Implementations must be best-effort: never throw.
    /// </summary>
    Task ReportAsync(ChannelErrorReportDto report, CancellationToken cancellationToken = default);
}
