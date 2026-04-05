using BotNexus.Gateway.Api.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Api.Extensions;

/// <summary>
/// DI registration and endpoint mapping extensions for the Gateway API layer.
/// </summary>
public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gateway API services (controllers, WebSocket handler).
    /// Call after <c>AddBotNexusGateway()</c>.
    /// </summary>
    public static IServiceCollection AddBotNexusGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<GatewayWebSocketHandler>();
        services.AddControllers()
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }

    /// <summary>
    /// Maps the Gateway WebSocket endpoint at <c>/ws</c>.
    /// Call in the request pipeline after <c>UseWebSockets()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapBotNexusGatewayWebSocket(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/ws", async context =>
        {
            var handler = context.RequestServices.GetRequiredService<GatewayWebSocketHandler>();
            await handler.HandleAsync(context, context.RequestAborted);
        });

        return endpoints;
    }
}
