using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.Tui;

/// <summary>
/// Dependency-injection extensions for registering the Terminal UI channel adapter.
/// </summary>
public static class TuiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Terminal UI channel adapter stub.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Phase 2 stub registration for channel lifecycle and routing integration tests.
    /// </remarks>
    public static IServiceCollection AddBotNexusTuiChannel(this IServiceCollection services)
    {
        services.AddSingleton<IChannelAdapter, TuiChannelAdapter>();
        return services;
    }
}
