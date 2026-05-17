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
        var gatewayUrl = configuration["GatewayUrl"] ?? "http://localhost:5005";

        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(gatewayUrl) });
        services.AddScoped<MobileState>();
        services.AddScoped<MobileGatewayClient>();

        return services;
    }
}
