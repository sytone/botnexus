using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>
/// Registers the self-contained mobile service layer.
/// No dependency on the desktop BlazorClient assembly.
/// </summary>
public static class MobileServiceExtensions
{
    public static IServiceCollection AddBotNexusMobileServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Do not set BaseAddress at registration time — the gateway URL is derived
        // at runtime from NavigationManager so it works across devtunnels and production.
        services.AddScoped(_ => new HttpClient());
        services.AddScoped<MobileState>();
        services.AddScoped<MobileGatewayClient>();

        return services;
    }
}
