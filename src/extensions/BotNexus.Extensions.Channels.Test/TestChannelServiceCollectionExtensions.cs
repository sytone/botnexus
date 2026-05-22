using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Dependency-injection extensions for registering the test channel adapter.
/// </summary>
public static class TestChannelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the test channel adapter and its log capture provider.
    /// </summary>
    /// <remarks>
    /// This method is opt-in only. Never call it in production configurations.
    /// The test channel is designed exclusively for integration testing.
    /// </remarks>
    public static IServiceCollection AddBotNexusTestChannel(this IServiceCollection services)
    {
        services.AddSingleton<TestChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<TestChannelAdapter>());
        services.AddSingleton<ILoggerProvider>(sp =>
            new TestChannelLoggerProvider(sp.GetRequiredService<TestChannelAdapter>()));
        return services;
    }
}
