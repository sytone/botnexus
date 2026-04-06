using BotNexus.Gateway.Abstractions.Channels;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelsController"/> class.
    /// </summary>
    /// <param name="channelManager">The channel adapter registry.</param>
    public ChannelsController(IChannelManager channelManager) => _channelManager = channelManager;

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
/// Channel adapter response payload.
/// </summary>
/// <param name="Name">The channel adapter name/type identifier.</param>
/// <param name="DisplayName">The human-readable channel display name.</param>
/// <param name="IsRunning">Whether the channel adapter is currently running.</param>
/// <param name="SupportsStreaming">Whether the adapter supports streamed deltas.</param>
/// <param name="SupportsSteering">Whether the adapter supports real-time steering.</param>
/// <param name="SupportsFollowUp">Whether the adapter supports follow-up controls.</param>
/// <param name="SupportsThinking">Whether the adapter supports thinking/progress rendering.</param>
/// <param name="SupportsToolDisplay">Whether the adapter supports tool activity display.</param>
public sealed record ChannelAdapterResponse(
    string Name,
    string DisplayName,
    bool IsRunning,
    bool SupportsStreaming,
    bool SupportsSteering,
    bool SupportsFollowUp,
    bool SupportsThinking,
    bool SupportsToolDisplay);
