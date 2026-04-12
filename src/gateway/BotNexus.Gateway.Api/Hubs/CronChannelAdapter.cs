using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Hubs;

#pragma warning disable CS1591 // Channel adapter members follow base contracts

/// <summary>
/// Internal adapter used for cron-triggered sessions.
/// </summary>
public sealed class CronChannelAdapter(ILogger<CronChannelAdapter> logger)
    : ChannelAdapterBase(logger)
{
    public override ChannelKey ChannelType => ChannelKey.From("cron");
    public override string DisplayName => "Cron Scheduler";
    public override bool SupportsStreaming => false;
    public override bool SupportsSteering => false;
    public override bool SupportsFollowUp => false;
    public override bool SupportsThinkingDisplay => false;
    public override bool SupportsToolDisplay => false;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
