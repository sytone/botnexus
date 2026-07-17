using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Filters;
using BotNexus.Gateway.Api.Logging;
using BotNexus.Gateway.Api.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Api.Extensions;

/// <summary>
/// DI registration for the Gateway API layer - controllers, triggers, logging.
/// Channel extensions (SignalR, etc.) are loaded dynamically by the extension loader.
/// </summary>
public static class GatewayApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Gateway API services (controllers, triggers, logging).
    /// Call after <c>AddBotNexusGateway()</c>.
    /// </summary>
    public static IServiceCollection AddBotNexusGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<IRecentLogStore, InMemoryRecentLogStore>();
        services.AddSingleton<ILoggerProvider>(serviceProvider =>
            new RecentLogEntryLoggerProvider(serviceProvider.GetRequiredService<IRecentLogStore>()));
        services.AddSingleton<CronTrigger>();
        services.AddSingleton<CronSessionStartupReconciler>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CronSessionStartupReconciler>());
        services.AddSingleton<HeartbeatTrigger>();
        services.AddSingleton<SoulTrigger>();
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<CronTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<SoulTrigger>());
        services.AddSingleton<IInternalTrigger>(provider => provider.GetRequiredService<HeartbeatTrigger>());

        // Conversation history assembly is stateless (it only holds the conversation/session
        // stores, both singletons) so it is safe to register as a singleton. Registering it lets
        // the same assembled history view be reused by the SignalR/portal path; the controller
        // also has a constructor fallback so the endpoint works even without this registration.
        services.TryAddSingleton<IConversationHistoryAssembler, ConversationHistoryAssembler>();

        // Register the sparse-fieldset projection as a global result filter so every GET endpoint
        // honours ?fields=a,b,c without per-controller wiring (issue #1782). It is a no-op unless the
        // query parameter is present, keeping the default full-object response non-breaking.
        services.AddControllers(options => options.Filters.Add<SparseFieldsetResultFilter>())
            .AddApplicationPart(typeof(GatewayApiServiceCollectionExtensions).Assembly);

        return services;
    }
}
