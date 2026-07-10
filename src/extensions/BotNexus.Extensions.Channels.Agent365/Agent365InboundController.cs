using System.Text.Json;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Agents.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Maps the inbound Agents SDK message endpoint that receives Activity deliveries for the Agent 365
/// channel.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft 365 surfaces POST Activity JSON to the app's messaging endpoint. This contributor hosts
/// that endpoint (default <c>POST /agent365/messages</c>) and is the inbound counterpart to the
/// adapter's outbound connector: it deserializes the Activity, hands it to
/// <see cref="Agent365ChannelAdapter.HandleInboundActivityAsync"/> for translation and dispatch, and
/// returns 200 so the channel service considers the delivery acknowledged.
/// </para>
/// <para>
/// The Register tier keeps authentication of the inbound endpoint minimal; JWT bearer validation of
/// the channel-service token is layered in with the identity blueprint PBI (PBI3). The route is
/// configurable via <c>channels:agent365:inboundRoute</c>.
/// </para>
/// </remarks>
public sealed class Agent365InboundController : IEndpointContributor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void MapEndpoints(WebApplication app)
    {
        var options = app.Services.GetService<IOptions<Agent365GatewayOptions>>()?.Value;
        var route = string.IsNullOrWhiteSpace(options?.InboundRoute) ? "/agent365/messages" : options!.InboundRoute;

        app.MapPost(route, HandleAsync)
            .WithName("Agent365Messages")
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var logger = services.GetService<ILogger<Agent365InboundController>>();

        var adapter = services.GetServices<IChannelAdapter>()
            .OfType<Agent365ChannelAdapter>()
            .FirstOrDefault();

        if (adapter is null)
        {
            // Agent 365 channel not loaded — nothing to receive for.
            return Results.NotFound();
        }

        Activity? activity;
        try
        {
            activity = await JsonSerializer.DeserializeAsync<Activity>(request.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Agent 365 message endpoint received a malformed Activity body");
            return Results.BadRequest();
        }

        if (activity is null)
            return Results.BadRequest();

        await adapter.HandleInboundActivityAsync(activity, cancellationToken);

        // The channel service only needs an acknowledgement; the reply is delivered asynchronously
        // by the BotNexus loop via the outbound connector.
        return Results.Ok();
    }
}
