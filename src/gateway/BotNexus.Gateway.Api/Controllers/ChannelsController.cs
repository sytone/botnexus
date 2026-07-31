using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Channels.Startup;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for inspecting registered channel adapters.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ChannelsController : ControllerBase
{
    private readonly IChannelManager _channelManager;
    private readonly ChannelStartupReport _startupReport;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelsController"/> class.
    /// </summary>
    /// <param name="channelManager">The channel adapter registry.</param>
    /// <param name="startupReport">The startup pass report used by the health endpoint.</param>
    public ChannelsController(IChannelManager channelManager, ChannelStartupReport startupReport)
    {
        _channelManager = channelManager;
        _startupReport = startupReport;
    }

    /// <summary>
    /// Reports how many configured channel adapters actually reached the started state, naming
    /// any that did not. Returns 200 with <c>status: ok</c> when every configured adapter
    /// started, or 503 with <c>status: degraded</c> and the per-adapter failure otherwise.
    /// </summary>
    /// <remarks>
    /// Clause 4 of #2447. The host previously logged "Gateway started with N channel adapter(s)"
    /// using the configured count even when an adapter had failed, so a dead channel was
    /// invisible without reading logs. Adapters absent from the startup report - because the
    /// startup pass has not completed - are reported as not-started rather than assumed healthy.
    /// </remarks>
    /// <returns>The configured/started counts plus any named failures.</returns>
    [HttpGet("health")]
    [ProducesResponseType(typeof(ChannelStartupHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ChannelStartupHealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ChannelStartupHealthResponse> Health()
    {
        var configured = _channelManager.Adapters
            .Select(adapter => adapter.ChannelType.ToString() ?? string.Empty)
            .ToArray();

        var outcomes = _startupReport.Outcomes
            .ToDictionary(outcome => outcome.ChannelType, StringComparer.OrdinalIgnoreCase);

        var failures = new List<ChannelStartFailure>();
        var startedCount = 0;

        foreach (var channelType in configured)
        {
            if (outcomes.TryGetValue(channelType, out var outcome) && outcome.Started)
            {
                startedCount++;
                continue;
            }

            failures.Add(outcome is null
                ? new ChannelStartFailure(channelType, "NotAttempted", "startup pass has not completed for this adapter", 0)
                : new ChannelStartFailure(
                    channelType,
                    outcome.FailureKind?.ToString() ?? "Unknown",
                    outcome.Error?.Message ?? "unknown start failure",
                    outcome.Attempts));
        }

        var response = new ChannelStartupHealthResponse(
            Status: failures.Count == 0 ? "ok" : "degraded",
            Configured: configured.Length,
            Started: startedCount,
            Failed: failures);

        return failures.Count == 0
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    /// <summary>
    /// Lists registered channel adapters and their capabilities.
    /// </summary>
    /// <returns>Registered channel adapters with runtime status and capability flags.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ChannelAdapterResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ChannelAdapterResponse>> List()
        => Ok(_channelManager.Adapters.Select(adapter => new ChannelAdapterResponse(
            adapter.ChannelType,
            adapter.DisplayName,
            adapter.IsRunning,
            adapter.SupportsStreaming,
            adapter.SupportsSteering,
            adapter.SupportsFollowUp,
            adapter.SupportsThinkingDisplay,
            adapter.SupportsToolDisplay)).ToArray());
}

/// <summary>
/// Channel startup health response payload.
/// </summary>
/// <param name="Status">"ok" when every configured adapter started, otherwise "degraded".</param>
/// <param name="Configured">Number of channel adapters configured on this gateway.</param>
/// <param name="Started">Number of those adapters that actually reached the started state.</param>
/// <param name="Failed">The adapters that did not start, named with their failure.</param>
public sealed record ChannelStartupHealthResponse(
    string Status,
    int Configured,
    int Started,
    IReadOnlyList<ChannelStartFailure> Failed);

/// <summary>
/// A configured channel adapter that did not reach the started state.
/// </summary>
/// <param name="ChannelType">The adapter's channel type identifier.</param>
/// <param name="FailureKind">Transient, Terminal, or NotAttempted.</param>
/// <param name="Error">The final error message.</param>
/// <param name="Attempts">How many start attempts were made.</param>
public sealed record ChannelStartFailure(
    string ChannelType,
    string FailureKind,
    string Error,
    int Attempts);

/// <summary>
/// Channel adapter response payload.
/// </summary>
/// <param name="Name">The channel adapter name/type identifier.</param>
/// <param name="DisplayName">The human-readable channel display name.</param>
/// <param name="IsRunning">Whether the channel adapter is currently running.</param>
/// <param name="SupportsStreaming">Whether the adapter supports streamed deltas.</param>
/// <param name="SupportsSteering">Whether the adapter supports real-time steering.</param>
/// <param name="SupportsFollowUp">Whether the adapter supports follow-up controls.</param>
/// <param name="SupportsThinkingDisplay">Whether the adapter supports thinking/progress rendering.</param>
/// <param name="SupportsToolDisplay">Whether the adapter supports tool activity display.</param>
public sealed record ChannelAdapterResponse(
    string Name,
    string DisplayName,
    bool IsRunning,
    bool SupportsStreaming,
    bool SupportsSteering,
    bool SupportsFollowUp,
    bool SupportsThinkingDisplay,
    bool SupportsToolDisplay);
